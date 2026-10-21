using System.IO;
using System.Text.Json;
using DockerManager.Wpf.Models;

namespace DockerManager.Wpf.Services;

public sealed class ProfileStore
{
    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DockerManager", "connections.json");

    public IReadOnlyList<ConnectionProfile> Load()
    {
        if (!File.Exists(FilePath)) return [];
        // A damaged configuration is intentionally reported instead of silently overwritten.
        return JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(FilePath), Options) ?? [];
    }

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(profiles, Options));
        File.Move(temporary, FilePath, overwrite: true);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
