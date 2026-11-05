using Microsoft.Data.SqlClient;

namespace SqlStudio.Services;

/// <summary>A table, view, stored procedure or function. <see cref="Kind"/> is "table", "view", "procedure" or "function".</summary>
public sealed record DependencyObject(string Schema, string Name, string Kind)
{
    public string Id => $"{Schema}.{Name}";
}

/// <summary><see cref="User"/> refers to <see cref="Used"/> in its definition (a view selects from a table, a procedure calls a function).</summary>
public sealed record DependencyLink(DependencyObject User, DependencyObject Used);

/// <summary>Reads what SQL Server recorded about which objects use which, from sys.sql_expression_dependencies.</summary>
public static class DependencyCatalog
{
    // SQL Server records nothing for dynamic SQL built in a string, or for references to other databases, so those never appear.
    public static async Task<List<DependencyLink>> LoadAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        var db = SqlText.Quote(database);
        var sql = $"""
            SELECT DISTINCT rs.name, ro.name, ro.type, ts.name, tobj.name, tobj.type
            FROM {db}.sys.sql_expression_dependencies d
            JOIN {db}.sys.objects ro ON ro.object_id = d.referencing_id
            JOIN {db}.sys.schemas rs ON rs.schema_id = ro.schema_id
            JOIN {db}.sys.objects tobj ON tobj.object_id = d.referenced_id
            JOIN {db}.sys.schemas ts ON ts.schema_id = tobj.schema_id
            WHERE d.referenced_id IS NOT NULL AND ro.object_id <> tobj.object_id
              AND ro.is_ms_shipped = 0 AND tobj.is_ms_shipped = 0
              AND ro.type IN ('U','V','P','FN','IF','TF') AND tobj.type IN ('U','V','P','FN','IF','TF')
            """;

        var links = new List<DependencyLink>();
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            links.Add(new DependencyLink(
                new DependencyObject(r.GetString(0), r.GetString(1), KindOf(r.GetString(2))),
                new DependencyObject(r.GetString(3), r.GetString(4), KindOf(r.GetString(5)))));
        return links;
    }

    /// <summary>Maps a sys.objects type code to the kind used in the diagram.</summary>
    public static string KindOf(string typeCode) => typeCode.Trim() switch
    {
        "U" => "table",
        "V" => "view",
        "P" => "procedure",
        _ => "function",
    };
}

public static class DependencyGraph
{
    public static string LabelOf(string kind) => kind == "procedure" ? "stored procedure" : kind;

    /// <summary>
    /// The links as a diagram: an arrow runs from the object that uses another to the object it uses, and the used
    /// objects sit to the left. With a focus, only what the focus uses (directly or through other objects) and what
    /// uses the focus (the things that could break if it changes) are kept.
    /// </summary>
    public static ErModel Build(IReadOnlyList<DependencyLink> links, DependencyObject? focus = null)
    {
        var kept = focus is null ? links : Reachable(links, focus.Id);
        var objects = new Dictionary<string, DependencyObject>();
        foreach (var l in kept)
        {
            objects[l.User.Id] = l.User;
            objects[l.Used.Id] = l.Used;
        }
        if (focus is not null)
            objects[focus.Id] = focus; // shown even when nothing is related to it

        var tables = objects.Values.Select(o => new ErTable(
            o.Schema, o.Name, [], o.Kind, LabelOf(o.Kind), IsFocus: focus is not null && o.Id == focus.Id)).ToList();
        var relations = kept.Select(l => new ErRelation($"{l.User.Id} uses {l.Used.Id}", l.User.Id, l.Used.Id, [""], [""])).ToList();
        return new ErModel(tables, relations);
    }

    static List<DependencyLink> Reachable(IReadOnlyList<DependencyLink> links, string focusId)
    {
        var keep = new HashSet<DependencyLink>();
        // Down the chain: what the focus uses, and what those use in turn.
        Walk(links, focusId, l => l.User.Id, l => l.Used.Id, keep);
        // Up the chain: what uses the focus, and what uses those in turn.
        Walk(links, focusId, l => l.Used.Id, l => l.User.Id, keep);
        return links.Where(keep.Contains).ToList();
    }

    static void Walk(IReadOnlyList<DependencyLink> links, string start, Func<DependencyLink, string> from, Func<DependencyLink, string> to, HashSet<DependencyLink> keep)
    {
        var seen = new HashSet<string> { start };
        var queue = new Queue<string>([start]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var l in links.Where(l => from(l) == id))
            {
                keep.Add(l);
                if (seen.Add(to(l)))
                    queue.Enqueue(to(l));
            }
        }
    }
}
