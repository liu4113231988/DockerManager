using System.Text;

namespace DockerManager.Wpf.Services;

/// <summary>The socket paths found on the server by <see cref="DockerSocketProbe"/>.</summary>
public sealed class DockerSocketProbeResult
{
    /// <summary>Sockets that exist and can be read/written by the SSH account.</summary>
    public IReadOnlyList<string> Usable { get; init; } = [];

    /// <summary>Sockets that exist but are not accessible to the SSH account.</summary>
    public IReadOnlyList<string> Denied { get; init; } = [];

    public bool Exists(string path) => Usable.Contains(path) || Denied.Contains(path);
}

/// <summary>
/// Tmds.Ssh reports a refused Unix-socket channel as
/// "Failed to open channel - ConnectFailed - open failed.", which is the same message for a missing
/// socket, a stopped daemon and a permission error. These helpers probe the host with a read-only
/// POSIX command and turn the result into something the user can act on.
/// </summary>
public static class DockerSocketProbe
{
    /// <summary>
    /// Common install locations: classic, rootless and snap/package layouts. Glob patterns are left
    /// unquoted so the remote shell expands them.
    /// </summary>
    public static IReadOnlyList<string> Candidates { get; } =
    [
        "/var/run/docker.sock",
        "/run/docker.sock",
        "/run/user/[0-9]*/docker.sock",
        "/run/user/[0-9]*/.docker/run/docker.sock",
        "/var/snap/docker/common/run/docker.sock",
        "/var/packages/Docker/var/run/docker.sock"
    ];

    /// <summary>Builds the probe. Only the configured path comes from user input, and it is quoted.</summary>
    public static string BuildCommand(string configured)
    {
        var targets = new List<string> { Quote(configured) };
        targets.AddRange(Candidates);
        return "for p in " + string.Join(' ', targets) + "; do " +
               "if [ -S \"$p\" ]; then " +
               "if [ -r \"$p\" ] && [ -w \"$p\" ]; then echo \"USABLE $p\"; else echo \"DENIED $p\"; fi; " +
               "fi; done 2>/dev/null";
    }

    /// <summary>POSIX single-quote escaping: the value can never break out of the quoted word.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public static DockerSocketProbeResult Parse(string? stdout)
    {
        var usable = new List<string>();
        var denied = new List<string>();
        foreach (var raw in (stdout ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("USABLE ", StringComparison.Ordinal)) Add(usable, line[7..]);
            else if (line.StartsWith("DENIED ", StringComparison.Ordinal)) Add(denied, line[7..]);
        }
        return new DockerSocketProbeResult { Usable = usable, Denied = denied };

        static void Add(List<string> paths, string value)
        {
            var path = value.Trim();
            if (path.StartsWith('/') && !paths.Contains(path)) paths.Add(path);
        }
    }

    /// <summary>True for the library's channel-open rejection (direct-streamlocal@openssh.com).</summary>
    public static bool IsChannelOpenFailure(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("Failed to open channel", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool IsAdministrativelyProhibited(string? serverError) =>
        serverError?.Contains("AdministrativelyProhibited", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The server does not implement OpenSSH's direct-streamlocal channel type at all.</summary>
    public static bool IsUnsupportedChannelType(string? serverError) =>
        serverError?.Contains("UnknownChannelType", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Explains why the socket could not be opened and what to change on the server.</summary>
    public static string BuildMessage(string configured, string host, string serverError, DockerSocketProbeResult? probe)
    {
        IReadOnlyList<string> usable = probe?.Usable ?? [];
        IReadOnlyList<string> denied = probe?.Denied ?? [];
        var otherUsable = usable.Where(p => p != configured).ToList();
        var otherDenied = denied.Where(p => p != configured).ToList();
        bool configuredUsable = usable.Contains(configured);
        bool configuredDenied = denied.Contains(configured);
        bool unsupported = IsUnsupportedChannelType(serverError);

        var text = new StringBuilder();
        text.AppendLine($"无法打开远端 Docker Socket：{configured}");
        text.AppendLine($"SSH 已成功登录 {host}，失败发生在服务器端打开 Socket 的环节，与账号密码、主机密钥无关。");
        text.AppendLine();
        text.AppendLine("服务器返回：" + serverError.Trim());
        text.AppendLine();

        if (probe is null)
            text.AppendLine("未能在服务器上执行检测命令（可能限制登录 shell 或禁止执行命令），请手工确认。");
        else
        {
            text.AppendLine("服务器上的检测结果：");
            if (configuredUsable && unsupported)
                text.AppendLine($"· {configured} 存在且当前账号可读写（说明 Docker 正在运行）。");
            else if (configuredUsable)
                text.AppendLine($"· {configured} 存在且当前账号可读写，但连接被拒绝（Docker 服务未运行）。");
            else if (configuredDenied) text.AppendLine($"· {configured} 存在，但当前账号没有读写权限。");
            else text.AppendLine($"· {configured} 不存在。");
            foreach (var path in otherUsable) text.AppendLine($"· 检测到可用的 Socket：{path}");
            foreach (var path in otherDenied) text.AppendLine($"· 检测到 Socket {path}，但当前账号无权访问。");
            if (!configuredUsable && !configuredDenied && otherUsable.Count == 0 && otherDenied.Count == 0)
                text.AppendLine("· 未找到任何 Docker Socket。");
        }
        text.AppendLine();

        var advice = new List<string>();
        if (unsupported)
            advice.Add("该服务器的 SSH 实现不支持 Unix Socket 转发扩展（direct-streamlocal@openssh.com），" +
                       "无法通过 SSH 直连 Docker：请改用 OpenSSH 官方 sshd，并确认中间没有会改写通道请求的 SSH 网关/代理。");
        else if (IsAdministrativelyProhibited(serverError))
            advice.Add("服务器的 sshd 禁止 Unix Socket 转发：在 /etc/ssh/sshd_config 中设置 " +
                       "AllowStreamLocalForwarding yes，重启 sshd 后重试。");
        if (probe is null)
        {
            advice.Add("在服务器上执行：ls -l " + configured);
            advice.Add("再执行：systemctl status docker");
        }
        else if (unsupported)
        {
            if (otherUsable.Count > 0)
                advice.Add("服务器上存在的 Socket：" + string.Join("、", otherUsable) + "（仅供参考，通道类型不受支持时仍然无法连接）。");
        }
        else if (configuredUsable)
            advice.Add("启动 Docker 后再连接：sudo systemctl start docker（或 service docker start）。");
        else if (configuredDenied)
            advice.Add("把账号加入 docker 组后重新登录：sudo usermod -aG docker <账号>；或改用有权限的账号（如 root）。");
        else if (otherUsable.Count > 0)
            advice.Add("在“编辑连接”里把 Socket 路径改成：" + string.Join("、", otherUsable) + "。");
        else
            advice.Add("确认该主机已安装并运行 Docker Engine，且 Docker 与 sshd 在同一台机器上（容器内的 sshd 看不到宿主机的 Socket）。");
        // The rootless hint is only useful when no suggestion already points at such a path.
        if (!advice.Any(line => line.Contains("/run/user/", StringComparison.Ordinal)))
            advice.Add("若使用 rootless Docker，Socket 通常位于 /run/user/<uid>/docker.sock，需要在连接配置中指定。");

        text.AppendLine("处理建议：");
        for (var i = 0; i < advice.Count; i++) text.AppendLine($"{i + 1}. {advice[i]}");
        return text.ToString().TrimEnd();
    }
}

/// <summary>A Docker socket that is reachable over SSH but cannot be opened on the server.</summary>
public sealed class DockerSocketException(string message) : Exception(message);
