using System.Text;
using System.Text.Json;
using DockerManager.Wpf.Models;
using DockerManager.Wpf.Services;

static void Expect(bool assertion, string message)
{
    if (!assertion) throw new Exception("FAIL " + message);
    Console.WriteLine("PASS " + message);
}

var ssh = new ConnectionProfile { Kind = ConnectionKind.DockerSsh, DockerHost = "ssh://tester@example.org:2222" };
var uri = DockerEndpoint.Parse(ssh);
Expect(uri.Host == "example.org" && uri.Port == 2222 && uri.UserInfo == "tester", "SSH endpoint parser");
Expect(DockerEndpoint.Port(uri) == 2222, "explicit port is kept");
var defaultPortUri = DockerEndpoint.Parse(new ConnectionProfile { Kind = ConnectionKind.DockerSsh, DockerHost = "ssh://tester@example.org" });
Expect(DockerEndpoint.Port(defaultPortUri) == 22, "omitted port falls back to 22 instead of -1");
Expect(DockerEndpoint.SocketPath(ssh) == "/var/run/docker.sock", "default Unix socket");
Expect(DockerEndpoint.ChooseVersion("1.55", "1.44") == "v1.44", "new Engine API negotiated");
Expect(DockerEndpoint.ChooseVersion("1.39", "1.24") == "v1.39", "old Engine API negotiated");
try
{
    DockerEndpoint.ChooseVersion("1.23", "1.20");
    throw new Exception("Old API should be rejected");
}
catch (NotSupportedException) { Console.WriteLine("PASS reject unsupported Engine API"); }
try
{
    DockerEndpoint.Parse(new ConnectionProfile { Kind = ConnectionKind.DockerSsh, DockerHost = "ssh://user:secret@example.org" });
    throw new Exception("SSH URI with embedded password should be rejected");
}
catch (ArgumentException) { Console.WriteLine("PASS reject plaintext password in URI"); }

using (var list = JsonDocument.Parse("""[{"Id":"abc123","Names":["/my-container"],"Image":"nginx:latest","State":"running","Status":"Up 5m","Labels":{"com.docker.compose.project":"demo","com.docker.compose.service":"web"}}]"""))
{
    var containers = DockerApiClient.ParseContainers(list.RootElement);
    Expect(containers.Count == 1 && containers[0].Name == "my-container", "parse Engine container list");
    Expect(containers[0].Project == "demo" && containers[0].Service == "web", "Compose labels");
}

static byte[] Frame(byte channel, byte[] payload)
{
    var header = new byte[8];
    header[0] = channel;
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)payload.Length);
    return [.. header, .. payload];
}

var splitUtf8 = Encoding.UTF8.GetBytes("中");
byte[] output = [.. Frame(1, [.. splitUtf8[..1]]), .. Frame(1, [.. splitUtf8[1..], (byte)'\n']),
    .. Frame(2, Encoding.UTF8.GetBytes("warning\n"))];
using (var memory = new MemoryStream(output))
{
    var lines = new List<string>();
    await DockerLogDecoder.ReadAsync(memory, false, lines.Add, CancellationToken.None);
    Expect(lines.SequenceEqual(["中", "[stderr] warning"]), "Docker mux logs and split UTF8 frames");
}
using (var memory = new MemoryStream(Encoding.UTF8.GetBytes("line-one\nline-two\n")))
{
    var lines = new List<string>();
    await DockerLogDecoder.ReadAsync(memory, true, lines.Add, CancellationToken.None);
    Expect(lines.SequenceEqual(["line-one", "line-two"]), "TTY unframed logs");
}

