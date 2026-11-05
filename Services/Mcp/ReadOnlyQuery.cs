using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlStudio.Services.Mcp;

public sealed record QueryTable(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows, bool Truncated);

/// <summary>Runs a vetted SELECT for an AI and formats the answer as a compact text table.</summary>
public static class ReadOnlyQuery
{
    public const int DefaultRows = 200;
    public const int MaxRows = 1000;
    public const int MaxSqlLength = 20_000;
    const int MaxCellLength = 200;
    const int TimeoutSeconds = 30;

    /// <summary>
    /// Runs the query inside a transaction that is always rolled back, so nothing it did could be kept, and reads at most
    /// <paramref name="maxRows"/> rows of the first result. Throws <see cref="McpToolException"/> when the query is not allowed.
    /// </summary>
    public static async Task<QueryTable> RunAsync(SqlConnection connection, string sql, int maxRows, CancellationToken ct)
    {
        if (sql.Length > MaxSqlLength)
            throw new McpToolException($"The query is longer than {MaxSqlLength:N0} characters.");
        if (ReadOnlySql.Check(sql) is { } problem)
            throw new McpToolException(problem);

        maxRows = Math.Clamp(maxRows, 1, MaxRows);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = TimeoutSeconds };
            await using var reader = await command.ExecuteReaderAsync(ct);
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<string?[]>();
            var truncated = false;
            var buffer = new object[columns.Count];
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    command.Cancel(); // stop the server sending rows nobody will read
                    break;
                }
                reader.GetValues(buffer);
                rows.Add(buffer.Select(FormatCell).ToArray());
            }
            return new QueryTable(columns, rows, truncated);
        }
        finally
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // The connection was cancelled or closed under it: nothing to roll back.
            }
        }
    }

    public static string FormatCell(object? value)
    {
        var text = value switch
        {
            null or DBNull => "NULL",
            byte[] bytes => "0x" + Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 16))) + (bytes.Length > 16 ? "…" : ""),
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Replace("|", "\\|");
        return text.Length > MaxCellLength ? text[..MaxCellLength] + "…" : text;
    }

    /// <summary>A markdown table plus a one-line footer saying how many rows came back and whether the limit cut it short.</summary>
    public static string ToText(QueryTable table, int maxRows)
    {
        if (table.Columns.Count == 0)
            return "The query returned no result set.";
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", table.Columns.Select(c => c.Replace("|", "\\|"))) + " |");
        sb.AppendLine("|" + string.Concat(table.Columns.Select(_ => " --- |")));
        foreach (var row in table.Rows)
            sb.AppendLine("| " + string.Join(" | ", row) + " |");
        sb.Append(table.Truncated
            ? $"\n{table.Rows.Count:N0} rows shown. There are more: the limit is {Math.Clamp(maxRows, 1, MaxRows):N0} rows. Narrow the query or raise max_rows (up to {MaxRows:N0})."
            : $"\n{table.Rows.Count:N0} row{(table.Rows.Count == 1 ? "" : "s")}.");
        return sb.ToString();
    }
}
