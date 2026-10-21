using System.IO;
using System.Text.Json;
using DockerManager.Wpf.Models;
using k8s;
using YamlDotNet.RepresentationModel;

namespace DockerManager.Wpf.Services;

/// <summary>Kubernetes official .NET client. Deliberately blocks kubeconfig exec authentication.</summary>
public sealed class KubernetesApiClient
{
    private static Kubernetes Open(ConnectionProfile profile)
    {
        var path = string.IsNullOrWhiteSpace(profile.Kubeconfig)
            ? KubernetesClientConfiguration.KubeConfigDefaultLocation :
              Environment.ExpandEnvironmentVariables(profile.Kubeconfig.Trim());
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 kubeconfig，请在连接设置中指定文件路径。", path);
        var context = string.IsNullOrWhiteSpace(profile.Context) ? null : profile.Context.Trim();
        ValidateNoExternalCredentials(path, context);
        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(path, currentContext: context);
        if (config.SkipTlsVerify)
            throw new InvalidOperationException("kubeconfig 已配置 insecure-skip-tls-verify。本客户端不允许跳过 API Server 证书验证。");
        return new Kubernetes(config);
    }

    /// <summary>External exec/auth-provider commands violate the no-external-CLI guarantee.</summary>
    public static void ValidateNoExternalCredentials(string path, string? context)
    {
        using var reader = File.OpenText(path);
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("kubeconfig YAML 结构不正确。");
        static YamlNode? Field(YamlMappingNode map, string name) =>
            map.Children.FirstOrDefault(x => x.Key is YamlScalarNode s && s.Value == name).Value;
        static string Str(YamlNode? node) => (node as YamlScalarNode)?.Value ?? "";
        static IEnumerable<YamlMappingNode> Entries(YamlNode? node) =>
            node is YamlSequenceNode sequence ? sequence.Children.OfType<YamlMappingNode>() : [];

        var active = context ?? Str(Field(root, "current-context"));
        var foundContext = Entries(Field(root, "contexts")).FirstOrDefault(x => Str(Field(x, "name")) == active);
        if (foundContext is null || Field(foundContext, "context") is not YamlMappingNode contextValues)
            throw new InvalidDataException($"kubeconfig 中不存在 Context：{active}");
        var user = Str(Field(contextValues, "user"));
        var targetUser = Entries(Field(root, "users")).FirstOrDefault(x => Str(Field(x, "name")) == user);
        if (targetUser is null || Field(targetUser, "user") is not YamlMappingNode credentials)
            throw new InvalidDataException($"kubeconfig 中不存在用户：{user}");
        if (Field(credentials, "exec") is not null || Field(credentials, "auth-provider") is not null)
            throw new NotSupportedException("该 Context 使用 exec/auth-provider 外部认证。免依赖版本目前只支持证书、Token 等不启动外部 CLI 的认证；请配置受信任的独立凭据。");
    }

    public async Task<string> TestAsync(ConnectionProfile profile, CancellationToken ct)
    {
        using var client = Open(profile);
        var pods = await client.CoreV1.ListPodForAllNamespacesAsync(limit: 1, cancellationToken: ct);
        return $"Kubernetes API 已连接 · Context：{(string.IsNullOrEmpty(profile.Context) ? "默认" : profile.Context)} · Pod 列表权限正常";
    }

    public async Task<IReadOnlyList<WorkloadItem>> PodsAsync(ConnectionProfile profile, CancellationToken ct)
    {
        using var client = Open(profile);
        var pods = await client.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: ct);
        var result = new List<WorkloadItem>();
        foreach (var pod in pods.Items)
        {
            if (pod.Metadata?.Name is not { Length: > 0 } name) continue;
            string ns = pod.Metadata.NamespaceProperty ?? "default";
            string phase = pod.Status?.Phase ?? "Unknown";
            foreach (var container in pod.Spec?.Containers ?? [])
            {
                var status = pod.Status?.ContainerStatuses?.FirstOrDefault(x => x.Name == container.Name);
                var state = status is null ? "Unknown" : status.Ready ? "Ready" : "Not Ready";
                result.Add(new WorkloadItem
                {
                    Id = name, Name = name, Namespace = ns, Container = container.Name,
                    Image = container.Image ?? "", State = phase,
                    Status = $"{state} · 重启 {status?.RestartCount ?? 0}"
                });
            }
        }
        return result;
    }

    public async Task<string> InspectAsync(ConnectionProfile profile, WorkloadItem item, CancellationToken ct)
    {
        using var client = Open(profile);
        var pod = await client.CoreV1.ReadNamespacedPodAsync(item.Id, item.Namespace, cancellationToken: ct);
        using var json = JsonDocument.Parse(k8s.KubernetesJson.Serialize(pod));
        return JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task LogsAsync(ConnectionProfile profile, WorkloadItem item, int tail, bool follow,
        Action<string> onLine, CancellationToken ct)
    {
        using var client = Open(profile);
        using var response = await client.CoreV1.ReadNamespacedPodLogWithHttpMessagesAsync(
            item.Id, item.Namespace, container: item.Container, timestamps: true,
            tailLines: tail, follow: follow, cancellationToken: ct);
        using var reader = new StreamReader(response.Body);
        while (await reader.ReadLineAsync(ct) is { } line) onLine(line);
    }
}
