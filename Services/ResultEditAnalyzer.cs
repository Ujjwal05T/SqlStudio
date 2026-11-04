using Microsoft.Data.SqlClient;

namespace SqlStudio.Services;

/// <summary>One column of a query's result as SQL Server describes it (sp_describe_first_result_set with browse information).</summary>
public sealed record DescribedColumn(
    string? Name, bool IsHidden, bool IsNullable, string SystemTypeName, int Precision, int Scale,
    string? SourceDatabase, string? SourceSchema, string? SourceTable, string? SourceColumn, bool IsIdentity, bool IsComputed);

public sealed record EditColumn(
    string Name, string TypeName, string SystemTypeName, int Precision, int Scale, bool Nullable,
    bool IsKey, bool IsIdentity, bool IsComputed, string? SourceColumn)
{
    /// <summary>Key columns identify the row, so they are not edited on existing rows.</summary>
    public bool CanEditExisting => SourceColumn is not null && !IsKey && !IsIdentity && !IsComputed && ValueParser.IsEditableType(TypeName);

    /// <summary>A new row may set any ordinary column, including key columns that are not generated.</summary>
    public bool CanEditNew => SourceColumn is not null && !IsIdentity && !IsComputed && ValueParser.IsEditableType(TypeName);

    /// <summary>Floating point and legacy large-text columns cannot be compared reliably, so they are left out of the "nobody else changed this" check.</summary>
    public bool CanVerify => SourceColumn is not null && TypeName is not ("float" or "real" or "text" or "ntext") && ValueParser.IsEditableType(TypeName);
}

/// <summary>The one table a result comes from, and how each result column maps onto it.</summary>
public sealed record EditTarget(string Database, string Schema, string Table, IReadOnlyList<EditColumn> Columns, IReadOnlyList<int> KeyColumns)
{
    public string FullName => $"{SqlText.Quote(Database)}.{SqlText.Quote(Schema)}.{SqlText.Quote(Table)}";
    public string DisplayName => $"{Schema}.{Table}";
    public int IdentityColumn => Columns.ToList().FindIndex(c => c.IsIdentity);
}

/// <summary>Either a table that can be edited, or the plain-language reason it can't.</summary>
public sealed record EditAnalysis(EditTarget? Target, string? Reason);

