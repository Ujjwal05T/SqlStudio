using System.Text;
using SqlStudio.Views;

namespace SqlStudio.Services;

/// <summary>Text for the clipboard, in the same form the grid shows (ISO dates, NULL), so what you copy is what you see.</summary>
public static class GridCopy
{
    public static string Cell(ResultSet set, GridRow row, int column) => CellFormatter.Format(row[column], set.Columns[column].TypeName);

    /// <summary>One line per row, values separated by tabs, so it pastes into Excel as cells.</summary>
    public static string Rows(ResultSet set, IEnumerable<GridRow> rows, bool withHeaders)
    {
        var sb = new StringBuilder();
        if (withHeaders)
            sb.AppendLine(string.Join('\t', set.Columns.Select(c => c.Name)));
        foreach (var row in rows)
            sb.AppendLine(string.Join('\t', Enumerable.Range(0, set.Columns.Count).Select(i => Tab(Cell(set, row, i)))));
        return sb.ToString().TrimEnd('\r', '\n');
    }

    public static string Column(ResultSet set, IEnumerable<GridRow> rows, int column, bool withHeader)
    {
        var lines = rows.Select(r => Cell(set, r, column));
        return string.Join(Environment.NewLine, withHeader ? lines.Prepend(set.Columns[column].Name) : lines);
    }

    public static string Json(ResultSet set, IEnumerable<GridRow> rows)
    {
        using var ms = new MemoryStream();
        ResultExporter.WriteJson(new ResultSet { Columns = set.Columns, Rows = rows.Select(r => r.Values).ToList() }, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>One INSERT per row. Pass the real table when known; otherwise a placeholder is written for you to replace.</summary>
    public static string Insert(ResultSet set, IEnumerable<GridRow> rows, EditTarget? target)
    {
        var table = target?.FullName ?? "[dbo].[TableName]";
        var columns = Enumerable.Range(0, set.Columns.Count)
            .Where(i => target is null || (!target.Columns[i].IsIdentity && !target.Columns[i].IsComputed && target.Columns[i].SourceColumn is not null))
            .ToList();
        var names = string.Join(", ", columns.Select(i => SqlText.Quote(target?.Columns[i].SourceColumn ?? set.Columns[i].Name)));
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var values = columns.Select(i => ChangeScripter.Literal(row[i], target?.Columns[i] ?? Untyped(set.Columns[i])));
            sb.AppendLine($"INSERT INTO {table} ({names}) VALUES ({string.Join(", ", values)});");
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    static EditColumn Untyped(ResultColumn column) => new(column.Name, column.TypeName, column.TypeName, 0, 0, true, false, false, false, column.Name);

    // A tab or line break inside a value would shift the cells when pasted.
    static string Tab(string text) => text.Replace('\t', ' ').Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
}
