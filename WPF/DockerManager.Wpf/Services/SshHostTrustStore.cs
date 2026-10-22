using System.IO;
using System.Text.Json;
using Tmds.Ssh;

namespace DockerManager.Wpf.Services;

/// <summary>Why the user is being asked about a server key.</summary>
public enum HostKeyIssue
{
    /// <summary>No record exists for this host, neither in the app store nor in known_hosts.</summary>
    FirstConnection,

    /// <summary>A different key was recorded before (app pin or OpenSSH known_hosts).</summary>
    KnownHostKeyChanged
}

/// <summary>Everything the UI needs to describe a host-key decision to the user.</summary>
public sealed record HostKeyPrompt(string Host, int Port, string Fingerprint, HostKeyIssue Issue);

/// <summary>
/// Raised when a server key cannot be accepted. The message is user-facing and explains how to recover.
/// </summary>
public sealed class SshHostKeyRejectedException(string message) : Exception(message);

/// <summary>
/// App-local SSH host-key pinning. Unknown hosts require an explicit user decision.
/// A key that differs from the stored/known record is never accepted silently, but the user
/// can approve a replacement after out-of-band verification (server rebuild, regenerated keys).
/// Revoked keys are always rejected.
/// </summary>
public sealed class SshHostTrustStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath { get; }

    /// <summary>Called when the current key is not yet approved; the UI must request out-of-band verification.</summary>
    public Func<HostKeyPrompt, CancellationToken, Task<bool>>? ConfirmNewHostKey { get; set; }

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
        if (result == KnownHostResult.Revoked)
            throw Rejected(host, port, fingerprint, "该主机密钥已被 known_hosts 标记为撤销（@revoked）");
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw Rejected(host, port, "(未知)", "服务器没有提供可用于校验的主机密钥");
        fingerprint = fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? fingerprint[7..] : fingerprint;

        await _gate.WaitAsync(ct);
        try
        {
            var hosts = Load();
            var key = HostId(host, port);
            bool hasPin = hosts.TryGetValue(key, out var pinned);

            // An explicitly approved key stays approved even when known_hosts still holds an older
            // entry for the same host (Changed); otherwise every reconnect would re-prompt.
            if (hasPin && string.Equals(pinned, fingerprint, StringComparison.Ordinal))
                return true;

            if (!hasPin && result == KnownHostResult.Trusted)
            {
                // The OS/OpenSSH trust store has already authenticated this host.
                hosts[key] = fingerprint;
                Save(hosts);
                return true;
            }

            var issue = hasPin || result == KnownHostResult.Changed
                ? HostKeyIssue.KnownHostKeyChanged : HostKeyIssue.FirstConnection;
            var reason = hasPin
                ? "本程序保存的指纹与服务器当前指纹不同"
                : result == KnownHostResult.Changed
                    ? "本机 OpenSSH known_hosts 中记录的密钥与服务器当前密钥不同"
                    : "首次连接，尚无该主机的密钥记录";

            if (!allowInteraction || ConfirmNewHostKey is null)
                throw Rejected(host, port, fingerprint, reason + "，且当前无法交互确认");

            ct.ThrowIfCancellationRequested();
            if (!await ConfirmNewHostKey(new HostKeyPrompt(host, port, "SHA256:" + fingerprint, issue), ct))
                throw Rejected(host, port, fingerprint, reason + "，用户已取消连接");

            ct.ThrowIfCancellationRequested();
            hosts[key] = fingerprint;
            Save(hosts);
            return true;
        }
        finally { _gate.Release(); }
    }

    private static SshHostKeyRejectedException Rejected(string host, int port, string fingerprint, string reason)
    {
        var text = fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase) ? fingerprint : "SHA256:" + fingerprint;
        return new SshHostKeyRejectedException(
            $"SSH 主机密钥未通过校验，已拒绝连接。\n\n" +
            $"服务器：{host}:{port}\n" +
            $"服务器当前指纹：{text}\n" +
            $"原因：{reason}\n\n" +
            "如果服务器确实重装过或更换了主机密钥，请先在服务器控制台执行\n" +
            "ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub\n" +
            "核对指纹与本机显示的一致后重新连接，并在提示中选择“是”。\n" +
            $"OpenSSH 的旧记录可用 ssh-keygen -R {host} 清除（可选，只影响命令行 ssh）。");
    }
}