var path = Path.Combine(Path.GetTempPath(), $"dockermanager-test-{Guid.NewGuid():N}.yaml");
try
{
    File.WriteAllText(path, """
apiVersion: v1
kind: Config
current-context: demo
contexts:
  - name: demo
    context:
      cluster: remote
      user: demo-user
users:
  - name: demo-user
    user:
      exec:
        command: untrusted.exe
""");
    try
    {
        KubernetesApiClient.ValidateNoExternalCredentials(path, "demo");
        throw new Exception("exec authentication must be rejected");
    }
    catch (NotSupportedException) { Console.WriteLine("PASS reject external authentication executable"); }
    // Test each format independently: source raw strings can contain CRLF on Windows.
    // Never rely on replacing a line within another fixture.
    const string tokenConfig = """
apiVersion: v1
kind: Config
current-context: demo
contexts:
  - name: demo
    context:
      cluster: remote
      user: demo-user
users:
  - name: demo-user
    user:
      token: fake-test-token
""";
    foreach (var newline in new[] { "\n", "\r\n" })
    {
        var normalized = tokenConfig.Replace("\r\n", "\n").Replace("\n", newline);
        File.WriteAllText(path, normalized);
        KubernetesApiClient.ValidateNoExternalCredentials(path, "demo");
        Console.WriteLine($"PASS direct token configuration with {(newline == "\n" ? "LF" : "CRLF")} newlines");
    }
}
finally { File.Delete(path); }

// Host-key trust: first connection asks, later connections reuse a persistent pin.
// A key that conflicts with known_hosts must be recoverable, but only through an explicit decision.
var trustFile = Path.Combine(Path.GetTempPath(), $"dockermanager-trust-{Guid.NewGuid():N}.json");
static async Task ExpectRejected(Func<Task> action, string message)
{
    try { await action(); }
    catch (SshHostKeyRejectedException) { Console.WriteLine("PASS " + message); return; }
    throw new Exception("FAIL " + message);
}
try
{
    var trusted = new SshHostTrustStore(trustFile);
    var prompts = new List<HostKeyPrompt>();
    trusted.ConfirmNewHostKey = (prompt, _) =>
    {
        prompts.Add(prompt);
        // A different key record is the only prompt that may replace a stored fingerprint.
        return Task.FromResult(prompt.Issue == HostKeyIssue.FirstConnection);
    };
    Expect(await trusted.VerifyAsync("example.org", 2222, Tmds.Ssh.KnownHostResult.Unknown,
        "initial-key", CancellationToken.None), "first use is explicitly accepted");
    Expect(prompts.Count == 1 && prompts[0].Fingerprint == "SHA256:initial-key"
        && prompts[0].Issue == HostKeyIssue.FirstConnection, "dialog displays complete fingerprint");

    var reloaded = new SshHostTrustStore(trustFile)
    {
        ConfirmNewHostKey = (prompt, _) => { prompts.Add(prompt); return Task.FromResult(false); }
    };
    Expect(await reloaded.VerifyAsync("example.org", 2222, Tmds.Ssh.KnownHostResult.Unknown,
        "initial-key", CancellationToken.None), "saved host key persists across sessions");
    Expect(await reloaded.VerifyAsync("example.org", 2222, Tmds.Ssh.KnownHostResult.Changed,
        "initial-key", CancellationToken.None), "approved key wins over a stale known_hosts entry");
    Expect(await reloaded.VerifyAsync("example.org", 2222, Tmds.Ssh.KnownHostResult.Trusted,
        "initial-key", CancellationToken.None), "trusted host is never rejected");
    Expect(prompts.Count == 1, "pinned host needs no confirmation");

    await ExpectRejected(() => reloaded.VerifyAsync("example.org", 2222, Tmds.Ssh.KnownHostResult.Unknown,
        "changed-key", CancellationToken.None), "different fingerprint is refused after a decline");
    Expect(prompts.Count == 2 && prompts[1].Issue == HostKeyIssue.KnownHostKeyChanged,
        "conflicting key is flagged as a change");
    await ExpectRejected(() => reloaded.VerifyAsync("example.org", 2222, Tmds.Ssh.KnownHostResult.Revoked,
        "initial-key", CancellationToken.None), "revoked host key is always blocked");
    Expect(prompts.Count == 2, "revoked key never prompts");
    await ExpectRejected(async () => await reloaded.VerifyAsync("other.example.org", 22,
        Tmds.Ssh.KnownHostResult.Unknown, "other-key", CancellationToken.None, allowInteraction: false),
        "non-interactive first connection is rejected instead of hanging");

    // Recovery path: a rebuilt server key can be approved, replacing the stored fingerprint.
    var recoveryFile = Path.Combine(Path.GetTempPath(), $"dockermanager-recovery-{Guid.NewGuid():N}.json");
    try
    {
        var recovery = new SshHostTrustStore(recoveryFile);
        recovery.ConfirmNewHostKey = (_, _) => Task.FromResult(true);
        Expect(await recovery.VerifyAsync("example.org", 2222, Tmds.Ssh.KnownHostResult.Unknown,
            "old-key", CancellationToken.None), "seed original fingerprint");
        string? issued = null;
        recovery.ConfirmNewHostKey = (prompt, _) => { issued = prompt.Issue.ToString(); return Task.FromResult(true); };
        Expect(await recovery.VerifyAsync("example.org", 2222, Tmds.Ssh.KnownHostResult.Changed,
            "new-key", CancellationToken.None), "changed key can be approved after verification");
        Expect(issued == nameof(HostKeyIssue.KnownHostKeyChanged), "changed key is flagged as a change");
        var afterReplace = new SshHostTrustStore(recoveryFile);
        Expect(await afterReplace.VerifyAsync("example.org", 2222, Tmds.Ssh.KnownHostResult.Changed,
            "new-key", CancellationToken.None), "replacement fingerprint persists without prompting");
    }
    finally { if (File.Exists(recoveryFile)) File.Delete(recoveryFile); }

    var refuse = new SshHostTrustStore(Path.Combine(Path.GetTempPath(), $"dockermanager-refuse-{Guid.NewGuid():N}.json"));
    refuse.ConfirmNewHostKey = (_, _) => Task.FromResult(false);
    await ExpectRejected(() => refuse.VerifyAsync("other.example.org", 22, Tmds.Ssh.KnownHostResult.Unknown,
        "other-key", CancellationToken.None), "user refusal stops authentication");
    Expect(!File.Exists(refuse.FilePath), "refused fingerprint is never saved");
}
finally { if (File.Exists(trustFile)) File.Delete(trustFile); }

