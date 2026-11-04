using System.Text;
using System.Text.RegularExpressions;

namespace SqlStudio.Services;

public sealed record SqlBatch(string Text, int Repeat, int StartLine);

/// <summary>
/// Splits a script on GO lines the way SSMS does. GO is a client-side separator, not T-SQL, so it is
/// only recognised alone on a line and never inside a string, bracketed name or block comment.
/// </summary>
public static partial class BatchSplitter
{
    [GeneratedRegex(@"^\s*GO(?:\s+(?<n>\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    public static List<SqlBatch> Split(string script)
    {
        var batches = new List<SqlBatch>();
        var current = new StringBuilder();
        var startLine = 1;
        var lineNo = 0;
        var state = new LexState();

        foreach (var line in script.ReplaceLineEndings("\n").Split('\n'))
        {
            lineNo++;
            if (state.IsPlain && GoLine().Match(line) is { Success: true } m)
            {
                Flush(m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value) : 1);
                startLine = lineNo + 1;
                continue;
            }

            if (current.Length == 0)
                startLine = lineNo;
            current.Append(line).Append('\n');
            state.Advance(line);
        }

        Flush(1);
        return batches;

        void Flush(int repeat)
        {
            var text = current.ToString();
            current.Clear();
            if (!string.IsNullOrWhiteSpace(text) && repeat > 0)
                batches.Add(new SqlBatch(text.TrimEnd('\n'), repeat, startLine));
        }
    }

    /// <summary>Tracks whether a line starts inside a comment or quoted token carried over from earlier lines.</summary>
    sealed class LexState
    {
        int _blockDepth;
        char _quote; // '\'' or '[' or '"' while inside one, otherwise '\0'

        public bool IsPlain => _blockDepth == 0 && _quote == '\0';

        public void Advance(string line)
        {
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                var next = i + 1 < line.Length ? line[i + 1] : '\0';

                if (_blockDepth > 0)
                {
                    if (c == '*' && next == '/') { _blockDepth--; i++; }
                    else if (c == '/' && next == '*') { _blockDepth++; i++; }
                }
                else if (_quote != '\0')
                {
                    var close = _quote == '[' ? ']' : _quote;
                    if (c == close)
                    {
                        if (next == close) i++; // doubled delimiter is an escape
                        else _quote = '\0';
                    }
                }
                else if (c == '-' && next == '-') return; // rest of line is a comment
                else if (c == '/' && next == '*') { _blockDepth++; i++; }
                else if (c is '\'' or '[' or '"') _quote = c;
            }
        }
    }
}
