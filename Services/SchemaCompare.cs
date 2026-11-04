using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlStudio.Services;

/// <summary>One database object reduced to comparable text: a module's definition, or a table's columns, indexes and constraints.</summary>
public sealed record SnapshotObject(string Kind, string Schema, string Name, IReadOnlyList<string> Lines)
{
    public string Key => $"{Schema}.{Name}";
    public string FullName => $"{SqlText.Quote(Schema)}.{SqlText.Quote(Name)}";
}

public sealed record SchemaSnapshot(IReadOnlyList<SnapshotObject> Objects);

public enum DiffStatus { OnlyInSource, OnlyInTarget, Different }

public enum DiffLineKind { Same, SourceOnly, TargetOnly }

public sealed record DiffLine(DiffLineKind Kind, string Text)
{
    public bool IsSourceOnly => Kind == DiffLineKind.SourceOnly;
    public bool IsTargetOnly => Kind == DiffLineKind.TargetOnly;
    public string Marker => Kind switch { DiffLineKind.SourceOnly => "+", DiffLineKind.TargetOnly => "−", _ => " " };
}

public sealed record SchemaDifference(string Kind, string Schema, string Name, DiffStatus Status, IReadOnlyList<DiffLine> Lines)
{
    public string FullName => $"{Schema}.{Name}";

    public string StatusLabel => Status switch
    {
        DiffStatus.OnlyInSource => "only in source",
        DiffStatus.OnlyInTarget => "only in target",
        _ => "different",
    };

    /// <summary>+ means the source has it and the target does not; − is the reverse; ≠ means both have it but it differs.</summary>
    public string IconKind => Status switch { DiffStatus.OnlyInSource => "plus", DiffStatus.OnlyInTarget => "minus", _ => "notequal" };
}