// Images, networks and volumes parsing.
using (var images = JsonDocument.Parse("""[{"Id":"sha256:abcdef1234567890","RepoTags":["nginx:latest","nginx:1.27"],"RepoDigests":["nginx@sha256:aaa"],"Size":1610612736,"Created":1735689600},{"Id":"sha256:999888777","RepoTags":[],"RepoDigests":[],"Size":512,"Created":0}]"""))
{
    var parsed = DockerApiClient.ParseImages(images.RootElement);
    Expect(parsed.Count == 2, "parse image list");
    Expect(parsed[0].Name == "nginx" && parsed[0].Tags == "nginx:latest, nginx:1.27", "image name and tags");
    Expect(parsed[0].Kind == ResourceKind.Image, "image resource kind");
    Expect(parsed[0].Size == "1.5 GB", "image size formatting: " + parsed[0].Size);
    Expect(parsed[0].Status.Length == 16 && parsed[0].Status.Contains('-'), "image created formatting");
    Expect(parsed[1].Name.Length > 0 && parsed[1].Tags == "<none>", "untagged image fallback");
}
using (var networks = JsonDocument.Parse("""[{"Name":"bridge","Id":"a1b2c3","Created":"2025-01-02T03:04:05.123456789Z","Scope":"local","Driver":"bridge"}]"""))
{
    var parsed = DockerApiClient.ParseNetworks(networks.RootElement);
    Expect(parsed.Count == 1 && parsed[0].Name == "bridge" && parsed[0].Driver == "bridge" && parsed[0].Scope == "local",
        "parse network list");
    Expect(parsed[0].Kind == ResourceKind.Network, "network resource kind");
}
using (var volumes = JsonDocument.Parse("""{"Volumes":[{"Name":"data","Driver":"local","Mountpoint":"/var/lib/docker/volumes/data/_data","CreatedAt":"2025-01-02T03:04:05Z"}],"Warnings":[]}"""))
{
    var parsed = DockerApiClient.ParseVolumes(volumes.RootElement.GetProperty("Volumes"));
    Expect(parsed.Count == 1 && parsed[0].Name == "data" && parsed[0].Mountpoint.EndsWith("_data"), "parse volume list");
    Expect(parsed[0].Kind == ResourceKind.Volume, "volume resource kind");
}
Expect(DockerApiClient.FormatSize(0) == "0 B" && DockerApiClient.FormatSize(2048) == "2 KB", "size formatting");

