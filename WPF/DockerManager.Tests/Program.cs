using DockerManager.Wpf.Models;
using DockerManager.Wpf.Services;

static void Equal<T>(T expected, T actual, string test)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{test}: expected {expected}, got {actual}");
    Console.WriteLine("PASS " + test);
}

var ssh = new ConnectionProfile { Kind = ConnectionKind.DockerSsh, DockerHost = "ssh://tester@server.example" };
var docker = ContainerClient.DockerArgs(ssh, "ps", "--all");
Equal("--host", docker[0], "docker ssh flag");
Equal("ssh://tester@server.example", docker[1], "docker ssh target");
Equal("ps", docker[2], "docker command");

var ctx = new ConnectionProfile { Kind = ConnectionKind.DockerContext, Context = "prod" };
var withContext = ContainerClient.DockerArgs(ctx, "logs", "123");
Equal("--context", withContext[0], "docker context flag");
Equal("prod", withContext[1], "docker context selection");

var kube = new ConnectionProfile { Kind = ConnectionKind.Kubernetes, Context = "my-cluster", Kubeconfig = @"C:\Users\Demo\.kube\config" };
var kubectl = ContainerClient.KubernetesArgs(kube, "get", "pods");
Equal("--kubeconfig", kubectl[0], "kubectl custom config");
Equal(@"C:\Users\Demo\.kube\config", kubectl[1], "kubectl path remains single argument");
Equal("--context", kubectl[2], "kubectl context flag");
Equal("my-cluster", kubectl[3], "kubectl context value");
Equal("get", kubectl[4], "kubectl command");

Console.WriteLine("All smoke tests passed.");
