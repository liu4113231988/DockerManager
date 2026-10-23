using DockerManager.Wpf.Models;

namespace DockerManager.Wpf.Services;

/// <summary>Connection settings are owned by DockerManager, not by the Docker CLI/context store.</summary>
public static class DockerEndpoint
{
    public static Uri Parse(ConnectionProfile profile)
    {
        if (profile.Kind != ConnectionKind.DockerSsh)
            throw new InvalidOperationException("请使用 Docker SSH 连接；旧 Docker Context 依赖 CLI，不能用于免依赖模式。请重新添加 SSH 连接。");
        if (!Uri.TryCreate(profile.DockerHost, UriKind.Absolute, out var uri) ||
            uri.Scheme != "ssh" || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.UserInfo.Length == 0 || uri.UserInfo.Contains(':') ||
            uri.AbsolutePath is not ("" or "/") ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("SSH 连接地址必须为 ssh://用户名@主机[:端口]，不允许密码、路径或参数。");
        return uri;
    }

    /// <summary>The ssh scheme has no registered default port, so an omitted port parses as -1.</summary>
    public static int Port(Uri uri) => uri.IsDefaultPort || uri.Port < 0 ? 22 : uri.Port;

    public static string SocketPath(ConnectionProfile profile)
    {
        var path = string.IsNullOrWhiteSpace(profile.DockerSocketPath)
            ? "/var/run/docker.sock" : profile.DockerSocketPath.Trim();
        if (!path.StartsWith('/') || path.Contains('\0') || path.Contains('\r') || path.Contains('\n'))
            throw new ArgumentException("远端 Docker Socket 必须填写 Linux 绝对路径。");
        return path;
    }

    public static string ApiPath(string id) => Uri.EscapeDataString(id);
    public static string QueryBool(bool value) => value ? "true" : "false";

    /// <summary>Use the highest API version that is understood by the MVP and accepted by the server.</summary>
    public static string ChooseVersion(string? serverMax, string? serverMin)
    {
        static int Minor(string? v)
        {
            var parts = (v ?? "").Split('.');
            return parts.Length == 2 && parts[0] == "1" && int.TryParse(parts[1], out var number)
                ? number : throw new InvalidOperationException($"无法识别 Docker API 版本：{v}");
        }
        var highest = Math.Min(Minor(serverMax), 44);
        var minimum = string.IsNullOrWhiteSpace(serverMin) ? 24 : Minor(serverMin);
        if (highest < Math.Max(minimum, 24))
            throw new NotSupportedException($"Docker Engine API {serverMin}～{serverMax} 与本程序支持的 1.24～1.44 不兼容。");
        return $"v1.{highest}";
    }
}