// Settings: a corrupted preferences file must never block startup.
var settingsFile = Path.Combine(Path.GetTempPath(), $"dockermanager-settings-{Guid.NewGuid():N}.json");
try
{
    var store = new SettingsStore(settingsFile);
    var defaults = store.Load();
    Expect(defaults.WindowWidth == 0 && defaults.LastLogDirectory.Length == 0, "default settings");
    store.Save(new AppSettings { WindowWidth = 1280, WindowHeight = 800, WindowLeft = 100, WindowTop = 50, LastLogDirectory = @"D:\logs" });
    var reloaded = new SettingsStore(settingsFile).Load();
    Expect(reloaded.WindowWidth == 1280 && reloaded.WindowHeight == 800 && reloaded.WindowLeft == 100
        && reloaded.WindowTop == 50 && reloaded.LastLogDirectory == @"D:\logs", "settings round trip");
    File.WriteAllText(settingsFile, "{ not json");
    var recovered = new SettingsStore(settingsFile).Load();
    Expect(recovered.WindowWidth == 0, "corrupted settings fall back to defaults");
}
finally { if (File.Exists(settingsFile)) File.Delete(settingsFile); }

// Remembered passwords are DPAPI-encrypted and never stored as plaintext.
var credFile = Path.Combine(Path.GetTempPath(), $"dockermanager-cred-{Guid.NewGuid():N}.json");
try
{
    var credentials = new CredentialStore(credFile);
    Expect(credentials.Get("root@10.0.0.1:22") is null, "no remembered password initially");
    credentials.Set("root@10.0.0.1:22", "s3cret-pw");
    Expect(File.ReadAllText(credFile).Contains("s3cret-pw") == false, "remembered password is not stored as plaintext");
    Expect(new CredentialStore(credFile).Get("root@10.0.0.1:22") == "s3cret-pw", "remembered password round trips");
    credentials.Set("root@10.0.0.1:22", "updated");
    Expect(new CredentialStore(credFile).Get("root@10.0.0.1:22") == "updated", "remembered password can be replaced");
    credentials.Remove("root@10.0.0.1:22");
    Expect(new CredentialStore(credFile).Get("root@10.0.0.1:22") is null, "removed password is gone");
    Expect(new CredentialStore(credFile).Get("other@host") is null, "unknown keys stay empty");
    File.WriteAllText(credFile, "{ not json");
    Expect(new CredentialStore(credFile).Get("root@10.0.0.1:22") is null, "corrupted store falls back to empty");
}
finally { if (File.Exists(credFile)) File.Delete(credFile); }

// Session pool: healthy connections are reused, broken or cancelled ones are discarded.
var pool = new ResourcePool<PooledFake>(TimeSpan.FromMinutes(5));
var created = 0;
PooledFake Connect(string name) => new(name) { Number = Interlocked.Increment(ref created) };
var first = await pool.RentAsync("docker:host", _ => Task.FromResult(Connect("a")), CancellationToken.None);
pool.Return("docker:host", first);
var reused = await pool.RentAsync("docker:host", _ => Task.FromResult(Connect("b")), CancellationToken.None);
Expect(ReferenceEquals(first, reused), "healthy session is reused");

var broken = new PooledFake("broken") { Usable = false };
pool.Return("docker:host", broken);
var replaced = await pool.RentAsync("docker:host", _ => Task.FromResult(Connect("c")), CancellationToken.None);
Expect(!ReferenceEquals(broken, replaced), "broken session is discarded");
Expect(broken.Disposed, "broken session is disposed");

var isolated = await pool.RentAsync("docker:other", _ => Task.FromResult(Connect("d")), CancellationToken.None);
Expect(!ReferenceEquals(replaced, isolated), "different connections stay isolated");
pool.Return("docker:host", replaced);
pool.Return("docker:other", isolated);

var idlePool = new ResourcePool<PooledFake>(TimeSpan.FromMilliseconds(1));
var idle = await idlePool.RentAsync("k", _ => Task.FromResult(new PooledFake("idle")), CancellationToken.None);
idlePool.Return("k", idle);
await Task.Delay(30);
var fresh = await idlePool.RentAsync("k", _ => Task.FromResult(new PooledFake("fresh")), CancellationToken.None);
Expect(!ReferenceEquals(idle, fresh), "idle session is recycled");
Expect(idle.Disposed, "recycled session is disposed");
pool.Clear();
idlePool.Clear();

