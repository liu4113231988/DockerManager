using System.Text.Json.Serialization;

namespace DockerManager.Wpf.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionKind>))]
public enum ConnectionKind
{
    DockerSsh,
    DockerContext,
    Kubernetes
}

public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ConnectionKind Kind { get; set; }
    public string DockerHost { get; set; } = "";
    public string Context { get; set; } = "";
    public string Kubeconfig { get; set; } = "";
    public string DockerSocketPath { get; set; } = "/var/run/docker.sock";

    [JsonIgnore]
    public string Subtitle => Kind switch
    {
        ConnectionKind.DockerSsh => "Docker SSH · " + DockerHost,
        ConnectionKind.DockerContext => "旧版 Context（需迁移） · " + Context,
        _ => "Kubernetes · " + (string.IsNullOrWhiteSpace(Context) ? "当前上下文" : Context)
    };

    public override string ToString() => Name;
}

public enum ViewKind { Docker, Compose, Kubernetes, Images, Networks, Volumes }

/// <summary>Which kind of remote object a <see cref="WorkloadItem"/> represents.</summary>
public enum ResourceKind { Container, Image, Network, Volume, Pod }

public sealed class WorkloadItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Namespace { get; init; } = "";
    public string Image { get; init; } = "";
    public string State { get; init; } = "";
    public string Status { get; init; } = "";
    public string Project { get; init; } = "";
    public string Service { get; init; } = "";
    public string Container { get; init; } = "";
    public string Tags { get; init; } = "";
    public string Size { get; init; } = "";
    public string Driver { get; init; } = "";
    public string Scope { get; init; } = "";
    public string Mountpoint { get; init; } = "";
    public ResourceKind Kind { get; init; } = ResourceKind.Container;
}

public sealed class ComposeProject
{
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";
    public override string ToString() => $"{Name}    {Status}";
}
