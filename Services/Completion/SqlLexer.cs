namespace SqlStudio.Services.Completion;

public enum TokenKind { Word, Bracketed, DoubleQuoted, String, Number, Punct, LineComment, BlockComment }

public sealed record Token(TokenKind Kind, int Start, int End, string Text, bool Terminated = true)
{
    public bool IsIdentifier => Kind is TokenKind.Word or TokenKind.Bracketed or TokenKind.DoubleQuoted;

    public bool IsPunct(string p) => Kind == TokenKind.Punct && Text == p;

    public bool Is(string keyword) => Kind == TokenKind.Word && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>The identifier without its quotes: [Order Details] becomes Order Details.</summary>
    public string Name => Kind switch
    {
        TokenKind.Bracketed => Unquote(Text, ']').Replace("]]", "]"),
        TokenKind.DoubleQuoted => Unquote(Text, '"').Replace("\"\"", "\""),
        _ => Text,
    };

    string Unquote(string text, char close) => text.Length >= 2 && Terminated && text[^1] == close ? text[1..^1] : text[1..];
}

/// <summary>
/// A forgiving T-SQL tokenizer. It never throws and copes with half-typed input (an unclosed string, bracket or comment
/// simply runs to the end), because it is called while the user is still typing.
/// </summary>
public static class SqlLexer
{
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && Next(text, i) == '-')
            {
                var end = text.IndexOf('\n', i);
                end = end < 0 ? text.Length : end;
                if (end > i && text[end - 1] == '\r') end--;
                tokens.Add(new Token(TokenKind.LineComment, i, end, text[i..end]));
                i = end;
            }
            else if (c == '/' && Next(text, i) == '*')
            {
                var end = BlockCommentEnd(text, i, out var closed);
                tokens.Add(new Token(TokenKind.BlockComment, i, end, text[i..end], closed));
                i = end;
            }
            else if (c == '\'')
            {
                var end = QuotedEnd(text, i, '\'', out var closed);
                tokens.Add(new Token(TokenKind.String, i, end, text[i..end], closed));
                i = end;
            }
            else if (c == '[')
            {
                var end = QuotedEnd(text, i, ']', out var closed);
                tokens.Add(new Token(TokenKind.Bracketed, i, end, text[i..end], closed));
                i = end;
            }
            else if (c == '"')
            {
                var end = QuotedEnd(text, i, '"', out var closed);
                tokens.Add(new Token(TokenKind.DoubleQuoted, i, end, text[i..end], closed));
                i = end;
            }
            else if (char.IsDigit(c))
            {
                var end = i + 1;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '.' && end + 1 < text.Length && char.IsDigit(text[end + 1])))
                    end++;
                tokens.Add(new Token(TokenKind.Number, i, end, text[i..end]));
                i = end;
            }
            else if (IsWordChar(c))
            {
                var end = i + 1;
                while (end < text.Length && IsWordChar(text[end]))
                    end++;
                tokens.Add(new Token(TokenKind.Word, i, end, text[i..end]));
                i = end;
            }
            else
            {
                tokens.Add(new Token(TokenKind.Punct, i, i + 1, c.ToString()));
                i++;
            }
        }
        return tokens;
    }

    static char Next(string text, int i) => i + 1 < text.Length ? text[i + 1] : '\0';

    /// <summary>End of a quoted token; a doubled closing character is an escape.</summary>
    static int QuotedEnd(string text, int start, char close, out bool closed)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] != close)
                continue;
            if (i + 1 < text.Length && text[i + 1] == close)
            {
                i++;
                continue;
            }
            closed = true;
            return i + 1;
        }
        closed = false;
        return text.Length;
    }

    static int BlockCommentEnd(string text, int start, out bool closed)
    {
        var depth = 0;
        for (var i = start; i < text.Length - 1; i++)
        {
            if (text[i] == '/' && text[i + 1] == '*') { depth++; i++; }
            else if (text[i] == '*' && text[i + 1] == '/' && --depth == 0) { closed = true; return i + 2; }
        }
        closed = false;
        return text.Length;
    }
}