/// <summary>Compares two snapshots. "Source" is the reference; "+" lines are what the target would need to match it.</summary>
public static class SchemaComparer
{
    public static List<SchemaDifference> Compare(SchemaSnapshot source, SchemaSnapshot target)
    {
        var targets = target.Objects.ToDictionary(o => o.Key, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SchemaDifference>();

        foreach (var s in source.Objects)
        {
            seen.Add(s.Key);
            if (!targets.TryGetValue(s.Key, out var t))
                result.Add(new(s.Kind, s.Schema, s.Name, DiffStatus.OnlyInSource, s.Lines.Select(l => new DiffLine(DiffLineKind.SourceOnly, l)).ToList()));
            else if (!s.Lines.SequenceEqual(t.Lines, StringComparer.Ordinal) || s.Kind != t.Kind)
                result.Add(new(s.Kind == t.Kind ? s.Kind : $"{s.Kind} / {t.Kind}", s.Schema, s.Name, DiffStatus.Different, LineDiff.Compute(Header(s).Concat(s.Lines).ToList(), Header(t).Concat(t.Lines).ToList())));
        }

        foreach (var t in target.Objects.Where(t => !seen.Contains(t.Key)))
            result.Add(new(t.Kind, t.Schema, t.Name, DiffStatus.OnlyInTarget, t.Lines.Select(l => new DiffLine(DiffLineKind.TargetOnly, l)).ToList()));

        return result
            .OrderBy(d => d.Kind, StringComparer.Ordinal)
            .ThenBy(d => d.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The type is part of the comparison, so a table in one database and a view of the same name in the other shows up.
    static IEnumerable<string> Header(SnapshotObject o) => [$"-- {o.Kind.ToLowerInvariant()}"];
}

/// <summary>Line-by-line diff. Equal lines at both ends are trimmed first, so only the changed middle is compared.</summary>
public static class LineDiff
{
    /// <summary>Beyond this many cell comparisons the changed middle is shown as one removed block and one added block.</summary>
    const long MaxCells = 16_000_000;

    public static List<DiffLine> Compute(IReadOnlyList<string> source, IReadOnlyList<string> target)
    {
        var start = 0;
        while (start < source.Count && start < target.Count && source[start] == target[start])
            start++;
        var endS = source.Count;
        var endT = target.Count;
        while (endS > start && endT > start && source[endS - 1] == target[endT - 1])
        {
            endS--;
            endT--;
        }

        var lines = new List<DiffLine>();
        for (var i = 0; i < start; i++)
            lines.Add(new(DiffLineKind.Same, source[i]));

        var n = endS - start;
        var m = endT - start;
        if ((long)n * m > MaxCells)
        {
            // Too large to align line by line: show the whole changed block from each side.
            lines.AddRange(target.Skip(start).Take(m).Select(l => new DiffLine(DiffLineKind.TargetOnly, l)));
            lines.AddRange(source.Skip(start).Take(n).Select(l => new DiffLine(DiffLineKind.SourceOnly, l)));
        }
        else
        {
            // Longest common subsequence over the changed middle.
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = source[start + i] == target[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

            int a = 0, b = 0;
            while (a < n || b < m)
            {
                if (a < n && b < m && source[start + a] == target[start + b])
                {
                    lines.Add(new(DiffLineKind.Same, source[start + a]));
                    a++;
                    b++;
                }
                else if (b < m && (a == n || lcs[a, b + 1] >= lcs[a + 1, b]))
                {
                    lines.Add(new(DiffLineKind.TargetOnly, target[start + b++]));
                }
                else
                {
                    lines.Add(new(DiffLineKind.SourceOnly, source[start + a++]));
                }
            }
        }

        for (var i = endS; i < source.Count; i++)
            lines.Add(new(DiffLineKind.Same, source[i]));
        return lines;
    }
}

/// <summary>Reads the structure of one database (tables, views, procedures, functions, triggers) into comparable text.</summary>
public static class SchemaSnapshotter
{
    public static async Task<SchemaSnapshot> CaptureAsync(SqlConnection connection, string database, CancellationToken ct)
    {
        var db = SqlText.Quote(database);
        // Each catalog view is read once for the whole database; results are grouped in memory.
        var sql = $"""
            SELECT s.name, o.name, RTRIM(o.type), m.definition
            FROM {db}.sys.objects o
            JOIN {db}.sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN {db}.sys.sql_modules m ON m.object_id = o.object_id
            WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'TR');

            SELECT s.name, t.name, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
                   cc.definition, dc.definition, dc.name, dc.is_system_named
            FROM {db}.sys.columns c
            JOIN {db}.sys.tables t ON t.object_id = c.object_id AND t.is_ms_shipped = 0
            JOIN {db}.sys.schemas s ON s.schema_id = t.schema_id
            JOIN {db}.sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN {db}.sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN {db}.sys.default_constraints dc ON dc.object_id = c.default_object_id;

            SELECT s.name, t.name, i.index_id, i.name, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint,
                   i.filter_definition, kc.is_system_named
            FROM {db}.sys.indexes i
            JOIN {db}.sys.tables t ON t.object_id = i.object_id AND t.is_ms_shipped = 0
            JOIN {db}.sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN {db}.sys.key_constraints kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
            WHERE i.type > 0 AND i.is_hypothetical = 0;

            SELECT s.name, t.name, ic.index_id, ic.key_ordinal, ic.is_descending_key, ic.is_included_column, c.name
            FROM {db}.sys.index_columns ic
            JOIN {db}.sys.tables t ON t.object_id = ic.object_id AND t.is_ms_shipped = 0
            JOIN {db}.sys.schemas s ON s.schema_id = t.schema_id
            JOIN {db}.sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            ORDER BY ic.object_id, ic.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;

            SELECT s.name, t.name, fk.object_id, fk.name, fk.is_system_named, fk.delete_referential_action_desc,
                   fk.update_referential_action_desc, rs.name, rt.name, pc.name, rc.name
            FROM {db}.sys.foreign_keys fk
            JOIN {db}.sys.tables t ON t.object_id = fk.parent_object_id AND t.is_ms_shipped = 0
            JOIN {db}.sys.schemas s ON s.schema_id = t.schema_id
            JOIN {db}.sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN {db}.sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN {db}.sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN {db}.sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN {db}.sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            ORDER BY fk.object_id, fkc.constraint_column_id;

            SELECT s.name, t.name, ck.name, ck.is_system_named, ck.definition
            FROM {db}.sys.check_constraints ck
            JOIN {db}.sys.tables t ON t.object_id = ck.parent_object_id AND t.is_ms_shipped = 0
            JOIN {db}.sys.schemas s ON s.schema_id = t.schema_id;
            """;

        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var objects = new List<(string Schema, string Name, string Type, string? Definition)>();
        while (await r.ReadAsync(ct))
            objects.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));

        var tables = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string> Lines(string schema, string table) =>
            tables.TryGetValue($"{schema}.{table}", out var l) ? l : tables[$"{schema}.{table}"] = [];
        string S(int i) => r.GetString(i);

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var type = SqlText.FormatType(S(3), r.GetInt16(4), r.GetByte(5), r.GetByte(6));
            var sb = new StringBuilder($"column {SqlText.Quote(S(2))} ");
            if (!r.IsDBNull(9))
                sb.Append("AS ").Append(r.GetString(9)); // computed: the expression defines it
            else
                sb.Append(type).Append(r.GetBoolean(7) ? " NULL" : " NOT NULL").Append(r.GetBoolean(8) ? " IDENTITY" : "");
            if (!r.IsDBNull(10))
                sb.Append(" DEFAULT ").Append(r.GetString(10)).Append(Named(r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) || r.GetBoolean(12)));
            Lines(S(0), S(1)).Add(sb.ToString());
        }

        await r.NextResultAsync(ct);
        var indexes = new Dictionary<(string, string, int), (string Name, string Type, bool Unique, bool Primary, bool UniqueConstraint, string? Filter, bool SystemNamed)>();
        while (await r.ReadAsync(ct))
            indexes[(S(0), S(1), r.GetInt32(2))] = (S(3), S(4), r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7),
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) || r.GetBoolean(9));

        await r.NextResultAsync(ct);
        var keyColumns = new Dictionary<(string, string, int), List<string>>();
        var includeColumns = new Dictionary<(string, string, int), List<string>>();
        while (await r.ReadAsync(ct))
        {
            var key = (S(0), S(1), r.GetInt32(2));
            var included = r.GetBoolean(5);
            var map = included ? includeColumns : keyColumns;
            if (!map.TryGetValue(key, out var list))
                map[key] = list = [];
            list.Add(SqlText.Quote(S(6)) + (!included && r.GetBoolean(4) ? " DESC" : ""));
        }

        foreach (var (key, ix) in indexes)
        {
            var kind = ix.Primary ? "primary key" : ix.UniqueConstraint ? "unique constraint" : ix.Unique ? "unique index" : "index";
            // An index or constraint with a generated name (PK__Orders__3213E83F...) would differ between databases for no real reason.
            var name = ix.Primary || ix.UniqueConstraint ? Named(ix.Name, ix.SystemNamed) : $" {SqlText.Quote(ix.Name)}";
            var sb = new StringBuilder($"{kind}{name} {ix.Type.ToLowerInvariant()} ({string.Join(", ", keyColumns.GetValueOrDefault(key) ?? [])})");
            if (includeColumns.TryGetValue(key, out var include))
                sb.Append($" include ({string.Join(", ", include)})");
            if (ix.Filter is not null)
                sb.Append(" where ").Append(ix.Filter);
            Lines(key.Item1, key.Item2).Add(sb.ToString());
        }

        await r.NextResultAsync(ct);
        var foreignKeys = new Dictionary<int, (string Schema, string Table, string? Name, bool SystemNamed, string OnDelete, string OnUpdate, string RefSchema, string RefTable, List<string> From, List<string> To)>();
        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt32(2);
            if (!foreignKeys.TryGetValue(id, out var fk))
                foreignKeys[id] = fk = (S(0), S(1), r.GetString(3), r.GetBoolean(4), S(5), S(6), S(7), S(8), [], []);
            fk.From.Add(SqlText.Quote(S(9)));
            fk.To.Add(SqlText.Quote(S(10)));
        }
        foreach (var fk in foreignKeys.Values)
            Lines(fk.Schema, fk.Table).Add(
                $"foreign key{Named(fk.Name, fk.SystemNamed)} ({string.Join(", ", fk.From)}) references {SqlText.Quote(fk.RefSchema)}.{SqlText.Quote(fk.RefTable)} ({string.Join(", ", fk.To)})" +
                $" on delete {fk.OnDelete.Replace('_', ' ')} on update {fk.OnUpdate.Replace('_', ' ')}");

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            Lines(S(0), S(1)).Add($"check{Named(S(2), r.GetBoolean(3))} {S(4)}");

        var result = new List<SnapshotObject>();
        foreach (var (schema, name, type, definition) in objects)
        {
            var kind = type switch { "U" => "Table", "V" => "View", "P" => "Procedure", "TR" => "Trigger", _ => "Function" };
            IReadOnlyList<string> lines = type == "U"
                ? Lines(schema, name).Order(StringComparer.Ordinal).ToList()
                : NormalizeDefinition(definition);
            result.Add(new SnapshotObject(kind, schema, name, lines));
        }
        return new SchemaSnapshot(result);
    }

    /// <summary>" [name]" for a constraint someone named, nothing for one SQL Server named itself.</summary>
    static string Named(string? name, bool systemNamed) => name is null || systemNamed ? "" : $" {SqlText.Quote(name)}";

    /// <summary>Line endings and trailing spaces differ between tools and editors without meaning anything.</summary>
    public static List<string> NormalizeDefinition(string? definition)
    {
        if (definition is null)
            return ["-- definition is encrypted or hidden from this login, so it cannot be compared"];
        var lines = definition.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
