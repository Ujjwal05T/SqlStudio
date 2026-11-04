using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlStudio.Services;

public sealed record ResultColumn(string Name, string TypeName);

public sealed class ResultSet
{
    public required IReadOnlyList<ResultColumn> Columns { get; init; }
    public required List<object?[]> Rows { get; init; }
    public bool Truncated { get; init; }

    /// <summary>The batch that produced this set, which result of the batch it was (0 = first) and the database it ran in. Used to work out whether it can be edited.</summary>
    public string? SourceSql { get; init; }
    public int SetIndex { get; init; }
    public string? Database { get; init; }
}

public interface IQuerySink
{
    void Message(string text, bool isError);
    void ResultSetReady(ResultSet set);
}

public sealed record ExecutionSummary(TimeSpan Elapsed, int ResultSets, int Errors, bool Cancelled);

public static class QueryExecutor
{
    public const int DefaultRowLimit = 200_000;
    const int MaxHexBytes = 4096;

    public static async Task<ExecutionSummary> ExecuteAsync(
        SqlConnection connection, string script, IQuerySink sink, int rowLimit, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var resultSets = 0;
        var errors = 0;
        var cancelled = false;

        void OnInfo(object _, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError err in e.Errors)
                sink.Message(Format(err), err.Class > 10);
        }

        connection.InfoMessage += OnInfo;
        try
        {
            foreach (var batch in BatchSplitter.Split(script))
            {
                for (var run = 0; run < batch.Repeat && !ct.IsCancellationRequested; run++)
                {
                    try
                    {
                        resultSets += await RunBatchAsync(connection, batch, sink, rowLimit, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                    catch (SqlException ex) when (ct.IsCancellationRequested && ex.Number == 0)
                    {
                        cancelled = true; // SqlClient reports an attention ack as a SqlException
                    }
                    catch (SqlException ex)
                    {
                        errors++;
                        foreach (SqlError err in ex.Errors)
                            sink.Message(Format(err, batch.StartLine), true);
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
            }
        }
        finally
        {
            connection.InfoMessage -= OnInfo;
        }

        return new ExecutionSummary(watch.Elapsed, resultSets, errors, cancelled);
    }

    static async Task<int> RunBatchAsync(
        SqlConnection connection, SqlBatch batch, IQuerySink sink, int rowLimit, CancellationToken ct)
    {
        await using var command = new SqlCommand(batch.Text, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        var count = 0;

        do
        {
            if (reader.FieldCount == 0)
                continue; // statements like UPDATE report through RecordsAffected below

            var columns = new ResultColumn[reader.FieldCount];
            for (var i = 0; i < columns.Length; i++)
                columns[i] = new ResultColumn(reader.GetName(i), reader.GetDataTypeName(i));

            var rows = new List<object?[]>();
            var truncated = false;
            var buffer = new object?[columns.Length];

            while (await reader.ReadAsync(ct))
            {
                if (rows.Count >= rowLimit)
                {
                    truncated = true;
                    command.Cancel(); // stop the server streaming rows we will never show
                    break;
                }

                reader.GetValues(buffer!);
                var row = new object?[buffer.Length];
                for (var i = 0; i < row.Length; i++)
                    row[i] = Normalize(buffer[i]);
                rows.Add(row);
            }

            sink.ResultSetReady(new ResultSet
            {
                Columns = columns, Rows = rows, Truncated = truncated,
                SourceSql = batch.Text, SetIndex = count, Database = connection.Database,
            });
            count++;

            if (truncated)
            {
                sink.Message($"Row limit reached: showing the first {rowLimit:N0} rows. Remaining results of this batch were skipped.", true);
                return count;
            }
        } while (await reader.NextResultAsync(ct));

        if (reader.RecordsAffected >= 0)
            sink.Message($"({reader.RecordsAffected} row{(reader.RecordsAffected == 1 ? "" : "s")} affected)", false);

        return count;
    }

    static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        byte[] bytes => ToHex(bytes),
        _ => value,
    };

    static string ToHex(byte[] bytes)
    {
        var shown = Math.Min(bytes.Length, MaxHexBytes);
        var sb = new StringBuilder(2 + shown * 2 + 1).Append("0x");
        for (var i = 0; i < shown; i++)
            sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        if (shown < bytes.Length)
            sb.Append('…');
        return sb.ToString();
    }

    static string Format(SqlError e, int? batchStartLine = null)
    {
        if (e.Class <= 10 && e.Number == 0)
            return e.Message; // PRINT output
        var line = e.LineNumber + (batchStartLine ?? 1) - 1;
        return $"Msg {e.Number}, Level {e.Class}, State {e.State}, Line {line}\n{e.Message}";
    }
}
