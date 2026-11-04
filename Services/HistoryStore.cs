using System.Text.Json;

namespace SqlStudio.Services;

public sealed record HistoryEntry(DateTime At, Guid ProfileId, string ServerName, string? Database, string Sql, bool Ok, double Seconds)
{
    /// <summary>First meaningful line, for a one-line preview.</summary>
    public string Preview
    {
        get
        {
            var line = Sql.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("--")) ?? Sql.Trim();
            return line.Length > 110 ? line[..110] + "…" : line;
        }
    }

    public string Where => string.IsNullOrEmpty(Database) ? ServerName : $"{ServerName} · {Database}";

    public string When => AgoText(DateTime.Now - At, At);

    public static string AgoText(TimeSpan age, DateTime at) => age.TotalMinutes switch
    {
        < 1 => "just now",
        < 60 => $"{(int)age.TotalMinutes} min ago",
        < 60 * 24 when age.TotalHours < 24 => $"{(int)age.TotalHours} h ago",
        < 60 * 48 => "yesterday",
        _ => at.ToString("d MMM"),
    };
}

/// <summary>Remembers the last queries that were run so the home screen can offer them again.</summary>
public sealed class HistoryStore
{
    public const int MaxEntries = 100;
    const int MaxSqlChars = 20_000;

    readonly string _path;

    public HistoryStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlStudio", "history.json");
    }

    /// <summary>Newest first.</summary>
    public List<HistoryEntry> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Adds an entry at the top. Running the same script on the same server again moves it up instead of repeating it.</summary>
    public static List<HistoryEntry> Add(List<HistoryEntry> history, HistoryEntry entry)
    {
        var sql = entry.Sql.Length > MaxSqlChars ? entry.Sql[..MaxSqlChars] : entry.Sql;
        entry = entry with { Sql = sql };
        var result = history
            .Where(h => !(h.ProfileId == entry.ProfileId && h.Sql == entry.Sql))
            .Prepend(entry)
            .Take(MaxEntries)
            .ToList();
        return result;
    }

    public void Save(IEnumerable<HistoryEntry> history)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(history));
        File.Move(tmp, _path, overwrite: true);
    }
}
