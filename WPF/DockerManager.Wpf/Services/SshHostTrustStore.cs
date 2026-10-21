using System.IO;
using System.Text.Json;
using Tmds.Ssh;

namespace DockerManager.Wpf.Services;

/// <summary>
/// App-local SSH host-key pinning. Unknown hosts require an explicit user decision.
/// Changed or revoked keys are rejected even if they were previously accepted.
/// </summary>
public sealed class SshHostTrustStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath { get; }

    /// <summary>Called only for a previously unknown host; UI must request out-of-band fingerprint verification.</summary>
    public Func<string, int, string, CancellationToken, Task<bool>>? ConfirmNewHostKey { get; set; }

    public SshHostTrustStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DockerManager", "trusted-ssh-hosts.json");
    }

    private static string HostId(string host, int port) => $"[{host.ToLowerInvariant()}]:{port}";

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(FilePath)) return new Dictionary<string, string>(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException("SSH 主机信任记录无效，请检查 trusted-ssh-hosts.json。");
    }

    private void Save(Dictionary<string, string> hosts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(hosts, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, overwrite: true);
    }

    public async Task<bool> VerifyAsync(string host, int port, KnownHostResult result,
        string fingerprint, CancellationToken ct, bool allowInteraction = true)
    {
        // Never override a revoked or changed known_hosts entry with a UI confirmation.
        if (result is KnownHostResult.Changed or KnownHostResult.Revoked) return false;
        if (string.IsNullOrWhiteSpace(fingerprint)) return false;
        fingerprint = fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? fingerprint[7..] : fingerprint;

        await _gate.WaitAsync(ct);
        try
        {
            var hosts = Load();
            var key = HostId(host, port);
            if (hosts.TryGetValue(key, out var pinned))
            {
                // A different server key is never automatically accepted, even if the
                // other key was added to the OS known_hosts file.
                return string.Equals(pinned, fingerprint, StringComparison.Ordinal);
            }

            if (result == KnownHostResult.Trusted)
            {
                // The OS/OpenSSH trust store has already authenticated this host.
                hosts[key] = fingerprint;
                Save(hosts);
                return true;
            }

            if (result != KnownHostResult.Unknown || !allowInteraction || ConfirmNewHostKey is null) return false;
            if (!await ConfirmNewHostKey(host, port, "SHA256:" + fingerprint, ct)) return false;
            ct.ThrowIfCancellationRequested();
            hosts[key] = fingerprint;
            Save(hosts);
            return true;
        }
        finally { _gate.Release(); }
    }
}
