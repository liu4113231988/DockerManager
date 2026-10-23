using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DockerManager.Wpf.Services;

/// <summary>
/// Stores optionally remembered SSH passwords, encrypted with Windows DPAPI for the
/// current user. Values never touch the disk as plaintext.
/// </summary>
public sealed class CredentialStore
{
    public string FilePath { get; }

    public CredentialStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DockerManager", "credentials.json");
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(FilePath)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // A corrupted store must never block a connection; it simply holds no passwords.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Save(Dictionary<string, string> credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(credentials, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, overwrite: true);
    }

    private static string Protect(string password) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? blob)
    {
        if (string.IsNullOrEmpty(blob)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // Saved by another user or on another machine: undecryptable, treat as absent.
            return null;
        }
    }

    public string? Get(string key) => Unprotect(Load().GetValueOrDefault(key));

    public void Set(string key, string password)
    {
        var credentials = Load();
        credentials[key] = Protect(password);
        Save(credentials);
    }

    public void Remove(string key)
    {
        var credentials = Load();
        if (credentials.Remove(key)) Save(credentials);
    }
}
