using DockerManager.Wpf.Models;

namespace DockerManager.Wpf.Services;

/// <summary>UI-facing facade. All remote operations use managed .NET libraries, never external executables.</summary>
public sealed class ContainerClient : IDisposable
{
    private readonly DockerApiClient _docker = new();
    private readonly KubernetesApiClient _kubernetes = new();

    public void Dispose() => _docker.Dispose();

    public Func<HostKeyPrompt, CancellationToken, Task<bool>>? ConfirmNewHostKey
    {
        get => _docker.ConfirmNewHostKey;
        set => _docker.ConfirmNewHostKey = value;
    }

    public Task<string> TestAsync(ConnectionProfile profile, CancellationToken ct) =>
        profile.Kind == ConnectionKind.Kubernetes ? _kubernetes.TestAsync(profile, ct) : _docker.TestAsync(profile, ct);

    public Task<IReadOnlyList<WorkloadItem>> DockerContainersAsync(ConnectionProfile profile, CancellationToken ct) =>
        _docker.ContainersAsync(profile, ct);

    public async Task<IReadOnlyList<ComposeProject>> ComposeProjectsAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var containers = await _docker.ContainersAsync(profile, ct);
        return containers.Where(x => x.Project.Length > 0)
            .GroupBy(x => x.Project, StringComparer.Ordinal)
            .Select(g => new ComposeProject
            {
                Name = g.Key,
                Status = $"{g.Count(x => x.State.Equals("running", StringComparison.OrdinalIgnoreCase))}/{g.Count()} 运行"
            }).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<WorkloadItem>> ComposeContainersAsync(ConnectionProfile profile, string project, CancellationToken ct) =>
        (await _docker.ContainersAsync(profile, ct)).Where(x => string.Equals(x.Project, project, StringComparison.Ordinal)).ToList();

    public Task<IReadOnlyList<WorkloadItem>> PodsAsync(ConnectionProfile profile, CancellationToken ct) =>
        _kubernetes.PodsAsync(profile, ct);

    public Task<IReadOnlyList<WorkloadItem>> ImagesAsync(ConnectionProfile profile, CancellationToken ct) =>
        _docker.ImagesAsync(profile, ct);

    public Task<IReadOnlyList<WorkloadItem>> NetworksAsync(ConnectionProfile profile, CancellationToken ct) =>
        _docker.NetworksAsync(profile, ct);

    public Task<IReadOnlyList<WorkloadItem>> VolumesAsync(ConnectionProfile profile, CancellationToken ct) =>
        _docker.VolumesAsync(profile, ct);

    public Task DeleteAsync(ConnectionProfile profile, WorkloadItem item, CancellationToken ct) =>
        _docker.DeleteAsync(profile, item, ct);

    public Task CreateVolumeAsync(ConnectionProfile profile, string name, string driver, CancellationToken ct) =>
        _docker.CreateVolumeAsync(profile, name, driver, ct);

    public Task CreateNetworkAsync(ConnectionProfile profile, string name, string driver, bool internalOnly, CancellationToken ct) =>
        _docker.CreateNetworkAsync(profile, name, driver, internalOnly, ct);

    public async Task<string> InspectAsync(ConnectionProfile profile, WorkloadItem item, CancellationToken ct) =>
        profile.Kind == ConnectionKind.Kubernetes
            ? await _kubernetes.InspectAsync(profile, item, ct)
            : await _docker.InspectAsync(profile, item, ct);

    public Task ContainerActionAsync(ConnectionProfile profile, WorkloadItem item, string action, CancellationToken ct)
    {
        if (profile.Kind == ConnectionKind.Kubernetes) throw new InvalidOperationException("此版本不修改 Kubernetes 工作负载。");
        return _docker.ActionAsync(profile, item.Id, action, ct);
    }

    public Task StreamLogsAsync(ConnectionProfile profile, WorkloadItem item, int tail, bool follow, Action<string> onLine, CancellationToken ct) =>
        profile.Kind == ConnectionKind.Kubernetes
            ? _kubernetes.LogsAsync(profile, item, tail, follow, onLine, ct)
            : _docker.LogsAsync(profile, item.Id, tail, follow, onLine, ct);
}
