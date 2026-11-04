using System.Globalization;
using Avalonia.Data.Converters;

namespace SqlStudio.Views;

/// <summary>Formats result cells the way SQL Server people expect to read them (ISO dates, bit as 1/0, NULL).</summary>
public sealed class CellFormatter : IValueConverter
{
    public static readonly CellFormatter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Format(value, parameter as string);

    /// <param name="sqlType">The column's SQL type name (e.g. "datetime"), which decides how dates are written.</param>
    public static string Format(object? value, string? sqlType) => value switch
    {
        null => "NULL",
        DateTime dt => FormatDateTime(dt, sqlType),
        DateTimeOffset dto => TrimFraction(dto.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture)),
        TimeSpan ts => TrimFraction(ts.ToString("c", CultureInfo.InvariantCulture)),
        bool b => b ? "1" : "0",
        Guid g => g.ToString().ToUpperInvariant(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Numbers read better right-aligned so digits line up.</summary>
    public static bool IsNumeric(string sqlType) =>
        sqlType is "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric" or "float" or "real" or "money" or "smallmoney";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    static string FormatDateTime(DateTime dt, string? sqlType) => sqlType switch
    {
        "date" => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "smalldatetime" => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        "datetime" => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        _ => TrimFraction(dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
    };

    // datetime2/time carry up to 7 fractional digits; drop trailing zeros but keep at least 3.
    static string TrimFraction(string text)
    {
        var dot = text.IndexOf('.');
        if (dot < 0)
            return text;
        var end = dot + 1;
        while (end < text.Length && char.IsDigit(text[end]))
            end++;
        var digits = end - dot - 1;
        var keep = digits;
        while (keep > 3 && text[dot + keep] == '0')
            keep--;
        return text[..(dot + 1 + keep)] + text[end..];
    }
}
