using System.Globalization;
using System.Text;
using SqlStudio.Services;

namespace SqlStudio.Services.Mcp;

/// <summary>Turns the schema into short plain text for an AI to read: searching for tables and columns, and describing one table.</summary>
public static class SchemaText
{
    const int MaxTables = 40;
    const int MaxColumnsShown = 30;
    const int MaxOverviewTables = 300;
    const int MaxOverviewColumns = 60;

    /// <summary>
    /// Tables whose own name or column names contain every word of <paramref name="text"/> (across the table's name and columns),
    /// best matches first. Empty text lists the tables.
    /// </summary>
    public static string Search(ErModel model, string? text)
    {
        var words = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return Listing(model);

        var hits = new List<(ErTable Table, int Score, List<ErColumn> Columns)>();
        foreach (var table in model.Tables)
        {
            var nameWords = words.Count(w => Contains(table.Name, w) || Contains(table.Schema, w));
            var matched = table.Columns.Where(c => words.Any(w => Contains(c.Name, w))).ToList();
            var covered = words.Count(w => Contains(table.Name, w) || Contains(table.Schema, w) || table.Columns.Any(c => Contains(c.Name, w)));
            if (covered < words.Length)
                continue; // every word must show up somewhere in this table
            hits.Add((table, nameWords * 3 + matched.Count, matched));
        }

        if (hits.Count == 0)
            return $"Nothing in this database has a table or column name containing \"{string.Join(' ', words)}\". " +
                   "Try a shorter or different word (for example a part of the name), or list the tables by leaving the search empty.";

        var sb = new StringBuilder();
        var shown = hits.OrderByDescending(h => h.Score).ThenBy(h => h.Table.Id, StringComparer.OrdinalIgnoreCase).Take(MaxTables).ToList();
        sb.AppendLine($"{hits.Count} table{(hits.Count == 1 ? "" : "s")} match{(hits.Count == 1 ? "es" : "")}" + (hits.Count > shown.Count ? $" (showing the best {shown.Count})" : "") + ":");
        foreach (var (table, _, columns) in shown)
        {
            sb.AppendLine().AppendLine($"{table.Id}  ({table.Columns.Count} columns)");
            var list = columns.Count > 0 ? columns : table.Columns.Take(8).ToList();
            foreach (var c in list.Take(MaxColumnsShown))
                sb.AppendLine("  " + Describe(c));
        }
        sb.AppendLine().Append("Use describe_table for all columns and relationships of one table.");
        return sb.ToString();
    }

    static string Listing(ErModel model)
    {
        if (model.Tables.Count == 0)
            return "This database has no tables.";
        var sb = new StringBuilder($"{model.Tables.Count} tables:\n");
        foreach (var t in model.Tables.OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase).Take(300))
            sb.AppendLine($"{t.Id}  ({t.Columns.Count} columns)");
        if (model.Tables.Count > 300)
            sb.AppendLine($"… and {model.Tables.Count - 300} more. Search by a word to narrow it down.");
        return sb.ToString();
    }

    /// <summary>
    /// The whole database in a compact form: every table with its columns, keys and relationships, plus the user's notes.
    /// One call here replaces a search and a describe for each table.
    /// </summary>
    public static string Overview(ErModel model, string database, IReadOnlyList<SchemaNote> notes)
    {
        var tables = model.Tables.Where(t => t.Kind == "table").OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder($"Database {database}: {tables.Count} table{(tables.Count == 1 ? "" : "s")}.\n");
        sb.AppendLine("Columns read: name type, with ? after the type when it allows NULL, PK for primary key, and FK->table.column for a foreign key.");
        if (notes.FirstOrDefault(n => n.Table is null) is { } dbNote)
            sb.AppendLine().AppendLine("Note from the user about this database: " + OneLine(dbNote.Text));
        if (tables.Count == 0)
            return sb.ToString().TrimEnd();

        foreach (var table in tables.Take(MaxOverviewTables))
        {
            var note = notes.FirstOrDefault(n => n.Table is not null && n.Table.Equals(table.Id, StringComparison.OrdinalIgnoreCase));
            sb.AppendLine().Append(table.Id);
            if (note is not null)
                sb.Append("  Note: ").Append(OneLine(note.Text));
            sb.AppendLine();
            var parts = table.Columns.Take(MaxOverviewColumns).Select(c => OverviewColumn(model, table, c)).ToList();
            sb.Append("  ").Append(string.Join(", ", parts));
            if (table.Columns.Count > MaxOverviewColumns)
                sb.Append($", … {table.Columns.Count - MaxOverviewColumns} more columns (describe_table shows them)");
            sb.AppendLine();
        }
        if (tables.Count > MaxOverviewTables)
            sb.AppendLine().AppendLine($"… and {tables.Count - MaxOverviewTables} more tables. Use search_schema to find them.");
        return sb.ToString().TrimEnd();
    }

    static string OverviewColumn(ErModel model, ErTable table, ErColumn c)
    {
        var text = $"{c.Name} {c.DisplayType}";
        if (c.IsPrimaryKey)
            text += " PK";
        if (c.IsForeignKey)
        {
            foreach (var r in model.Relations.Where(r => r.FromId == table.Id))
            {
                var at = r.FromColumns.ToList().FindIndex(f => f.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                    text += $" FK->{r.ToId}.{(at < r.ToColumns.Count ? r.ToColumns[at] : "?")}";
            }
        }
        return text;
    }

    static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>All columns and relationships of one table. <paramref name="name"/> may be "Orders" or "dbo.Orders".</summary>
    public static string DescribeTable(ErModel model, string name)
    {
        var matches = Find(model, name);
        if (matches.Count == 0)
            throw new McpToolException($"No table named \"{name}\" in this database. Use search_schema to find the right name.");
        if (matches.Count > 1)
            throw new McpToolException($"\"{name}\" matches more than one table: {string.Join(", ", matches.Select(t => t.Id))}. Give the schema too, like {matches[0].Id}.");

        var table = matches[0];
        var sb = new StringBuilder($"{table.Id}\n\nColumns:\n");
        foreach (var c in table.Columns)
            sb.AppendLine("  " + Describe(c));

        var outgoing = model.Relations.Where(r => r.FromId == table.Id).ToList();
        var incoming = model.Relations.Where(r => r.ToId == table.Id && r.FromId != table.Id).ToList();
        if (outgoing.Count > 0)
        {
            sb.AppendLine().AppendLine("References (this table points to):");
            foreach (var r in outgoing)
                sb.AppendLine($"  ({string.Join(", ", r.FromColumns)}) -> {r.ToId} ({string.Join(", ", r.ToColumns)})");
        }
        if (incoming.Count > 0)
        {
            sb.AppendLine().AppendLine("Referenced by (these tables point to this one):");
            foreach (var r in incoming)
                sb.AppendLine($"  {r.FromId} ({string.Join(", ", r.FromColumns)}) -> ({string.Join(", ", r.ToColumns)})");
        }
        return sb.ToString().TrimEnd();
    }

    static List<ErTable> Find(ErModel model, string name)
    {
        var wanted = name.Trim().Replace("[", "").Replace("]", "");
        var exact = model.Tables.Where(t => t.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count > 0 ? exact : model.Tables.Where(t => t.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    static string Describe(ErColumn c)
    {
        var flags = string.Join(' ', new[] { c.IsPrimaryKey ? "PK" : null, c.IsForeignKey ? "FK" : null, c.Nullable ? "null" : "not null" }.Where(f => f is not null));
        return $"{c.Name}  {c.Type}  {flags}";
    }

    static bool Contains(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
}
