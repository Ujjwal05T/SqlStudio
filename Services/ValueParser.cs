using System.Globalization;

namespace SqlStudio.Services;

/// <summary>Turns what the user typed into a cell into the .NET value SQL Server expects for that column type.</summary>
public static class ValueParser
{
    static readonly HashSet<string> Editable = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "bigint", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "smallmoney", "float", "real",
        "date", "datetime", "smalldatetime", "datetime2", "datetimeoffset", "time", "uniqueidentifier",
        "char", "varchar", "text", "nchar", "nvarchar", "ntext",
    };

    /// <summary>"nvarchar(50)" becomes "nvarchar".</summary>
    public static string BaseType(string systemTypeName)
    {
        var paren = systemTypeName.IndexOf('(');
        return (paren >= 0 ? systemTypeName[..paren] : systemTypeName).Trim().ToLowerInvariant();
    }

    /// <summary>Binary, XML, spatial and other special types are shown but not edited: a text box cannot hold them safely.</summary>
    public static bool IsEditableType(string typeName) => Editable.Contains(BaseType(typeName));

    public static bool IsText(string typeName) => BaseType(typeName) is "char" or "varchar" or "text" or "nchar" or "nvarchar" or "ntext";

    public static bool TryParse(string text, string typeName, bool nullable, out object? value, out string error)
    {
        value = null;
        error = "";
        var type = BaseType(typeName);
        var trimmed = text.Trim();

        if (IsText(type))
        {
            value = text; // empty text is an empty string; use "Set NULL" for NULL
            return true;
        }

        if (trimmed.Length == 0 || trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            if (nullable)
                return true;
            error = "This column doesn't allow NULL, so a value is required.";
            return false;
        }

        var inv = CultureInfo.InvariantCulture;
        switch (type)
        {
            case "int":
                return Integer(trimmed, int.MinValue, int.MaxValue, "int", v => (int)v, out value, out error);
            case "bigint":
                return Integer(trimmed, long.MinValue, long.MaxValue, "bigint", v => v, out value, out error);
            case "smallint":
                return Integer(trimmed, short.MinValue, short.MaxValue, "smallint", v => (short)v, out value, out error);
            case "tinyint":
                return Integer(trimmed, 0, 255, "tinyint", v => (byte)v, out value, out error);
            case "bit":
                switch (trimmed.ToLowerInvariant())
                {
                    case "1" or "true" or "yes" or "y":
                        value = true;
                        return true;
                    case "0" or "false" or "no" or "n":
                        value = false;
                        return true;
                }
                error = "Enter 1 or 0 (or true or false).";
                return false;
            case "decimal" or "numeric" or "money" or "smallmoney":
                if (decimal.TryParse(trimmed, NumberStyles.Number, inv, out var dec))
                {
                    value = dec;
                    return true;
                }
                error = "Enter a number such as 12.50.";
                return false;
            case "float":
                if (double.TryParse(trimmed, NumberStyles.Float, inv, out var dbl) && double.IsFinite(dbl))
                {
                    value = dbl;
                    return true;
                }
                error = "Enter a number such as 3.14.";
                return false;
            case "real":
                if (float.TryParse(trimmed, NumberStyles.Float, inv, out var flt) && float.IsFinite(flt))
                {
                    value = flt;
                    return true;
                }
                error = "Enter a number such as 3.14.";
                return false;
            case "date":
                if (DateTime.TryParse(trimmed, inv, DateTimeStyles.None, out var d))
                {
                    value = d.Date;
                    return true;
                }
                error = "Enter a date such as 2026-11-04.";
                return false;
            case "datetime" or "smalldatetime" or "datetime2":
                if (DateTime.TryParse(trimmed, inv, DateTimeStyles.None, out var dt))
                {
                    value = dt;
                    return true;
                }
                error = "Enter a date and time such as 2026-11-04 13:45:00.";
                return false;
            case "datetimeoffset":
                if (DateTimeOffset.TryParse(trimmed, inv, DateTimeStyles.None, out var dto))
                {
                    value = dto;
                    return true;
                }
                error = "Enter a date, time and offset such as 2026-11-04 13:45:00 +05:30.";
                return false;
            case "time":
                if (TimeSpan.TryParse(trimmed, inv, out var ts) && ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1))
                {
                    value = ts;
                    return true;
                }
                error = "Enter a time such as 13:45:00.";
                return false;
            case "uniqueidentifier":
                if (Guid.TryParse(trimmed, out var guid))
                {
                    value = guid;
                    return true;
                }
                error = "Enter a GUID such as 3F2504E0-4F89-11D3-9A0C-0305E82C3301.";
                return false;
            default:
                error = $"{typeName} columns can't be edited here.";
                return false;
        }
    }

    static bool Integer(string text, long min, long max, string type, Func<long, object> convert, out object? value, out string error)
    {
        value = null;
        error = "";
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max)
        {
            value = convert(n);
            return true;
        }
        error = $"Enter a whole number from {min:N0} to {max:N0} for {type}.";
        return false;
    }
}
