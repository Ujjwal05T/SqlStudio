using System.Text.Json;

namespace SqlStudio.Services;

/// <summary>
/// What the user wants Claude to know about a database or one of its tables (what a status code means, how tables join).
/// <see cref="Table"/> is "schema.name", or null for a note on the whole database.
/// </summary>
public sealed record SchemaNote(Guid ProfileId, string Database, string? Table, string Text);

/// <summary>Keeps the user's notes in a file next to the other settings. Safe to use from the UI and from Claude's calls at the same time.</summary>
public sealed class SchemaNoteStore
{
    readonly string _path;
    readonly object _gate = new();
    List<SchemaNote>? _cache; // the sidebar asks for every row, so the file is read once

    public SchemaNoteStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlStudio", "notes.json");
    }

    public string? Get(Guid profileId, string database, string? table) =>
        Load().FirstOrDefault(n => Matches(n, profileId, database, table))?.Text;

    /// <summary>Every note for one database: the database note first, then the tables.</summary>
    public IReadOnlyList<SchemaNote> ForDatabase(Guid profileId, string database) =>
        Load().Where(n => n.ProfileId == profileId && n.Database.Equals(database, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Table is not null).ThenBy(n => n.Table, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Saves the note, or removes it when the text is empty.</summary>
    public void Set(Guid profileId, string database, string? table, string text)
    {
        lock (_gate)
        {
            var notes = Load().Where(n => !Matches(n, profileId, database, table)).ToList();
            if (!string.IsNullOrWhiteSpace(text))
                notes.Add(new SchemaNote(profileId, database, table, text.Trim()));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
            _cache = notes;
        }
    }

    static bool Matches(SchemaNote n, Guid profileId, string database, string? table) =>
        n.ProfileId == profileId
        && n.Database.Equals(database, StringComparison.OrdinalIgnoreCase)
        && string.Equals(n.Table, table, StringComparison.OrdinalIgnoreCase);

    List<SchemaNote> Load()
    {
        lock (_gate)
        {
            if (_cache is not null)
                return _cache;
            try
            {
                return _cache = File.Exists(_path) ? JsonSerializer.Deserialize<List<SchemaNote>>(File.ReadAllText(_path)) ?? [] : [];
            }
            catch (JsonException)
            {
                return _cache = []; // a damaged notes file must never get in the way of using the app
            }
        }
    }
}
