using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlStudio.Services;

public enum ChangeKind { Update, Insert, Delete }

public sealed record SqlParam(string Name, object? Value, EditColumn Column);

/// <summary>One statement to run. Sql uses parameters (what runs); DisplaySql has the values written in (what the user reviews).</summary>
public sealed record PendingChange(ChangeKind Kind, GridRow Row, string Sql, IReadOnlyList<SqlParam> Params, string DisplaySql, bool ReturnsIdentity);

/// <summary>Turns the edits in a grid into UPDATE, INSERT and DELETE statements for the table behind it.</summary>
public static partial class ChangeScripter
{
    [GeneratedRegex(@"@[pko]\d+\b")]
    private static partial Regex Token();

    /// <param name="checkOldValues">Add the cell's old value to the WHERE clause, so a row someone else changed meanwhile is not overwritten.</param>
    public static List<PendingChange> Build(EditTarget target, IEnumerable<GridRow> rows, bool checkOldValues)
    {
        var all = rows.ToList();
        var changes = new List<PendingChange>();
        // Updates first, then inserts, then deletes: the order people expect to read in the review.
        foreach (var row in all.Where(r => r.State == RowState.Modified))
            changes.Add(Update(target, row, checkOldValues));
        foreach (var row in all.Where(r => r.State == RowState.Added))
            changes.Add(Insert(target, row));
        foreach (var row in all.Where(r => r.State == RowState.Deleted))
            changes.Add(Delete(target, row));
        return changes;
    }

    static PendingChange Update(EditTarget target, GridRow row, bool checkOldValues)
    {
        var parameters = new List<SqlParam>();
        var sets = new List<string>();
        foreach (var column in row.ChangedColumns.Order())
        {
            var name = $"@p{parameters.Count}";
            parameters.Add(new SqlParam(name, row[column], target.Columns[column]));
            sets.Add($"{SqlText.Quote(target.Columns[column].SourceColumn!)} = {name}");
        }

        var where = Where(target, row, parameters);
        if (checkOldValues)
            foreach (var column in row.ChangedColumns.Order().Where(c => target.Columns[c].CanVerify))
                where.Add(OldValueCondition(target, row, column, parameters));

        var sql = $"UPDATE {target.FullName} SET {string.Join(", ", sets)} WHERE {string.Join(" AND ", where)};";
        return new PendingChange(ChangeKind.Update, row, sql, parameters, Render(sql, parameters), false);
    }

    static PendingChange Insert(EditTarget target, GridRow row)
    {
        var parameters = new List<SqlParam>();
        var columns = new List<string>();
        var values = new List<string>();
        foreach (var column in row.AssignedColumns.Order().Where(c => target.Columns[c].CanEditNew))
        {
            var name = $"@p{parameters.Count}";
            parameters.Add(new SqlParam(name, row[column], target.Columns[column]));
            columns.Add(SqlText.Quote(target.Columns[column].SourceColumn!));
            values.Add(name);
        }

        var hasIdentity = target.IdentityColumn >= 0;
        var sql = columns.Count == 0
            ? $"INSERT INTO {target.FullName} DEFAULT VALUES;"
            : $"INSERT INTO {target.FullName} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)});";
        // The new identity value comes back so the grid can show it.
        if (hasIdentity)
            sql += " SELECT SCOPE_IDENTITY();";
        return new PendingChange(ChangeKind.Insert, row, sql, parameters, Render(sql, parameters), hasIdentity);
    }

    static PendingChange Delete(EditTarget target, GridRow row)
    {
        var parameters = new List<SqlParam>();
        var sql = $"DELETE FROM {target.FullName} WHERE {string.Join(" AND ", Where(target, row, parameters))};";
        return new PendingChange(ChangeKind.Delete, row, sql, parameters, Render(sql, parameters), false);
    }

    /// <summary>Finds the row by its key, using the key values as they were before any edit.</summary>
    static List<string> Where(EditTarget target, GridRow row, List<SqlParam> parameters)
    {
        var conditions = new List<string>();
        foreach (var key in target.KeyColumns)
        {
            var name = $"@k{key}";
            parameters.Add(new SqlParam(name, row.OriginalValue(key), target.Columns[key]));
            conditions.Add($"{SqlText.Quote(target.Columns[key].SourceColumn!)} = {name}");
        }
        return conditions;
    }

    static string OldValueCondition(EditTarget target, GridRow row, int column, List<SqlParam> parameters)
    {
        var quoted = SqlText.Quote(target.Columns[column].SourceColumn!);
        var original = row.OriginalValue(column);
        if (original is null)
            return $"{quoted} IS NULL";
        var name = $"@o{column}";
        parameters.Add(new SqlParam(name, original, target.Columns[column]));
        return $"{quoted} = {name}";
    }

    /// <summary>The statement with each parameter replaced by its value, for reading and for copying into another tool.</summary>
    static string Render(string sql, IReadOnlyList<SqlParam> parameters)
    {
        var byName = parameters.ToDictionary(p => p.Name);
        return Token().Replace(sql, m => byName.TryGetValue(m.Value, out var p) ? Literal(p.Value, p.Column) : m.Value);
    }