public static class ResultEditAnalyzer
{
    /// <summary>
    /// Decides whether a result can be edited, from SQL Server's description of it and the key of the table it reads.
    /// Pure logic, so every refusal reason can be checked without a server.
    /// </summary>
    public static EditAnalysis Analyze(IReadOnlyList<DescribedColumn> described, ResultSet set, string? tableType, IReadOnlyList<string>? keyColumns)
    {
        var visible = described.Where(c => !c.IsHidden).ToList();
        if (visible.Count != set.Columns.Count)
            return new(null, "The result could not be matched to its table, so it can't be edited.");

        var tables = visible.Where(c => c.SourceTable is not null)
            .Select(c => (c.SourceDatabase, c.SourceSchema, c.SourceTable)).Distinct().ToList();
        if (tables.Count == 0)
            return new(null, "This result isn't read from a table, so it can't be edited.");
        if (tables.Count > 1)
            return new(null, $"This result combines columns from {tables.Count} tables, so it can't be edited. Query a single table to edit it.");

        var (db, schema, table) = tables[0];
        if (db is null || schema is null || table is null)
            return new(null, "The table behind this result could not be identified.");
        if (tableType != "U")
            return new(null, $"{schema}.{table} is not an ordinary table (views can't be edited here), so it can't be edited.");
        if (keyColumns is null || keyColumns.Count == 0)
            return new(null, $"{schema}.{table} has no primary key or unique key, so its rows can't be identified safely and it can't be edited.");

        var columns = new List<EditColumn>();
        for (var i = 0; i < visible.Count; i++)
        {
            var d = visible[i];
            var isKey = d.SourceColumn is not null && keyColumns.Contains(d.SourceColumn, StringComparer.OrdinalIgnoreCase);
            columns.Add(new EditColumn(set.Columns[i].Name, ValueParser.BaseType(d.SystemTypeName), d.SystemTypeName, d.Precision, d.Scale,
                d.IsNullable, isKey, d.IsIdentity, d.IsComputed, d.SourceColumn));
        }

        var keyIndexes = new List<int>();
        var missing = new List<string>();
        foreach (var key in keyColumns)
        {
            var index = columns.FindIndex(c => c.IsKey && string.Equals(c.SourceColumn, key, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                missing.Add(key);
            else
                keyIndexes.Add(index);
        }
        if (missing.Count > 0)
            return new(null, $"Include the key column{(missing.Count == 1 ? "" : "s")} {string.Join(", ", missing)} in the query so each row can be identified (SELECT * does).");

        return new(new EditTarget(db, schema, table, columns, keyIndexes), null);
    }

    /// <summary>The first primary key, otherwise the first unique index whose columns cannot be NULL.</summary>
    public static IReadOnlyList<string> PickKey(IEnumerable<(int IndexId, bool IsPrimary, string Column, bool Nullable)> rows)
    {
        foreach (var group in rows.GroupBy(r => r.IndexId))
        {
            var columns = group.ToList();
            if (columns[0].IsPrimary || columns.All(c => !c.Nullable))
                return columns.Select(c => c.Column).ToList();
        }
        return [];
    }

    /// <summary>Asks the server about the query that produced a result and the key of the table behind it.</summary>
    public static async Task<EditAnalysis> AnalyzeAsync(SqlConnection connection, ResultSet set, CancellationToken ct)
    {
        if (set.SourceSql is null)
            return new(null, "This result can't be edited.");
        if (set.SetIndex != 0)
            return new(null, "Only the first result of a query can be edited. Run the query on its own to edit this one.");

        List<DescribedColumn> described;
        try
        {
            described = await DescribeAsync(connection, set.SourceSql, ct);
        }
        catch (SqlException ex)
        {
            return new(null, "SQL Server couldn't analyze this query for editing: " + ex.Message);
        }

        var first = described.FirstOrDefault(c => c.SourceTable is not null);
        if (first?.SourceDatabase is null || first.SourceSchema is null || first.SourceTable is null)
            return Analyze(described, set, null, null);

        var (type, key) = await ReadKeyAsync(connection, first.SourceDatabase, first.SourceSchema, first.SourceTable, ct);
        return Analyze(described, set, type, key);
    }

    static async Task<List<DescribedColumn>> DescribeAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand("EXEC sys.sp_describe_first_result_set @tsql = @t, @params = NULL, @browse_information_mode = 1", connection);
        command.Parameters.AddWithValue("@t", sql);
        await using var r = await command.ExecuteReaderAsync(ct);

        string? Str(string column) => r.IsDBNull(r.GetOrdinal(column)) ? null : Convert.ToString(r.GetValue(r.GetOrdinal(column)));
        bool Flag(string column) => !r.IsDBNull(r.GetOrdinal(column)) && Convert.ToBoolean(r.GetValue(r.GetOrdinal(column)));
        int Num(string column) => r.IsDBNull(r.GetOrdinal(column)) ? 0 : Convert.ToInt32(r.GetValue(r.GetOrdinal(column)));

        var list = new List<DescribedColumn>();
        while (await r.ReadAsync(ct))
            list.Add(new DescribedColumn(Str("name"), Flag("is_hidden"), Flag("is_nullable"), Str("system_type_name") ?? "", Num("precision"), Num("scale"),
                Str("source_database"), Str("source_schema"), Str("source_table"), Str("source_column"), Flag("is_identity_column"), Flag("is_computed_column")));
        return list;
    }

    static async Task<(string? Type, IReadOnlyList<string> Key)> ReadKeyAsync(
        SqlConnection connection, string database, string schema, string table, CancellationToken ct)
    {
        var db = SqlText.Quote(database);
        var sql = $"""
            SELECT RTRIM(o.type) FROM {db}.sys.objects o WHERE o.object_id = OBJECT_ID(@full);

            SELECT i.index_id, i.is_primary_key, c.name, c.is_nullable
            FROM {db}.sys.indexes i
            JOIN {db}.sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN {db}.sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@full) AND i.is_unique = 1 AND i.is_disabled = 0 AND i.has_filter = 0
              AND ic.is_included_column = 0 AND ic.key_ordinal > 0
            ORDER BY i.is_primary_key DESC, i.index_id, ic.key_ordinal;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@full", $"{db}.{SqlText.Quote(schema)}.{SqlText.Quote(table)}");
        await using var r = await command.ExecuteReaderAsync(ct);

        string? type = null;
        if (await r.ReadAsync(ct) && !r.IsDBNull(0))
            type = r.GetString(0);

        var rows = new List<(int, bool, string, bool)>();
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add((r.GetInt32(0), r.GetBoolean(1), r.GetString(2), r.GetBoolean(3)));
        return (type, PickKey(rows));
    }
}
