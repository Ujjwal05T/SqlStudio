namespace SqlStudio.Services.Completion;

public enum ItemKind { Keyword, Function, Table, View, Procedure, Schema, Column, Database }

public sealed record DbObject(string Schema, string Name, string TypeCode)
{
    /// <summary>Something you can SELECT from: tables, views and table-valued functions.</summary>
    public bool IsTableLike => TypeCode is "U" or "V" or "IF" or "TF";

    public ItemKind Kind => TypeCode switch
    {
        "U" => ItemKind.Table,
        "V" => ItemKind.View,
        "P" => ItemKind.Procedure,
        _ => ItemKind.Function,
    };
}

public sealed record DbObjects(IReadOnlyList<string> Schemas, IReadOnlyList<DbObject> Objects)
{
    public static readonly DbObjects Empty = new([], []);
}

/// <summary>Where completion gets its facts about the server. Tests supply a fake; the app reads the real catalog.</summary>
public interface ISchemaSource
{
    Task<DbObjects> GetObjectsAsync(string database);
    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string database, string schema, string name);
    Task<IReadOnlyList<string>> GetDatabasesAsync();
}

public sealed record CompletionItem(string Label, string InsertText, ItemKind Kind, string? Detail, int Rank);

/// <param name="ReplaceStart">Where the text to replace starts in the original text.</param>
/// <param name="Prefix">What was typed so far (used to pre-select the best match).</param>
public sealed record CompletionResult(int ReplaceStart, string Prefix, bool InBracket, int ExtendAfter, IReadOnlyList<CompletionItem> Items);

public sealed class SqlCompletionProvider
{
    const int MaxItems = 300;
    readonly ISchemaSource _source;

    public SqlCompletionProvider(ISchemaSource source) => _source = source;

    /// <param name="manual">True when the user asked for suggestions (Ctrl+Space): offer them even with nothing typed yet.</param>
    public async Task<CompletionResult?> GetAsync(string text, int caret, string? database, bool manual)
    {
        var ctx = SqlContextAnalyzer.Analyze(text, caret);
        if (ctx.Kind == CompletionKind.None)
            return null;

        // Typing triggers only once there is something to match: one letter for names, two for plain keywords.
        if (!manual)
        {
            var needed = ctx.Kind switch { CompletionKind.Members => 0, CompletionKind.Keywords => 2, _ => ctx.InBracket ? 0 : 1 };
            if (ctx.Prefix.Length < needed)
                return null;
        }

        List<CompletionItem> items;
        try
        {
            items = await BuildAsync(ctx, database);
        }
        catch (Exception)
        {
            return null; // a failed lookup must never get in the way of typing
        }

        var ranked = items
            .Select(i => (Item: i, Score: Match(i.Label, ctx.Prefix)))
            .Where(x => x.Score >= 0)
            .OrderBy(x => x.Score).ThenBy(x => x.Item.Rank).ThenBy(x => x.Item.Label, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Item)
            .Take(MaxItems)
            .ToList();

        return ranked.Count == 0 ? null : new CompletionResult(ctx.ReplaceStart, ctx.Prefix, ctx.InBracket, ctx.ExtendAfter, ranked);
    }

