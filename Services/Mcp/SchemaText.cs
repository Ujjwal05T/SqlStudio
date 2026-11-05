using System.Globalization;
using System.Text;

namespace SqlStudio.Services.Mcp;

/// <summary>Turns the schema into short plain text for an AI to read: searching for tables and columns, and describing one table.</summary>
public static class SchemaText
{
    const int MaxTables = 40;
    const int MaxColumnsShown = 30;

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