// Remote socket diagnosis: the SSH channel error is identical for a missing socket,
// a stopped daemon and a permission error, so it must be explained from a probe.
Expect(DockerSocketProbe.IsChannelOpenFailure(new Exception("Failed to open channel - ConnectFailed - open failed.")),
    "channel open failure is recognized");
Expect(!DockerSocketProbe.IsChannelOpenFailure(new Exception("AuthenticationFailed")),
    "other connection failures are not socket channel failures");
Expect(DockerSocketProbe.IsChannelOpenFailure(new InvalidOperationException("wrap",
        new Exception("Failed to open channel - ConnectFailed - open failed."))),
    "wrapped channel failure is recognized");
try
{
    DockerEndpoint.SocketPath(new ConnectionProfile { Kind = ConnectionKind.DockerSsh, DockerSocketPath = "var/run/docker.sock" });
    throw new Exception("relative socket path should be rejected");
}
catch (ArgumentException) { Console.WriteLine("PASS reject relative socket path"); }

var probeCommand = DockerSocketProbe.BuildCommand("/opt/odd path's.sock");
Expect(probeCommand.Contains("'/opt/odd path'\\''s.sock'"), "configured socket path is shell-quoted");
Expect(probeCommand.Contains("/run/user/[0-9]*/docker.sock"), "probe covers rootless sockets and keeps globs unquoted");
Expect(DockerSocketProbe.Parse("USABLE /var/run/docker.sock\nDENIED /run/user/1000/docker.sock\nnoise\n" +
        "USABLE /var/run/docker.sock\n").Usable.SequenceEqual(["/var/run/docker.sock"]),
    "probe output lists usable sockets once and ignores noise");
Expect(DockerSocketProbe.Parse("DENIED /run/user/1000/docker.sock").Denied.SequenceEqual(["/run/user/1000/docker.sock"]),
    "probe output lists denied sockets");

var missingSocket = DockerSocketProbe.BuildMessage("/var/run/docker.sock", "10.1.2.3",
    "Failed to open channel - ConnectFailed - open failed.",
    new DockerSocketProbeResult { Usable = ["/run/user/1000/docker.sock"] });
Expect(missingSocket.Contains("/var/run/docker.sock") && missingSocket.Contains("10.1.2.3"),
    "socket message names the path and the host");
Expect(missingSocket.Contains("不存在") && missingSocket.Contains("/run/user/1000/docker.sock"),
    "socket message points at the discovered socket");

Expect(DockerSocketProbe.BuildMessage("/var/run/docker.sock", "h",
        "Failed to open channel - ConnectFailed - open failed.",
        new DockerSocketProbeResult { Denied = ["/var/run/docker.sock"] })
        .Contains("usermod -aG docker"), "permission problem suggests the docker group");
Expect(DockerSocketProbe.BuildMessage("/var/run/docker.sock", "h",
        "Failed to open channel - ConnectFailed - open failed.",
        new DockerSocketProbeResult { Usable = ["/var/run/docker.sock"] })
        .Contains("systemctl start docker"), "stopped daemon suggests starting Docker");
var prohibited = DockerSocketProbe.BuildMessage("/var/run/docker.sock", "h",
    "Failed to open channel - AdministrativelyProhibited - open failed.", null);
Expect(prohibited.Contains("AllowStreamLocalForwarding"), "disallowed forwarding explains sshd_config");
Expect(prohibited.Contains("ls -l /var/run/docker.sock"), "unavailable probe falls back to manual commands");
Expect(DockerSocketProbe.BuildMessage("/var/run/docker.sock", "h",
        "Failed to open channel - UnknownChannelType - unknown channel type.", null)
        .Contains("direct-streamlocal@openssh.com"), "unsupported channel type is explained");

Console.WriteLine("All DockerManager managed-API smoke tests passed.");

sealed class PooledFake(string name) : IPooledResource, IDisposable
{
    public int Number { get; init; }
    public bool Usable { get; set; } = true;
    public bool Disposed { get; private set; }
    public bool IsUsable => Usable && !Disposed;
    public void Dispose() => Disposed = true;
}
