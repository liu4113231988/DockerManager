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
    var safe = File.ReadAllText(path).Replace("      exec:\n        command: untrusted.exe", "      token: fake-test-token");
    File.WriteAllText(path, safe);
    KubernetesApiClient.ValidateNoExternalCredentials(path, "demo");
    Console.WriteLine("PASS direct token configuration is allowed");
}
finally { File.Delete(path); }

Console.WriteLine("All DockerManager managed-API smoke tests passed.");
