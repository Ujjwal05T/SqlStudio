using System.Text.Json;
using System.Text.Json.Serialization;
using SqlStudio.Models;

namespace SqlStudio.Services;

public sealed class ProfileStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly string _path;

    public ProfileStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlStudio", "profiles.json");
    }

    public List<ConnectionProfile> Load()
    {
        if (!File.Exists(_path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(_path), Json) ?? [];
        }
        catch (JsonException)
        {
            // Keep the unreadable file around instead of silently overwriting it on the next save.
            File.Move(_path, _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            return [];
        }
    }

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(profiles, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
