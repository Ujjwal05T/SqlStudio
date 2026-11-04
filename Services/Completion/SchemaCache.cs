using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SqlStudio.Services.Completion;

/// <summary>
/// Remembers what the server's catalog looked like, so suggestions appear instantly after the first load. One cache per
/// server session, shared by every query tab on that server.
/// </summary>
public sealed partial class SchemaCache : ISchemaSource
{
    readonly ServerSession _session;
    readonly ConcurrentDictionary<string, Task<DbObjects>> _objects = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Task<IReadOnlyList<ColumnInfo>>> _columns = new(StringComparer.OrdinalIgnoreCase);
    Task<IReadOnlyList<string>>? _databases;

    public SchemaCache(ServerSession session) => _session = session;

    public Task<DbObjects> GetObjectsAsync(string database) =>
        Cached(_objects, database, () => _session.UseAsync(c => CatalogService.ListSchemaAsync(c, database)));

    public Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string database, string schema, string name) =>
        Cached(_columns, $"{database}\u0001{schema}\u0001{name}",
            async () => (IReadOnlyList<ColumnInfo>)await _session.UseAsync(c => CatalogService.ListColumnsAsync(c, database, new SchemaObject(schema, name))));

    public Task<IReadOnlyList<string>> GetDatabasesAsync()
    {
        var task = _databases ??= LoadDatabasesAsync();
        task.ContinueWith(t => { if (t.IsFaulted) _databases = null; }, TaskScheduler.Default); // retry next time
        return task;
    }

    async Task<IReadOnlyList<string>> LoadDatabasesAsync() =>
        (await _session.UseAsync(CatalogService.ListDatabasesAsync)).Where(d => d.IsOnline).Select(d => d.Name).ToList();

    /// <summary>Forget one database's objects and columns, e.g. after a table was created or dropped.</summary>
    public void Invalidate(string? database)
    {
        if (database is null)
            return;
        _objects.TryRemove(database, out _);
        var prefix = database + "\u0001";
        foreach (var key in _columns.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            _columns.TryRemove(key, out _);
    }

    public void Clear()
    {
        _objects.Clear();
        _columns.Clear();
        _databases = null;
    }

    static Task<T> Cached<T>(ConcurrentDictionary<string, Task<T>> cache, string key, Func<Task<T>> load)
    {
        var task = cache.GetOrAdd(key, _ => load());
        // A failed load must not be remembered, or one network blip would break suggestions until restart.
        task.ContinueWith(t => { if (t.IsFaulted || t.IsCanceled) cache.TryRemove(key, out _); }, TaskScheduler.Default);
        return task;
    }

    /// <summary>True when a script probably changed which tables or columns exist.</summary>
    public static bool LooksLikeSchemaChange(string sql) => SchemaChange().IsMatch(StripCommentsAndStrings(sql));

    [GeneratedRegex(@"\b(create|alter|drop|truncate)\b|\bsp_rename\b|\bselect\b[^;]*\binto\b", RegexOptions.IgnoreCase)]
    private static partial Regex SchemaChange();

    static string StripCommentsAndStrings(string sql) =>
        string.Concat(SqlLexer.Tokenize(sql)
            .Where(t => t.Kind is not (TokenKind.LineComment or TokenKind.BlockComment or TokenKind.String))
            .Select(t => t.Text + " "));
}
