using System.Text;

namespace SqlStudio.Services.Mcp;

/// <summary>
/// A safety net for queries that come from an AI: only a single SELECT (optionally with CTEs) gets through.
/// It reads words outside comments, strings and quoted names, so a value like 'DROP TABLE' is fine and a statement is not.
/// This is not a guarantee: the database login's own permissions are the real limit.
/// </summary>
public static class ReadOnlySql
{
    static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "DROP", "ALTER", "CREATE", "TRUNCATE", "EXEC", "EXECUTE",
        "GRANT", "REVOKE", "DENY", "BACKUP", "RESTORE", "SHUTDOWN", "KILL", "DBCC", "RECONFIGURE",
        "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "INTO", "WAITFOR",
    };

    /// <summary>Null when the query is allowed, otherwise the reason it is not.</summary>
    public static string? Check(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "The query is empty.";

        var words = new List<string>();
        var statementEnded = false;
        foreach (var token in Tokenize(sql))
        {
            if (token == ";")
            {
                statementEnded = true;
                continue;
            }
            if (statementEnded)
                return "Only one statement is allowed. Send the queries one at a time.";
            words.Add(token);
        }

        if (words.Count == 0)
            return "The query is empty.";
        if (!words[0].Equals("SELECT", StringComparison.OrdinalIgnoreCase) && !words[0].Equals("WITH", StringComparison.OrdinalIgnoreCase))
            return $"Only SELECT queries are allowed (this one starts with {words[0].ToUpperInvariant()}).";
        if (words[0].Equals("WITH", StringComparison.OrdinalIgnoreCase) && !words.Any(w => w.Equals("SELECT", StringComparison.OrdinalIgnoreCase)))
            return "Only SELECT queries are allowed.";

        for (var i = 0; i < words.Count; i++)
        {
            if (Forbidden.Contains(words[i]))
                return $"{words[i].ToUpperInvariant()} is not allowed: this connection is read-only.";
            // NEXT VALUE FOR a sequence reads it and uses up a number.
            if (words[i].Equals("NEXT", StringComparison.OrdinalIgnoreCase) && i + 1 < words.Count && words[i + 1].Equals("VALUE", StringComparison.OrdinalIgnoreCase))
                return "NEXT VALUE FOR changes a sequence, so it is not allowed here.";
        }
        return null;
    }

    /// <summary>The words (as text) and semicolons of the script, skipping comments, string literals and quoted names.</summary>
    static IEnumerable<string> Tokenize(string sql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                    i++;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var depth = 1; // block comments nest in T-SQL
                i += 2;
                while (i < sql.Length && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
            }
            else if (c is '\'' or '"' or '[')
            {
                var close = c == '[' ? ']' : c;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == close)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; } // doubled closing character
                        break;
                    }
                    i++;
                }
                i++;
            }
            else if (c == ';')
            {
                i++;
                yield return ";";
            }
            else if (char.IsLetter(c) || c is '_' or '@' or '#')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '@' or '#' or '$'))
                    i++;
                yield return sql[start..i];
            }
            else
            {
                i++;
            }
        }
    }
}
