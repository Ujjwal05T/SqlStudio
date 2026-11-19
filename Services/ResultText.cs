using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SqlStudio.Services;

public enum AgentFormat { Markdown, Json }

/// <param name="Server">Display name only. Addresses and logins are never put in text meant to be pasted elsewhere.</param>
/// <param name="TotalRows">Rows in the whole result, before any grid filter.</param>
/// <param name="Filter">The grid's row filter, when one is active.</param>
public sealed record AgentContext(string? Server, string? Database, string? Sql, int SetIndex, int TotalRows, string? Filter, bool ResultWasCut);

/// <param name="Cut">True when fewer rows were included than the grid shows.</param>
public sealed record AgentCopy(string Text, int RowsIncluded, int RowsAvailable, bool Cut);

/// <summary>
/// The whole result as text. The plain formats copy everything shown; the "for an agent" formats add what a reader who
/// cannot see the screen needs in order to check the answer: the query, where it ran, the column types, how many rows
/// there are and whether anything was left out.
/// </summary>
public static class ResultText
{
    /// <summary>Text for an agent is kept to a usable size; the note in the text says so when rows are left out.</summary>
    public const int AgentRowLimit = 1000;
    public const int AgentCellLimit = 500;

    // ---- plain formats (everything that is shown) ----

    public static string Markdown(ResultSet set, IReadOnlyList<GridRow> rows, int? maxCellChars = null)
    {
        var names = set.Columns.Select(c => MarkdownCell(c.Name)).ToList();
        var sb = new StringBuilder();
        sb.Append("| ").AppendJoin(" | ", names).AppendLine(" |");
        sb.Append("| ").AppendJoin(" | ", names.Select(_ => "---")).AppendLine(" |");
        foreach (var row in rows)
        {
            sb.Append("| ");
            for (var i = 0; i < set.Columns.Count; i++)
            {
                if (i > 0)
                    sb.Append(" | ");
                sb.Append(MarkdownCell(Shorten(GridCopy.Cell(set, row, i), maxCellChars)));
            }
            sb.AppendLine(" |");
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>Standard CSV for pasting: a NULL is an empty field, and commas, quotes and line breaks are quoted.</summary>
    public static string Csv(ResultSet set, IReadOnlyList<GridRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', set.Columns.Select(c => CsvField(c.Name))));
        foreach (var row in rows)
            sb.AppendLine(string.Join(',', Enumerable.Range(0, set.Columns.Count)
                .Select(i => row[i] is null ? "" : CsvField(GridCopy.Cell(set, row, i)))));
        return sb.ToString().TrimEnd('\r', '\n');
    }

    // ---- for an AI agent ----

    public static AgentCopy ForAgent(ResultSet set, IReadOnlyList<GridRow> rows, AgentContext context, AgentFormat format)
    {
        var included = rows.Count > AgentRowLimit ? rows.Take(AgentRowLimit).ToList() : rows;
        var cut = included.Count < rows.Count;

        var sb = new StringBuilder();
        sb.AppendLine("# SQL Server query result").AppendLine();
        if (!string.IsNullOrWhiteSpace(context.Server))
            sb.AppendLine($"- Server: {context.Server}");
        if (!string.IsNullOrWhiteSpace(context.Database))
            sb.AppendLine($"- Database: {context.Database}");
        sb.AppendLine($"- Rows: {RowsSentence(included.Count, rows.Count, context, cut)}");
        sb.AppendLine("- Columns: " + string.Join(", ", set.Columns.Select(c => $"{c.Name} ({c.TypeName})")));
        if (context.SetIndex > 0)
            sb.AppendLine($"- This is result {context.SetIndex + 1} of the query below, which returns several results.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(context.Sql))
        {
            var sql = context.Sql.Trim();
            var fence = new string('`', Math.Max(3, LongestRun(sql, '`') + 1)); // a fence the SQL itself cannot close
            sb.AppendLine("Query:").AppendLine().AppendLine(fence + "sql").AppendLine(sql).AppendLine(fence).AppendLine();
        }

        if (format == AgentFormat.Markdown)
        {
            sb.AppendLine($"Values are shown as in the grid: NULL means a database NULL, dates are ISO 8601, bit is 1 or 0. Text longer than {AgentCellLimit} characters is shortened and ends with …").AppendLine();
            sb.AppendLine("Result:").AppendLine().Append(Markdown(set, included, AgentCellLimit));
        }
        else
        {
            sb.AppendLine("Values are JSON: NULL is null, numbers are numbers, and dates and other types are strings in the grid's format. One object per row.").AppendLine();
            sb.AppendLine("Result:").AppendLine().AppendLine("```json").AppendLine(CompactJson(set, included)).Append("```");
        }
        return new AgentCopy(sb.ToString().TrimEnd('\r', '\n'), included.Count, rows.Count, cut);
    }

    static string RowsSentence(int included, int available, AgentContext c, bool cut)
    {
        var sb = new StringBuilder();
        if (cut)
            sb.Append($"first {included:N0} of {available:N0} (shortened to keep this text a usable size; the rest were not included)");
        else
            sb.Append($"{available:N0}");

        if (!string.IsNullOrWhiteSpace(c.Filter) && available != c.TotalRows)
            sb.Append($", filtered by \"{c.Filter}\" in the grid ({available:N0} of {c.TotalRows:N0} rows match)");
        else if (!cut)
            sb.Append(c.ResultWasCut ? "" : " (complete result)");

        if (c.ResultWasCut)
            sb.Append(". The query returned more rows than the app loads, so this is only the first part of it");
        return sb.ToString();
    }

    /// <summary>The same typed values as the file export, one object per line instead of indented, which is far cheaper to read in bulk.</summary>
    static string CompactJson(ResultSet set, IReadOnlyList<GridRow> rows)
    {
        using var doc = JsonDocument.Parse(GridCopy.Json(set, rows));
        var lines = doc.RootElement.EnumerateArray().Select(e => "  " + JsonSerializer.Serialize(e));
        return "[\n" + string.Join(",\n", lines) + "\n]";
    }

    // ---- helpers ----

    static string MarkdownCell(string text) =>
        text.Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");

    static string CsvField(string text) =>
        text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    static string Shorten(string text, int? max) =>
        max is { } m && text.Length > m ? text[..m] + "…" : text;

    static int LongestRun(string text, char c)
    {
        int best = 0, run = 0;
        foreach (var ch in text)
        {
            run = ch == c ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        return best;
    }
}

/// <summary>What the grid says after a copy, so it is always clear what ended up on the clipboard.</summary>
public static class CopyFeedback
{
    public static string Rows(int count) => $"{count.ToString("N0", CultureInfo.InvariantCulture)} row{(count == 1 ? "" : "s")}";

    public static string Number(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

    public static string Values(int count) => $"{count.ToString("N0", CultureInfo.InvariantCulture)} value{(count == 1 ? "" : "s")}";

    /// <summary>A rough size for text that is going into a model: about four characters per token.</summary>
    public static string Size(string text)
    {
        if (text.Length < 1500)
            return "";
        var tokens = (text.Length + 3) / 4;
        var shown = tokens < 1000 ? tokens.ToString("N0", CultureInfo.InvariantCulture) : (tokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return $" · about {shown} tokens";
    }

    public static string Message(string what, string text) => $"Copied {what}{Size(text)}";
}