    public static string Literal(object? value, EditColumn column)
    {
        var inv = CultureInfo.InvariantCulture;
        return value switch
        {
            null => "NULL",
            bool b => b ? "1" : "0",
            string s => SqlText.Literal(s),
            DateTime dt => column.TypeName switch
            {
                "date" => $"'{dt:yyyy-MM-dd}'",
                "datetime" or "smalldatetime" => $"'{dt.ToString("yyyy-MM-ddTHH:mm:ss.fff", inv)}'",
                _ => $"'{dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", inv)}'",
            },
            DateTimeOffset dto => $"'{dto.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", inv)}'",
            TimeSpan ts => $"'{ts.ToString("c", inv)}'",
            Guid g => $"'{g.ToString().ToUpperInvariant()}'",
            double d => d.ToString("R", inv),
            float f => f.ToString("R", inv),
            IFormattable f => f.ToString(null, inv),
            _ => SqlText.Literal(value.ToString() ?? ""),
        };
    }

    /// <summary>All statements as one readable script, wrapped in a transaction the way they will run.</summary>
    public static string Script(IReadOnlyList<PendingChange> changes)
    {
        var sb = new StringBuilder("BEGIN TRANSACTION;\n\n");
        foreach (var change in changes)
            sb.AppendLine(change.DisplaySql.Replace(" SELECT SCOPE_IDENTITY();", ""));
        sb.AppendLine().Append("COMMIT TRANSACTION;");
        return sb.ToString();
    }

    public static SqlParameter ToSqlParameter(SqlParam p)
    {
        var parameter = new SqlParameter { ParameterName = p.Name, Value = p.Value ?? DBNull.Value };
        var column = p.Column;
        switch (column.TypeName)
        {
            case "int": parameter.SqlDbType = SqlDbType.Int; break;
            case "bigint": parameter.SqlDbType = SqlDbType.BigInt; break;
            case "smallint": parameter.SqlDbType = SqlDbType.SmallInt; break;
            case "tinyint": parameter.SqlDbType = SqlDbType.TinyInt; break;
            case "bit": parameter.SqlDbType = SqlDbType.Bit; break;
            case "decimal" or "numeric":
                parameter.SqlDbType = SqlDbType.Decimal;
                parameter.Precision = (byte)column.Precision;
                parameter.Scale = (byte)column.Scale;
                break;
            case "money": parameter.SqlDbType = SqlDbType.Money; break;
            case "smallmoney": parameter.SqlDbType = SqlDbType.SmallMoney; break;
            case "float": parameter.SqlDbType = SqlDbType.Float; break;
            case "real": parameter.SqlDbType = SqlDbType.Real; break;
            case "date": parameter.SqlDbType = SqlDbType.Date; break;
            case "datetime": parameter.SqlDbType = SqlDbType.DateTime; break;
            case "smalldatetime": parameter.SqlDbType = SqlDbType.SmallDateTime; break;
            case "datetime2": parameter.SqlDbType = SqlDbType.DateTime2; parameter.Scale = (byte)column.Scale; break;
            case "datetimeoffset": parameter.SqlDbType = SqlDbType.DateTimeOffset; parameter.Scale = (byte)column.Scale; break;
            case "time": parameter.SqlDbType = SqlDbType.Time; parameter.Scale = (byte)column.Scale; break;
            case "uniqueidentifier": parameter.SqlDbType = SqlDbType.UniqueIdentifier; break;
            // Matching the column's own text type keeps the key lookup able to use its index.
            case "char" or "varchar" or "text": parameter.SqlDbType = SqlDbType.VarChar; parameter.Size = -1; break;
            default: parameter.SqlDbType = SqlDbType.NVarChar; parameter.Size = -1; break;
        }
        return parameter;
    }

    public sealed class ConflictException(string message) : Exception(message);

    /// <summary>Runs every change in one transaction. Any failure, or a statement that does not touch exactly one row, rolls everything back.</summary>
    public static async Task<Dictionary<GridRow, object?>> ApplyAsync(SqlConnection connection, IReadOnlyList<PendingChange> changes, CancellationToken ct)
    {
        var identities = new Dictionary<GridRow, object?>();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var change in changes)
            {
                await using var command = new SqlCommand(change.Sql, connection, transaction) { CommandTimeout = 60 };
                foreach (var p in change.Params)
                    command.Parameters.Add(ToSqlParameter(p));

                if (change.ReturnsIdentity)
                {
                    identities[change.Row] = await command.ExecuteScalarAsync(ct);
                    continue;
                }

                var affected = await command.ExecuteNonQueryAsync(ct);
                if (affected != 1)
                    throw new ConflictException(change.Kind == ChangeKind.Insert
                        ? "An insert didn't add exactly one row."
                        : $"{(change.Kind == ChangeKind.Update ? "An update" : "A delete")} changed {affected} rows instead of 1. " +
                          "The row was probably changed or deleted by someone else after you loaded it. Run the query again and redo the edit.");
            }
            await transaction.CommitAsync(ct);
            return identities;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
