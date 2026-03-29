using System.Text.Json;

namespace Andy.Containers.Cli.Auth;

public class StoredCredentials
{
    public string? ApiUrl { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? UserEmail { get; set; }
    public string? AuthorityUrl { get; set; }
}

public static class CredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetCredentialPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".andy", "credentials.json");
    }

    public static StoredCredentials? Load()
    {
        var path = GetCredentialPath();
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<StoredCredentials>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(StoredCredentials creds)
    {
        var path = GetCredentialPath();
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(creds, JsonOptions);
        File.WriteAllText(path, json);

        // Set file permissions to owner-only on Unix
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* best effort */ }
        }
    }

    public static void Delete()
    {
        var path = GetCredentialPath();
        if (File.Exists(path))
            File.Delete(path);
    }

    public static bool IsExpired(StoredCredentials creds)
    {
        if (creds.ExpiresAt is null) return false;
        return DateTime.UtcNow >= creds.ExpiresAt.Value.AddSeconds(-60);
    }
}
