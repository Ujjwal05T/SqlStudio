using System.Text;

namespace SqlStudio.Services;

/// <summary>Helpers for putting names and values into generated T-SQL safely.</summary>
public static class SqlText
{
    /// <summary>[name] with any closing bracket doubled, like QUOTENAME.</summary>
    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>N'text' with quotes doubled.</summary>
    public static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";

    /// <summary>Renders a column type the way SSMS shows it, e.g. nvarchar(50), decimal(18,2), varchar(max).</summary>
    public static string FormatType(string typeName, int maxLength, int precision, int scale)
    {
        var sb = new StringBuilder(typeName);
        switch (typeName.ToLowerInvariant())
        {
            case "char" or "varchar" or "binary" or "varbinary":
                sb.Append('(').Append(maxLength == -1 ? "max" : maxLength.ToString()).Append(')');
                break;
            case "nchar" or "nvarchar":
                sb.Append('(').Append(maxLength == -1 ? "max" : (maxLength / 2).ToString()).Append(')');
                break;
            case "decimal" or "numeric":
                sb.Append('(').Append(precision).Append(',').Append(scale).Append(')');
                break;
            case "datetime2" or "time" or "datetimeoffset":
                sb.Append('(').Append(scale).Append(')');
                break;
        }
        return sb.ToString();
    }

    /// <summary>Picks the path separator a server uses, judging by one of its own paths.</summary>
    public static char SeparatorFor(string? serverPath) => serverPath?.Contains('/') == true ? '/' : '\\';

    public static string JoinPath(string folder, string file)
    {
        var sep = SeparatorFor(folder);
        return folder.TrimEnd('\\', '/') + sep + file;
    }
}
