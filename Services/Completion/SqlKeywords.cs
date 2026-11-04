namespace SqlStudio.Services.Completion;

public static class SqlKeywords
{
    public static readonly string[] Keywords =
    [
        "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUTHORIZATION", "BACKUP", "BEGIN", "BETWEEN", "BREAK", "BY",
        "CASE", "CATCH", "CHECK", "CLOSE", "CLUSTERED", "COLUMN", "COMMIT", "CONSTRAINT", "CONTINUE", "CREATE", "CROSS",
        "CURSOR", "DATABASE", "DEALLOCATE", "DECLARE", "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP", "ELSE", "END",
        "EXCEPT", "EXEC", "EXECUTE", "EXISTS", "FETCH", "FOREIGN", "FROM", "FULL", "FUNCTION", "GO", "GOTO", "GRANT",
        "GROUP", "HAVING", "IDENTITY", "IF", "IN", "INDEX", "INNER", "INSERT", "INTERSECT", "INTO", "IS", "JOIN", "KEY",
        "LEFT", "LIKE", "MERGE", "NOCOUNT", "NOLOCK", "NOT", "NULL", "OFFSET", "ON", "OPEN", "OPTION", "OR", "ORDER",
        "OUTER", "OUTPUT", "OVER", "PARTITION", "PRIMARY", "PRINT", "PROCEDURE", "RETURN", "RETURNS", "REVOKE", "RIGHT",
        "ROLLBACK", "ROWS", "SCHEMA", "SELECT", "SET", "TABLE", "THEN", "TOP", "TRAN", "TRANSACTION", "TRIGGER",
        "TRUNCATE", "TRY", "UNION", "UNIQUE", "UPDATE", "USE", "VALUES", "VIEW", "WHEN", "WHERE", "WHILE", "WITH",
    ];

    public static readonly string[] Functions =
    [
        "ABS", "AVG", "CAST", "CEILING", "CHARINDEX", "COALESCE", "CONCAT", "CONVERT", "COUNT", "COUNT_BIG", "DATEADD",
        "DATEDIFF", "DATENAME", "DATEPART", "DAY", "DB_NAME", "FLOOR", "FORMAT", "GETDATE", "GETUTCDATE", "IIF", "ISNULL",
        "ISNUMERIC", "LEFT", "LEN", "LOWER", "LTRIM", "MAX", "MIN", "MONTH", "NEWID", "NULLIF", "OBJECT_ID",
        "OBJECT_NAME", "RIGHT", "ROUND", "ROW_NUMBER", "RANK", "DENSE_RANK", "RTRIM", "SCOPE_IDENTITY", "SERVERPROPERTY",
        "STRING_AGG", "STRING_SPLIT", "SUBSTRING", "SUM", "SYSDATETIME", "TRIM", "TRY_CAST", "TRY_CONVERT", "UPPER", "YEAR",
    ];

    /// <summary>Words that must be wrapped in [brackets] when used as a name.</summary>
    public static readonly HashSet<string> Reserved = new(Keywords.Concat(
        ["USER", "ORDER", "GROUP", "TABLE", "KEY", "INDEX", "VIEW", "COLUMN", "CHECK", "DEFAULT", "PRIMARY", "REFERENCES",
         "PUBLIC", "PLAN", "FILE", "PERCENT", "READ", "SCHEMA", "SESSION_USER", "SYSTEM_USER", "TEXTSIZE", "TO", "OF", "OR"]),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>A word that cannot be a table alias, because it starts the next part of the statement.</summary>
    public static readonly HashSet<string> AliasStops = new(
        ["WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER",
         "JOIN", "ON", "SET", "VALUES", "SELECT", "WITH", "FOR", "OPTION", "USING", "WHEN", "OUTPUT", "TABLESAMPLE", "PIVOT",
         "UNPIVOT", "APPLY", "DEFAULT", "EXEC", "EXECUTE", "OFFSET", "NATURAL", "GO"],
        StringComparer.OrdinalIgnoreCase);

    public static bool NeedsBrackets(string name) =>
        name.Length == 0
        || !(char.IsLetter(name[0]) || name[0] == '_')
        || !name.All(c => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$')
        || Reserved.Contains(name);

    public static string Quote(string name) => NeedsBrackets(name) ? "[" + name.Replace("]", "]]") + "]" : name;
}
