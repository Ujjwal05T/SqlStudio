using System.Text.Json;

namespace SqlStudio.Services;

/// <summary>
/// Keeps one encrypted secret per profile in a JSON file under the user's config folder
/// (%APPDATA%\SqlStudio on Windows, ~/.config/SqlStudio on Linux). Subclasses supply the encryption.
/// </summary>
public abstract class EncryptedFileCredentialStore : ICredentialStore
{
    readonly object _gate = new();

    protected EncryptedFileCredentialStore(string? path) =>
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlStudio", "credentials.json");

    protected string Path { get; }

    /// <summary>Returns the stored text for this base64 blob, or null when it can't be decrypted.</summary>
    protected abstract string? Decrypt(string blob);

    protected abstract string Encrypt(string password);

    public string? Get(Guid profileId)
    {
        lock (_gate)
            return Load().TryGetValue(profileId.ToString(), out var blob) ? Decrypt(blob) : null;
    }

    public void Set(Guid profileId, string password)
    {
        lock (_gate)
        {
            var all = Load();
            all[profileId.ToString()] = Encrypt(password);
            Save(all);
        }
    }

    public void Delete(Guid profileId)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.Remove(profileId.ToString()))
                Save(all);
        }
    }

    Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }
        return [];
    }

    void Save(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, Path, overwrite: true);
    }
}
