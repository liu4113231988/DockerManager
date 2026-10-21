using System.Text.Json;
using DockerManager.Wpf.Models;

namespace DockerManager.Wpf.Services;

/// <summary>Uses Docker CLI / kubectl without a shell; every parameter is a discrete argument.</summary>
public sealed class ContainerClient
{
    private readonly CommandRunner _runner = new();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(35);

    public static IReadOnlyList<string> DockerArgs(ConnectionProfile profile, params string[] command)
    {
        var args = new List<string>();
        switch (profile.Kind)
        {
            case ConnectionKind.DockerSsh:
                args.AddRange(["--host", profile.DockerHost]);
                break;
            case ConnectionKind.DockerContext:
                args.AddRange(["--context", profile.Context]);
                break;
            default:
                throw new InvalidOperationException("当前连接不是 Docker 类型。");
        }
        args.AddRange(command);
        return args;
    }

    public static IReadOnlyList<string> KubernetesArgs(ConnectionProfile profile, params string[] command)
    {
        if (profile.Kind != ConnectionKind.Kubernetes)
            throw new InvalidOperationException("当前连接不是 Kubernetes 类型。");
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.Kubeconfig)) args.AddRange(["--kubeconfig", profile.Kubeconfig]);
        if (!string.IsNullOrWhiteSpace(profile.Context)) args.AddRange(["--context", profile.Context]);
        args.AddRange(command);
        return args;
    }

    public async Task<string> TestAsync(ConnectionProfile profile, CancellationToken ct)
    {
        CommandResult result = profile.Kind == ConnectionKind.Kubernetes
            ? await _runner.RunAsync("kubectl", KubernetesArgs(profile, "get", "--raw", "/version", "--request-timeout=15s"), Timeout, ct)
            : await _runner.RunAsync("docker", DockerArgs(profile, "version", "--format", "{{.Server.Version}}"), Timeout, ct);
        result.ThrowOnFailure(profile.Kind == ConnectionKind.Kubernetes ? "kubectl" : "docker");
        return result.Output.Trim();
    }

    public async Task<IReadOnlyList<WorkloadItem>> DockerContainersAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var result = await _runner.RunAsync("docker", DockerArgs(profile, "ps", "--all", "--no-trunc", "--format", "json"), Timeout, ct);
        result.ThrowOnFailure("docker ps");
        return JsonLines(result.Output).Select(x => new WorkloadItem
        {
            Id = Get(x, "ID"), Name = Get(x, "Names"), Image = Get(x, "Image"),
            State = Get(x, "State"), Status = Get(x, "Status"),
            Project = FindLabel(Get(x, "Labels"), "com.docker.compose.project"),
            Service = FindLabel(Get(x, "Labels"), "com.docker.compose.service")
        }).ToList();
    }

    // Compose projects are discovered from Engine labels rather than running
    // docker compose ps: remote Compose yaml files are not on the Windows PC.
    public async Task<IReadOnlyList<ComposeProject>> ComposeProjectsAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var containers = await DockerContainersAsync(profile, ct);
        return containers.Where(x => x.Project.Length > 0)
            .GroupBy(x => x.Project, StringComparer.Ordinal)
            .Select(g => new ComposeProject
            {
                Name = g.Key,
                Status = $"{g.Count(x => x.State.Equals("running", StringComparison.OrdinalIgnoreCase))}/{g.Count()} 运行"
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<WorkloadItem>> ComposeContainersAsync(ConnectionProfile profile, string project, CancellationToken ct)
    {
        var containers = await DockerContainersAsync(profile, ct);
        return containers.Where(x => string.Equals(x.Project, project, StringComparison.Ordinal)).ToList();
    }

    public async Task<IReadOnlyList<WorkloadItem>> PodsAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var result = await _runner.RunAsync("kubectl", KubernetesArgs(profile, "get", "pods", "--all-namespaces", "-o", "json", "--request-timeout=20s"), Timeout, ct);
        result.ThrowOnFailure("kubectl get pods");
        using var document = JsonDocument.Parse(result.Output);
        var workloads = new List<WorkloadItem>();
        foreach (var pod in document.RootElement.GetProperty("items").EnumerateArray())
        {
            var metadata = pod.GetProperty("metadata");
            var podName = Get(metadata, "name");
            var ns = Get(metadata, "namespace");
            var phase = pod.TryGetProperty("status", out var st) ? Get(st, "phase") : "Unknown";
            if (!pod.TryGetProperty("spec", out var spec) || !spec.TryGetProperty("containers", out var containers)) continue;
            foreach (var container in containers.EnumerateArray())
            {
                var name = Get(container, "name");
                workloads.Add(new WorkloadItem
                {
                    Id = podName, Name = podName, Container = name, Namespace = ns,
                    Image = Get(container, "image"), State = phase,
                    Status = GetContainerState(pod, name)
                });
            }
        }
        return workloads;
    }

    public Task<CommandResult> InspectAsync(ConnectionProfile profile, WorkloadItem entry, CancellationToken ct)
    {
        return profile.Kind == ConnectionKind.Kubernetes
            ? _runner.RunAsync("kubectl", KubernetesArgs(profile, "get", "pod", entry.Id, "--namespace", entry.Namespace, "-o", "json"), Timeout, ct)
            : _runner.RunAsync("docker", DockerArgs(profile, "inspect", entry.Id), Timeout, ct);
    }

    public Task<CommandResult> ContainerActionAsync(ConnectionProfile profile, WorkloadItem entry, string action, CancellationToken ct)
    {
        if (profile.Kind == ConnectionKind.Kubernetes) throw new InvalidOperationException("Kubernetes 不支持通过此操作直接管理容器。");
        if (action is not ("start" or "stop" or "restart")) throw new ArgumentOutOfRangeException(nameof(action));
        return _runner.RunAsync("docker", DockerArgs(profile, action, entry.Id), Timeout, ct);
    }

    public Task StreamLogsAsync(ConnectionProfile profile, WorkloadItem entry, int tail, bool follow,
        Action<string> onLine, CancellationToken ct)
    {
        string executable;
        IReadOnlyList<string> args;
        if (profile.Kind == ConnectionKind.Kubernetes)
        {
            executable = "kubectl";
            var command = new List<string> { "logs", entry.Id, "--namespace", entry.Namespace, "--container", entry.Container, "--timestamps=true", "--tail", tail.ToString() };
            if (follow) command.Add("--follow");
            args = KubernetesArgs(profile, command.ToArray());
        }
        else
        {
            executable = "docker";
            var command = new List<string> { "logs", "--timestamps", "--tail", tail.ToString() };
            if (follow) command.Add("--follow");
            command.Add(entry.Id);
            args = DockerArgs(profile, command.ToArray());
        }
        return _runner.StreamAsync(executable, args, onLine, ct);
    }

    private static IEnumerable<JsonElement> JsonLines(string input)
    {
        // Docker versions may return JSON Lines or a single JSON array.
        var trimmed = input.Trim();
        if (trimmed.Length == 0) yield break;
        if (trimmed.StartsWith('['))
        {
            using var arrayDoc = JsonDocument.Parse(trimmed);
            foreach (var element in arrayDoc.RootElement.EnumerateArray()) yield return element.Clone();
            yield break;
        }
        foreach (var line in input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
    }

    private static string Get(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.ToString() : "";
    }

    private static string GetContainerState(JsonElement pod, string containerName)
    {
        if (!pod.TryGetProperty("status", out var status) || !status.TryGetProperty("containerStatuses", out var array)) return "";
        foreach (var entry in array.EnumerateArray())
        {
            if (Get(entry, "name") != containerName) continue;
            if (entry.TryGetProperty("state", out var state))
                return state.EnumerateObject().FirstOrDefault().Name ?? "";
        }
        return "";
    }

    private static string FindLabel(string labels, string key)
    {
        foreach (var label in labels.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = label.Trim().Split('=', 2);
            if (parts.Length == 2 && parts[0] == key) return parts[1];
        }
        return "";
    }
}