    /// <summary>
    /// 0 = starts with it, 1 = a word inside starts with it, 2 = contains it, 3 or more = the typed characters all appear in
    /// order but spread out ("ordid" finds OrderID; a bigger number means more spread), -1 = no match.
    /// </summary>
    public static int Match(string label, string prefix)
    {
        if (prefix.Length == 0)
            return 0;
        if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return 0;
        for (var i = 1; i < label.Length; i++)
        {
            var boundary = label[i - 1] is '_' or '.' or ' ' || (char.IsLower(label[i - 1]) && char.IsUpper(label[i]));
            if (boundary && string.Compare(label, i, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return 1;
        }
        if (label.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            return 2;
        return SubsequenceSpread(label, prefix) is var spread and >= 0 ? 3 + spread : -1;
    }

    /// <summary>How many extra characters sit between the typed ones when they are found in order, or -1 if they are not all there.</summary>
    static int SubsequenceSpread(string label, string typed)
    {
        var first = -1;
        var at = 0;
        foreach (var c in typed)
        {
            if (char.IsWhiteSpace(c))
                continue;
            var found = label.IndexOf(c.ToString(), at, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                return -1;
            if (first < 0)
                first = found;
            at = found + 1;
        }
        return first < 0 ? 0 : at - first - typed.Count(c => !char.IsWhiteSpace(c));
    }

    async Task<List<CompletionItem>> BuildAsync(CompletionContext ctx, string? database)
    {
        var items = new List<CompletionItem>();
        switch (ctx.Kind)
        {
            case CompletionKind.Databases:
                foreach (var db in await _source.GetDatabasesAsync())
                    items.Add(new CompletionItem(db, SqlKeywords.Quote(db), ItemKind.Database, "database", 0));
                break;

            case CompletionKind.Objects:
                if (database is not null) // without a database there are no names to offer, and keywords would be noise here
                    AddObjects(items, await _source.GetObjectsAsync(database), o => o.IsTableLike, withSchemas: true);
                break;

            case CompletionKind.Procedures:
                if (database is not null)
                    AddObjects(items, await _source.GetObjectsAsync(database), o => o.Kind == ItemKind.Procedure, withSchemas: true);
                break;

            case CompletionKind.Columns:
                await AddColumnsAsync(items, ctx.Tables, database);
                AddWords(items, SqlKeywords.Functions, ItemKind.Function, "function", rank: 5);
                AddWords(items, SqlKeywords.Keywords, ItemKind.Keyword, null, rank: 6);
                break;

            case CompletionKind.Members:
                await AddMembersAsync(items, ctx, database);
                break;

            default:
                AddWords(items, SqlKeywords.Keywords, ItemKind.Keyword, null, rank: 1);
                AddWords(items, SqlKeywords.Functions, ItemKind.Function, "function", rank: 2);
                break;
        }
        return items;
    }

    static void AddWords(List<CompletionItem> items, IEnumerable<string> words, ItemKind kind, string? detail, int rank)
    {
        foreach (var w in words)
            items.Add(new CompletionItem(w, w, kind, detail, rank));
    }

    static void AddObjects(List<CompletionItem> items, DbObjects db, Func<DbObject, bool> include, bool withSchemas)
    {
        if (withSchemas)
            foreach (var schema in db.Schemas.Where(s => !s.Equals("dbo", StringComparison.OrdinalIgnoreCase)))
                items.Add(new CompletionItem(schema, SqlKeywords.Quote(schema) + ".", ItemKind.Schema, "schema", 3));

        foreach (var o in db.Objects.Where(include))
        {
            // dbo objects are inserted bare, like SSMS; anything else keeps its schema so the name is unambiguous
            var dbo = o.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase);
            var label = dbo ? o.Name : $"{o.Schema}.{o.Name}";
            var insert = dbo ? SqlKeywords.Quote(o.Name) : $"{SqlKeywords.Quote(o.Schema)}.{SqlKeywords.Quote(o.Name)}";
            items.Add(new CompletionItem(label, insert, o.Kind, Describe(o), o.Kind == ItemKind.Table ? 0 : 1));
        }
    }

    static string Describe(DbObject o) => o.TypeCode switch
    {
        "U" => "table",
        "V" => "view",
        "P" => "procedure",
        "IF" or "TF" => "table function",
        _ => "function",
    };

    async Task AddMembersAsync(List<CompletionItem> items, CompletionContext ctx, string? database)
    {
        var q = ctx.Qualifier;

        // alias.  or  table.  ->  that table's columns
        if (q.Count == 1)
        {
            var match = ctx.Tables.FirstOrDefault(t => !t.IsCte && t.Matches(q[0]));
            if (match is not null)
            {
                await AddColumnsAsync(items, [match], database);
                return;
            }
        }
        if (database is null)
            return;

        var objects = await _source.GetObjectsAsync(database);
        if (q.Count == 1)
        {
            // schema.  ->  everything in that schema
            var schema = objects.Schemas.FirstOrDefault(s => s.Equals(q[0], StringComparison.OrdinalIgnoreCase));
            if (schema is not null)
            {
                foreach (var o in objects.Objects.Where(o => o.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)))
                    items.Add(new CompletionItem(o.Name, SqlKeywords.Quote(o.Name), o.Kind, Describe(o), o.Kind == ItemKind.Table ? 0 : 1));
                return;
            }

            // otherDatabase.  ->  its schemas
            if ((await _source.GetDatabasesAsync()).Any(d => d.Equals(q[0], StringComparison.OrdinalIgnoreCase)))
            {
                var other = await _source.GetObjectsAsync(q[0]);
                foreach (var s in other.Schemas)
                    items.Add(new CompletionItem(s, SqlKeywords.Quote(s), ItemKind.Schema, "schema", 0));
            }
            return;
        }

        if (q.Count == 2)
        {
            // schema.table.  ->  columns
            var o = objects.Objects.FirstOrDefault(x => x.Schema.Equals(q[0], StringComparison.OrdinalIgnoreCase) && x.Name.Equals(q[1], StringComparison.OrdinalIgnoreCase));
            if (o is not null)
            {
                await AddColumnsAsync(items, [new TableRef(null, o.Schema, o.Name, null)], database);
                return;
            }

            // database.schema.  ->  objects in that schema of the other database
            var otherObjects = await _source.GetObjectsAsync(q[0]);
            foreach (var x in otherObjects.Objects.Where(x => x.Schema.Equals(q[1], StringComparison.OrdinalIgnoreCase)))
                items.Add(new CompletionItem(x.Name, SqlKeywords.Quote(x.Name), x.Kind, Describe(x), x.Kind == ItemKind.Table ? 0 : 1));
            return;
        }

        // database.schema.table.  ->  columns
        await AddColumnsAsync(items, [new TableRef(q[0], q[1], q[2], null)], database);
    }

    async Task AddColumnsAsync(List<CompletionItem> items, IReadOnlyList<TableRef> tables, string? database)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var many = tables.Count(t => !t.IsCte) > 1;
        foreach (var table in tables.Where(t => !t.IsCte))
        {
            var db = table.Database ?? database;
            if (db is null)
                continue;
            var o = FindObject(await _source.GetObjectsAsync(db), table);
            if (o is null)
                continue;

            foreach (var c in await _source.GetColumnsAsync(db, o.Schema, o.Name))
            {
                var shown = table.Alias ?? o.Name;
                var detail = many ? $"{shown} · {c.Type}" : c.Type;
                // a column present in several joined tables is offered once per table, with the table's name to tell them apart
                if (many && !seen.Add($"{shown}.{c.Name}") || !many && !seen.Add(c.Name))
                    continue;
                items.Add(new CompletionItem(c.Name, SqlKeywords.Quote(c.Name), ItemKind.Column, detail + (c.Nullable ? ", null" : ""), 0));
            }
        }
    }

    /// <summary>Finds the real table behind a reference. With no schema given, dbo wins, like SQL Server's own lookup.</summary>
    static DbObject? FindObject(DbObjects db, TableRef table)
    {
        var candidates = db.Objects.Where(o => o.IsTableLike && o.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase));
        if (table.Schema is not null)
            return candidates.FirstOrDefault(o => o.Schema.Equals(table.Schema, StringComparison.OrdinalIgnoreCase));
        return candidates.OrderBy(o => o.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
    }
}
