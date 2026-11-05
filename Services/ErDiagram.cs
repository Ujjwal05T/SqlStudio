using Microsoft.Data.SqlClient;

namespace SqlStudio.Services;

/// <summary>
/// <see cref="Change"/> is set only in a schema-diff picture: where the column exists, or whether it differs.
/// <see cref="DisplayOverride"/> replaces the drawn type, e.g. to show both sides of a changed type.
/// </summary>
public sealed record ErColumn(string Name, string Type, bool Nullable, bool IsPrimaryKey, bool IsForeignKey, DiffStatus? Change = null, string? DisplayOverride = null)
{
    /// <summary>The type as drawn: a trailing "?" marks a column that allows NULL.</summary>
    public string DisplayType => DisplayOverride ?? (Nullable ? Type + "?" : Type);
}

/// <summary>
/// A box in the diagram. Tables list their columns; other objects (views, procedures, functions) have no columns and
/// show <see cref="Subtitle"/> instead. <see cref="IsFocus"/> marks the object a dependency view was opened from.
/// </summary>
public sealed record ErTable(string Schema, string Name, IReadOnlyList<ErColumn> Columns, string Kind = "table", string? Subtitle = null, bool IsFocus = false, DiffStatus? Change = null, string? TitleOverride = null)
{
    public string Id => $"{Schema}.{Name}";
    public string Title => TitleOverride ?? Id;
}

/// <summary>A declared foreign key: <see cref="FromId"/> (the child) references <see cref="ToId"/> (the parent).</summary>
public sealed record ErRelation(string Name, string FromId, string ToId, IReadOnlyList<string> FromColumns, IReadOnlyList<string> ToColumns, DiffStatus? Change = null);

public sealed record ErModel(IReadOnlyList<ErTable> Tables, IReadOnlyList<ErRelation> Relations)
{
    public static ErModel Empty { get; } = new([], []);
}

/// <summary>Reads the tables, columns, primary keys and declared foreign keys of one database.</summary>
public static class ErCatalog
{
    public static async Task<ErModel> LoadAsync(SqlConnection connection, string database, CancellationToken ct = default)
    {
        var db = SqlText.Quote(database);
        var tables = await ReadTablesAsync(connection, db, ct);
        var relations = await ReadRelationsAsync(connection, db, tables.Select(t => t.Id).ToHashSet(), ct);
        return new ErModel(tables, relations);
    }

    static async Task<List<ErTable>> ReadTablesAsync(SqlConnection connection, string db, CancellationToken ct)
    {
        var sql = $"""
            SELECT s.name, t.name, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable,
                   CASE WHEN EXISTS (SELECT 1 FROM {db}.sys.index_columns ic
                                     JOIN {db}.sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                                     WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id)
                        THEN 1 ELSE 0 END,
                   CASE WHEN EXISTS (SELECT 1 FROM {db}.sys.foreign_key_columns fc
                                     WHERE fc.parent_object_id = c.object_id AND fc.parent_column_id = c.column_id)
                        THEN 1 ELSE 0 END
            FROM {db}.sys.tables t
            JOIN {db}.sys.schemas s ON s.schema_id = t.schema_id
            JOIN {db}.sys.columns c ON c.object_id = t.object_id
            JOIN {db}.sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id
            """;

        var tables = new List<ErTable>();
        string? schema = null, table = null;
        var columns = new List<ErColumn>();
        void Flush()
        {
            if (schema is not null)
                tables.Add(new ErTable(schema, table!, columns.ToList()));
            columns.Clear();
        }

        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (r.GetString(0) != schema || r.GetString(1) != table)
            {
                Flush();
                (schema, table) = (r.GetString(0), r.GetString(1));
            }
            columns.Add(new ErColumn(
                r.GetString(2), FormatType(r.GetString(3), r.GetInt16(4), r.GetByte(5), r.GetByte(6)),
                r.GetBoolean(7), r.GetInt32(8) == 1, r.GetInt32(9) == 1));
        }
        Flush();
        return tables;
    }

    static async Task<List<ErRelation>> ReadRelationsAsync(SqlConnection connection, string db, HashSet<string> tableIds, CancellationToken ct)
    {
        var sql = $"""
            SELECT fk.object_id, fk.name, ps.name, pt.name, rs.name, rt.name, pc.name, rc.name
            FROM {db}.sys.foreign_keys fk
            JOIN {db}.sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN {db}.sys.tables pt ON pt.object_id = fk.parent_object_id
            JOIN {db}.sys.schemas ps ON ps.schema_id = pt.schema_id
            JOIN {db}.sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN {db}.sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN {db}.sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN {db}.sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            ORDER BY fk.object_id, fkc.constraint_column_id
            """;

        var relations = new List<ErRelation>();
        int? current = null;
        string name = "", from = "", to = "";
        List<string> fromColumns = [], toColumns = [];
        void Flush()
        {
            if (current is not null && tableIds.Contains(from) && tableIds.Contains(to))
                relations.Add(new ErRelation(name, from, to, fromColumns, toColumns));
            fromColumns = [];
            toColumns = [];
        }

        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (r.GetInt32(0) != current)
            {
                Flush();
                current = r.GetInt32(0);
                name = r.GetString(1);
                from = $"{r.GetString(2)}.{r.GetString(3)}";
                to = $"{r.GetString(4)}.{r.GetString(5)}";
            }
            fromColumns.Add(r.GetString(6));
            toColumns.Add(r.GetString(7));
        }
        Flush();
        return relations;
    }

    /// <summary>The type as written in a column definition, e.g. nvarchar(50), decimal(18,2), varchar(max).</summary>
    public static string FormatType(string type, int maxLength, int precision, int scale) => type switch
    {
        "nvarchar" or "nchar" => maxLength == -1 ? $"{type}(max)" : $"{type}({maxLength / 2})",
        "varchar" or "char" or "varbinary" or "binary" => maxLength == -1 ? $"{type}(max)" : $"{type}({maxLength})",
        "decimal" or "numeric" => $"{type}({precision},{scale})",
        _ => type,
    };
}
