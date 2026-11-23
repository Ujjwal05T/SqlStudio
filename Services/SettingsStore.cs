using System.Text.Json;

namespace SqlStudio.Services;

public sealed class AppSettings
{
    public string ThemeId { get; set; } = "midnight";
    public ClaudeAccessSettings ClaudeAccess { get; set; } = new();
    public CompanySettings Company { get; set; } = new();
}

/// <summary>Company sign-in preferences. The backend address override is hidden: edit settings.json, there is no screen for it.</summary>
public sealed class CompanySettings
{
    public string BackendUrlOverride { get; set; } = "";
    public string LastEmail { get; set; } = "";
}

/// <summary>The "Connect Claude" switch, its port and secret key, and which servers the user allowed or refused.</summary>
public sealed class ClaudeAccessSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 47821;
    public string Token { get; set; } = "";

    /// <summary>Profile id to the user's choice. A server with no entry follows its environment tag.</summary>
    public Dictionary<string, bool> Allow { get; set; } = [];
}

public sealed class SettingsStore
{
    readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlStudio", "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings(); // a damaged preferences file must never stop the app from starting
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
