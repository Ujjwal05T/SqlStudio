namespace SqlStudio.Services.Completion;

public enum CompletionKind { None, Keywords, Objects, Procedures, Databases, Columns, Members }

/// <summary>A table named in the statement, with the alias it was given (FROM Orders o).</summary>
public sealed record TableRef(string? Database, string? Schema, string Name, string? Alias, bool IsCte = false)
{
    public bool Matches(string qualifier) =>
        string.Equals(Alias, qualifier, StringComparison.OrdinalIgnoreCase)
        || (Alias is null && string.Equals(Name, qualifier, StringComparison.OrdinalIgnoreCase));
}

/// <param name="ReplaceStart">Where the word being typed starts (including its opening [ when one was typed).</param>
/// <param name="Qualifier">The dotted parts before the cursor: ["dbo"] for "dbo.", ["o"] for "o.".</param>
public sealed record CompletionContext(
    CompletionKind Kind,
    string Prefix,
    int ReplaceStart,
    bool InBracket,
    int ExtendAfter,
    IReadOnlyList<string> Qualifier,
    IReadOnlyList<TableRef> Tables)
{
    public static readonly CompletionContext None = new(CompletionKind.None, "", 0, false, 0, [], []);
}

/// <summary>
/// Works out what the user is probably about to type, from the text around the cursor. This is a pragmatic reader, not a
/// full T-SQL parser: it understands the statement the cursor is in (split on ; and GO), its table aliases, and the
/// keyword just before the cursor.
/// </summary>
public static class SqlContextAnalyzer
{
    static readonly HashSet<string> ObjectKeywords = new(["FROM", "JOIN", "INTO", "UPDATE", "TABLE", "MERGE", "REFERENCES"], StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> ColumnKeywords = new(
        ["SELECT", "WHERE", "AND", "OR", "ON", "BY", "HAVING", "SET", "WHEN", "THEN", "ELSE", "CASE", "NOT", "IN", "LIKE",
         "BETWEEN", "DISTINCT", "ALL", "IS", "EXISTS", "ANY", "SOME", "OUTPUT", "RETURN"], StringComparer.OrdinalIgnoreCase);

    public static CompletionContext Analyze(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var all = SqlLexer.Tokenize(text);
        if (all.Any(t => IsInsideLiteral(t, caret)))
            return CompletionContext.None;

        var p = caret;
        while (p > 0 && SqlLexer.IsWordChar(text[p - 1]))
            p--;
        var inBracket = p > 0 && text[p - 1] == '[';
        var cutoff = inBracket ? p - 1 : p;
        var prefix = text[p..caret];
        var extendAfter = inBracket && caret < text.Length && text[caret] == ']' ? 1 : 0;

        var tokens = all.Where(t => t.Kind is not (TokenKind.LineComment or TokenKind.BlockComment)).ToList();
        var (stmtStart, stmtEnd) = StatementBounds(text, tokens, cutoff, caret);
        var stmt = tokens.Where(t => t.Start >= stmtStart && t.Start < stmtEnd).ToList();
        var before = stmt.Where(t => t.End <= cutoff).ToList();
        var tables = ExtractTables(stmt);

        var qualifier = ReadQualifier(before, cutoff);
        var kind = qualifier.Count > 0 ? CompletionKind.Members : Classify(before);
        return new CompletionContext(kind, prefix, cutoff, inBracket, extendAfter, qualifier, tables);
    }

    static bool IsInsideLiteral(Token t, int caret) => t.Kind switch
    {
        TokenKind.String => caret > t.Start && (caret < t.End || (!t.Terminated && caret <= t.End)),
        TokenKind.BlockComment => caret > t.Start && (caret < t.End || (!t.Terminated && caret <= t.End)),
        TokenKind.LineComment => caret > t.Start && caret <= t.End,
        _ => false,
    };

    // ---- statements ----

    static bool IsBoundary(string text, Token t) => t.IsPunct(";") || (t.Is("GO") && IsAloneOnItsLine(text, t));

    static bool IsAloneOnItsLine(string text, Token t)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, t.Start - 1)) + 1;
        if (t.Start > 0 && text.AsSpan(lineStart, t.Start - lineStart).Trim().Length > 0)
            return false;
        var lineEnd = text.IndexOf('\n', t.End);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd;
        var rest = text.AsSpan(t.End, lineEnd - t.End).Trim();
        return rest.Length == 0 || rest.StartsWith("--") || int.TryParse(rest, out _); // GO 5
    }

    static (int Start, int End) StatementBounds(string text, List<Token> tokens, int cutoff, int caret)
    {
        var start = 0;
        var end = int.MaxValue;
        foreach (var t in tokens)
        {
            if (!IsBoundary(text, t))
                continue;
            if (t.End <= cutoff)
                start = t.End;
            else if (t.Start >= caret)
            {
                end = t.Start;
                break;
            }
        }
        return (start, end);
    }

    // ---- what comes before the cursor ----

    static List<string> ReadQualifier(List<Token> before, int cutoff)
    {
        var qualifier = new List<string>();
        var i = before.Count - 1;
        if (i < 0 || !before[i].IsPunct(".") || before[i].End != cutoff)
            return qualifier;
        while (i >= 1 && before[i].IsPunct(".") && before[i - 1].IsIdentifier && qualifier.Count < 3)
        {
            qualifier.Insert(0, before[i - 1].Name);
            i -= 2;
        }
        return qualifier;
    }

    static CompletionKind Classify(List<Token> before)
    {
        if (before.Count == 0)
            return CompletionKind.Keywords;
        var prev = before[^1];

        if (prev.Kind == TokenKind.Word)
        {
            var kw = prev.Text.ToUpperInvariant();
            if (kw == "AS")
                return CompletionKind.None; // naming something new
            if (ObjectKeywords.Contains(kw))
                return kw == "TABLE" && before.Count >= 2 && before[^2].Is("CREATE") ? CompletionKind.None : CompletionKind.Objects;
            if (kw is "EXEC" or "EXECUTE")
                return CompletionKind.Procedures;
            if (kw == "USE")
                return CompletionKind.Databases;
            return ColumnKeywords.Contains(kw) ? CompletionKind.Columns : CompletionKind.Keywords;
        }

        if (prev.Kind == TokenKind.Number)
            return before.Count >= 2 && before[^2].Is("TOP") ? CompletionKind.Columns : CompletionKind.Keywords;

        if (prev.Kind == TokenKind.Punct)
        {
            switch (prev.Text)
            {
                case ",":
                    return ClauseOf(before);
                case "(":
                    return AfterOpenParen(before);
                case "*":
                    return before.Count >= 2 && (before[^2].Is("SELECT") || before[^2].IsPunct(",") || before[^2].IsPunct("."))
                        ? CompletionKind.Keywords
                        : CompletionKind.Columns;
                case "=" or "<" or ">" or "+" or "-" or "/" or "%" or "&" or "|" or "^" or "!" or "~":
                    return CompletionKind.Columns;
            }
        }
        return CompletionKind.Keywords;
    }

    /// <summary>After a comma: which part of the statement are we in?</summary>
    static CompletionKind ClauseOf(List<Token> before)
    {
        var depth = 0;
        for (var i = before.Count - 2; i >= 0; i--)
        {
            var t = before[i];
            if (t.IsPunct(")"))
                depth++;
            else if (t.IsPunct("(") && depth > 0)
                depth--;
            else if (depth == 0 && t.Kind == TokenKind.Word)
            {
                var kw = t.Text.ToUpperInvariant();
                if (kw == "FROM")
                    return CompletionKind.Objects;
                if (kw is "SELECT" or "BY" or "SET" or "WHERE" or "HAVING" or "ON" or "VALUES" or "WHEN" or "AND" or "OR" or "INTO" or "UPDATE")
                    return CompletionKind.Columns;
            }
        }
        return CompletionKind.Keywords;
    }

    static CompletionKind AfterOpenParen(List<Token> before)
    {
        var n = before.Count;
        if (n >= 4 && before[n - 3].Is("TABLE") && before[n - 4].Is("CREATE"))
            return CompletionKind.None; // defining new columns
        if (n >= 3 && before[n - 2].IsIdentifier && before[n - 3].Is("INTO"))
            return CompletionKind.Columns; // INSERT INTO t (
        if (n >= 2 && before[n - 2].Kind == TokenKind.Word && before[n - 2].Text.ToUpperInvariant() is "FROM" or "JOIN" or "APPLY" or "UNION" or "AS")
            return CompletionKind.Keywords; // a derived table or CTE body starts with SELECT
        return CompletionKind.Columns;
    }

    // ---- tables named in the statement ----

    public static List<TableRef> ExtractTables(List<Token> stmt)
    {
        var result = new List<TableRef>();
        ReadCtes(stmt, result);

        for (var i = 0; i < stmt.Count; i++)
        {
            var t = stmt[i];
            if (t.Kind != TokenKind.Word)
                continue;
            var kw = t.Text.ToUpperInvariant();
            if (kw is "FROM" or "JOIN" or "UPDATE" or "INTO" or "MERGE" or "USING")
                i = ReadTableList(stmt, i + 1, allowCommas: kw == "FROM", isInto: kw == "INTO", result) - 1;
        }

        // FROM recent  where "recent" was defined by WITH is the CTE, even if a real table has the same name
        var cteNames = result.Where(r => r.IsCte).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return result
            .Select(r => !r.IsCte && r.Database is null && r.Schema is null && cteNames.Contains(r.Name) ? r with { IsCte = true } : r)
            .Distinct()
            .ToList();
    }

    static void ReadCtes(List<Token> stmt, List<TableRef> result)
    {
        if (stmt.Count == 0 || !stmt[0].Is("WITH"))
            return;
        var i = 1;
        while (i < stmt.Count && stmt[i].IsIdentifier)
        {
            var name = stmt[i].Name;
            i++;
            if (i < stmt.Count && stmt[i].IsPunct("("))
                i = SkipParens(stmt, i);
            if (i < stmt.Count && stmt[i].Is("AS"))
                i++;
            if (i < stmt.Count && stmt[i].IsPunct("("))
                i = SkipParens(stmt, i);
            result.Add(new TableRef(null, null, name, null, IsCte: true));
            if (i < stmt.Count && stmt[i].IsPunct(","))
                i++;
            else
                break;
        }
    }

    static int ReadTableList(List<Token> stmt, int i, bool allowCommas, bool isInto, List<TableRef> result)
    {
        var n = stmt.Count;
        while (true)
        {
            if (i >= n)
                return i;

            if (stmt[i].IsPunct("("))
            {
                i = SkipParens(stmt, i); // derived table: its alias has no known columns
                i = ReadAlias(stmt, i, out _);
            }
            else if (stmt[i].IsIdentifier && !(stmt[i].Kind == TokenKind.Word && SqlKeywords.AliasStops.Contains(stmt[i].Text)))
            {
                var parts = new List<string> { stmt[i].Name };
                i++;
                while (i + 1 < n && stmt[i].IsPunct(".") && stmt[i + 1].IsIdentifier)
                {
                    parts.Add(stmt[i + 1].Name);
                    i += 2;
                }

                if (i < n && stmt[i].IsPunct("."))
                    return i + 1; // still typing the name ("dbo."): the part so far is a qualifier, not a table

                if (i < n && stmt[i].IsPunct("(") && !isInto)
                {
                    i = SkipParens(stmt, i); // table-valued function
                    i = ReadAlias(stmt, i, out _);
                }
                else
                {
                    i = ReadAlias(stmt, i, out var alias);
                    var last3 = parts.TakeLast(3).ToList();
                    result.Add(last3.Count switch
                    {
                        1 => new TableRef(null, null, last3[0], alias),
                        2 => new TableRef(null, last3[0], last3[1], alias),
                        _ => new TableRef(last3[0], last3[1], last3[2], alias),
                    });
                }
            }
            else
            {
                return i;
            }

            if (allowCommas && i < n && stmt[i].IsPunct(","))
            {
                i++;
                continue;
            }
            return i;
        }
    }

    static int ReadAlias(List<Token> stmt, int i, out string? alias)
    {
        alias = null;
        if (i < stmt.Count && stmt[i].Is("AS"))
            i++;
        if (i < stmt.Count && stmt[i].IsIdentifier && !(stmt[i].Kind == TokenKind.Word && SqlKeywords.AliasStops.Contains(stmt[i].Text)))
        {
            alias = stmt[i].Name;
            i++;
        }
        return i;
    }

    static int SkipParens(List<Token> stmt, int i)
    {
        var depth = 0;
        for (; i < stmt.Count; i++)
        {
            if (stmt[i].IsPunct("(")) depth++;
            else if (stmt[i].IsPunct(")") && --depth == 0) return i + 1;
        }
        return stmt.Count;
    }
}
