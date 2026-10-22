using System.Net;
using System.Net.Http;
using System.Text.Json;
using DockerManager.Wpf.Models;
using Tmds.Ssh;

namespace DockerManager.Wpf.Services;

/// <summary>Direct SSH Unix-socket transport to Docker Engine REST API; no docker.exe or ssh.exe.</summary>
public sealed class DockerApiClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan SessionIdleLimit = TimeSpan.FromMinutes(5);

    private readonly SshHostTrustStore _trustStore = new();
    private readonly ResourcePool<Session> _sessions = new(SessionIdleLimit);

    public Func<HostKeyPrompt, CancellationToken, Task<bool>>? ConfirmNewHostKey
    {
        get => _trustStore.ConfirmNewHostKey;
        set => _trustStore.ConfirmNewHostKey = value;
    }

    public void Dispose() => _sessions.Clear();

    // A healthy session is reused, so a Compose refresh (projects + containers) or a log
    // request (inspect + logs) performs a single SSH handshake instead of one per call.
    private async Task<Session> RentAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var endpoint = DockerEndpoint.Parse(profile);
        var key = $"{Uri.UnescapeDataString(endpoint.UserInfo).ToLowerInvariant()}@{endpoint.Host}:{endpoint.Port}" +
                  $"/{DockerEndpoint.SocketPath(profile)}";
        return await _sessions.RentAsync(key, token => ConnectAsync(profile, key, token), ct);
    }

    private void Return(Session session, CancellationToken ct)
    {
        // A cancelled operation may leave the tunnel half-used; never hand such a session back.
        if (ct.IsCancellationRequested) session.Fault();
        _sessions.Return(session.Key, session);
    }

    private async Task<Session> ConnectAsync(ConnectionProfile profile, string key, CancellationToken ct)
    {
        var endpoint = DockerEndpoint.Parse(profile);
        var destination = $"{Uri.UnescapeDataString(endpoint.UserInfo)}@{endpoint.Host}" +
                          (endpoint.IsDefaultPort ? "" : $":{endpoint.Port}");
        HostAuthentication verify = async (context, token) =>
        {
            // Tmds.Ssh 0.26.0 uses HostAuthenticationContext + CancellationToken.
            // Prompt only for an interactive initial connection, never during rekey.
            bool interactive = !context.IsNonInteractive;
            if (interactive) context.SuspendConnectTimeout();
            try
            {
                return await _trustStore.VerifyAsync(
                    endpoint.Host, endpoint.Port, context.KnownHostResult,
                    context.ConnectionInfo.ServerKey.Key.SHA256FingerPrint,
                    token, allowInteraction: interactive);
            }
            finally
            {
                if (interactive) context.ResumeConnectTimeout();
            }
        };
        // Read the OpenSSH user configuration (~/.ssh/config) so IdentityFile, Port, ProxyJump and
        // StrictHostKeyChecking behave like the ssh command line. Missing files are ignored.
        var config = new SshConfigSettings { HostAuthentication = verify, AutoConnect = false };
        var ssh = new SshClient(destination, config);
        try
        {
            await ssh.ConnectAsync(ct);
            var path = DockerEndpoint.SocketPath(profile);
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                MaxConnectionsPerServer = 6,
                ConnectCallback = async (_, cancel) => await ssh.OpenUnixConnectionAsync(path, cancel)
            };
            var http = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://docker.internal/"),
                Timeout = Timeout.InfiniteTimeSpan // Streaming requests must not time out after 100 seconds.
            };
            var session = new Session(ssh, http) { Key = key };
            try
            {
                using var version = await session.RequestAsync(HttpMethod.Get, "version", ct);
                using var json = await JsonDocument.ParseAsync(await version.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var root = json.RootElement;
                session.ApiVersion = DockerEndpoint.ChooseVersion(
                    root.GetProperty("ApiVersion").GetString(),
                    root.TryGetProperty("MinAPIVersion", out var minimum) ? minimum.GetString() : null);
                session.ServerVersion = root.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "";
                return session;
            }
            catch { session.Dispose(); throw; }
        }
        catch { ssh.Dispose(); throw; }
    }

    private sealed class Session(SshClient ssh, HttpClient http) : IDisposable, IPooledResource
    {
        public string Key { get; init; } = "";
        public string ApiVersion { get; set; } = "";
        public string ServerVersion { get; set; } = "";

        private bool _disposed;
        private bool _faulted;

        public bool IsUsable => !_disposed && !_faulted;

        public void Fault() => _faulted = true;

        public async Task<HttpResponseMessage> RequestAsync(HttpMethod verb, string path, CancellationToken ct, bool stream = false)
        {
            using var request = new HttpRequestMessage(verb, path);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!stream) timeout.CancelAfter(RequestTimeout);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch
            {
                // A failed request usually means the tunnel or the Engine went away.
                _faulted = true;
                throw;
            }
            if (!response.IsSuccessStatusCode)
            {
                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException($"Docker API {(int)response.StatusCode} {response.ReasonPhrase}: {body[..Math.Min(body.Length, 1200)]}");
                }
            }
            return response;
        }
        public void Dispose() { if (_disposed) return; _disposed = true; http.Dispose(); ssh.Dispose(); }
    }

    public async Task<string> TestAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var session = await RentAsync(profile, ct);
        try
        {
            using var response = await session.RequestAsync(HttpMethod.Get, $"{session.ApiVersion}/_ping", ct);
            return $"Docker Engine {session.ServerVersion} · API {session.ApiVersion} · {await response.Content.ReadAsStringAsync(ct)}";
        }
        finally { Return(session, ct); }
    }

    public async Task<IReadOnlyList<WorkloadItem>> ContainersAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var session = await RentAsync(profile, ct);
        try
        {
            using var response = await session.RequestAsync(HttpMethod.Get, $"{session.ApiVersion}/containers/json?all=1&size=0", ct);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return ParseContainers(json.RootElement);
        }
        finally { Return(session, ct); }
    }

    public static IReadOnlyList<WorkloadItem> ParseContainers(JsonElement array)
    {
        var result = new List<WorkloadItem>();
        foreach (var value in array.EnumerateArray())
        {
            var names = value.TryGetProperty("Names", out var allNames) && allNames.GetArrayLength() > 0
                ? allNames[0].GetString() ?? "" : "";
            var labels = value.TryGetProperty("Labels", out var lab) ? lab : default;
            string Label(string name) => labels.ValueKind == JsonValueKind.Object &&
                labels.TryGetProperty(name, out var field) ? field.GetString() ?? "" : "";
            result.Add(new WorkloadItem
            {
                Id = Get(value, "Id"), Name = names.TrimStart('/'), Image = Get(value, "Image"),
                State = Get(value, "State"), Status = Get(value, "Status"),
                Project = Label("com.docker.compose.project"), Service = Label("com.docker.compose.service")
            });
        }
        return result;
    }
    private static string Get(JsonElement value, string key) => value.TryGetProperty(key, out var field) ? field.ToString() : "";

    public async Task<string> InspectAsync(ConnectionProfile profile, string id, CancellationToken ct)
    {
        var session = await RentAsync(profile, ct);
        try
        {
            using var response = await session.RequestAsync(HttpMethod.Get,
                $"{session.ApiVersion}/containers/{DockerEndpoint.ApiPath(id)}/json", ct);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        finally { Return(session, ct); }
    }

    public async Task ActionAsync(ConnectionProfile profile, string id, string action, CancellationToken ct)
    {
        if (action is not ("start" or "stop" or "restart")) throw new ArgumentOutOfRangeException(nameof(action));
        var session = await RentAsync(profile, ct);
        try
        {
            using var response = await session.RequestAsync(HttpMethod.Post,
                $"{session.ApiVersion}/containers/{DockerEndpoint.ApiPath(id)}/{action}" +
                (action is "stop" or "restart" ? "?t=10" : ""), ct);
        }
        finally { Return(session, ct); }
    }

    public async Task LogsAsync(ConnectionProfile profile, string id, int tail, bool follow, Action<string> onLine, CancellationToken ct)
    {
        var session = await RentAsync(profile, ct);
        try
        {
            // Engine log responses can be raw UTF-8 (TTY) or Docker's multiplexed stdout/stderr format.
            using var inspect = await session.RequestAsync(HttpMethod.Get,
                $"{session.ApiVersion}/containers/{DockerEndpoint.ApiPath(id)}/json", ct);
            using var json = await JsonDocument.ParseAsync(await inspect.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            bool tty = json.RootElement.GetProperty("Config").TryGetProperty("Tty", out var flag) && flag.GetBoolean();
            var path = $"{session.ApiVersion}/containers/{DockerEndpoint.ApiPath(id)}/logs" +
                $"?stdout=1&stderr=1&timestamps=1&follow={DockerEndpoint.QueryBool(follow)}&tail={tail}";
            using var response = await session.RequestAsync(HttpMethod.Get, path, ct, stream: true);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await DockerLogDecoder.ReadAsync(stream, tty, onLine, ct);
        }
        finally { Return(session, ct); }
    }
}
