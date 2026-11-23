using System.Globalization;
using System.Text;
using SqlStudio.Models;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Themes;

namespace SqlStudio.Services;

/// <summary>The colours of the query editor for one palette.</summary>
public sealed record EditorColors(
    string Background,
    string Foreground,
    string Selection,
    string LineNumber,
    string Comment,
    string Keyword,
    string String,
    string Number,
    string Function,
    string Type,
    string Variable);

/// <summary>
/// Builds the editor's colour scheme from the app palette, so the selection, text, operators (the * in SELECT *) and syntax
/// colours belong to the chosen theme instead of a fixed Dark+/Light+ pair. Operators and punctuation use the plain text
/// colour, so they can never vanish. Every syntax colour is nudged until it reads on both the editor and the selection.
/// </summary>
public static class EditorTheme
{
    public const double MinText = 4.5;

    static readonly Dictionary<string, EditorColors> Cache = [];

    public static EditorColors For(ThemePalette p)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(p.Id, out var colors))
                Cache[p.Id] = colors = Build(p);
            return colors;
        }
    }

    static EditorColors Build(ThemePalette p)
    {
        var surface = p.Surface;
        // The selection is the accent washed into the editor. Take the strongest wash that still lets every colour keep its contrast.
        EditorColors? best = null;
        for (var k = 0.34; k >= 0.10; k -= 0.02)
        {
            var selection = Mix(surface, p.Accent, k);
            var colors = Palette(p, selection);
            best = colors;
            if (Ok(colors, surface, selection))
                break;
        }
        return best!;
    }

    static EditorColors Palette(ThemePalette p, string selection)
    {
        string Fit(string hex) => Readable(hex, [p.Surface, selection], p.IsDark, MinText);
        var hue = Hsl.From(p.Accent);
        return new EditorColors(
            Background: p.Surface,
            Foreground: p.Text,
            Selection: selection,
            LineNumber: Readable(p.Muted, [p.Surface], p.IsDark, MinText),
            Comment: Fit(p.Muted),
            Keyword: Fit(p.Accent),
            String: Fit(p.Success),
            Number: Fit(p.Warn),
            Function: Fit(hue.Rotate(40).ToHex()),
            Type: Fit(hue.Rotate(-45).ToHex()),
            Variable: Fit(hue.Rotate(150).ToHex()));
    }

    static bool Ok(EditorColors c, string surface, string selection) =>
        new[] { c.Foreground, c.Comment, c.Keyword, c.String, c.Number, c.Function, c.Type, c.Variable }
            .All(fg => Contrast.Ratio(fg, surface) >= MinText && Contrast.Ratio(fg, selection) >= MinText)
        && Contrast.Ratio(selection, surface) >= 1.25;

    /// <summary>Moves a colour toward white (dark editors) or black (light editors) until it reads on every background.</summary>
    static string Readable(string hex, string[] backgrounds, bool dark, double min)
    {
        var c = Hsl.From(hex);
        for (var i = 0; i < 60 && backgrounds.Any(bg => Contrast.Ratio(c.ToHex(), bg) < min); i++)
            c = c with { L = Math.Clamp(c.L + (dark ? 0.01 : -0.01), 0, 1) };
        return c.ToHex();
    }

    /// <summary>The scheme as a VS Code style theme, which is what TextMate reads.</summary>
    public static string Json(ThemePalette p)
    {
        var c = For(p);
        var sb = new StringBuilder();
        sb.Append("{\"name\":\"").Append(p.Id).Append("\",\"type\":\"").Append(p.IsDark ? "dark" : "light").Append("\",");
        sb.Append("\"colors\":{");
        sb.Append($"\"editor.background\":\"{c.Background}\",\"editor.foreground\":\"{c.Foreground}\",");
        sb.Append($"\"editor.selectionBackground\":\"{c.Selection}\",\"editorLineNumber.foreground\":\"{c.LineNumber}\"");
        sb.Append("},\"tokenColors\":[");
        sb.Append($"{{\"settings\":{{\"foreground\":\"{c.Foreground}\",\"background\":\"{c.Background}\"}}}}");
        Token(sb, c.Foreground, null, "keyword.operator", "punctuation", "meta.brace", "keyword.operator.star", "keyword.operator.wildcard", "keyword.operator.comparison", "keyword.operator.assignment", "keyword.operator.arithmetic");
        Token(sb, c.Comment, "italic", "comment", "punctuation.definition.comment");
        Token(sb, c.Keyword, null, "keyword", "keyword.other", "storage.modifier", "constant.language", "keyword.control");
        Token(sb, c.String, null, "string", "string.quoted", "punctuation.definition.string");
        Token(sb, c.Number, null, "constant.numeric", "constant.character", "constant.other");
        Token(sb, c.Function, null, "support.function", "entity.name.function", "meta.function-call");
        Token(sb, c.Type, null, "storage.type", "support.type", "entity.name.type", "support.class", "entity.name.class");
        Token(sb, c.Variable, null, "variable", "variable.other", "variable.parameter", "entity.name.tag");
        sb.Append("]}");
        return sb.ToString();
    }

    static void Token(StringBuilder sb, string color, string? style, params string[] scopes)
    {
        sb.Append(",{\"scope\":[").Append(string.Join(",", scopes.Select(s => $"\"{s}\"")));
        sb.Append($"],\"settings\":{{\"foreground\":\"{color}\"");
        if (style is not null)
            sb.Append($",\"fontStyle\":\"{style}\"");
        sb.Append("}}");
    }

    public static IRawTheme Load(ThemePalette p)
    {
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(Json(p))));
        return ThemeReader.ReadThemeSync(reader);
    }

    static string Mix(string a, string b, double t)
    {
        var (ar, ag, ab) = Rgb(a);
        var (br, bg, bb) = Rgb(b);
        int Lerp(int x, int y) => (int)Math.Round(x + (y - x) * t);
        return $"#{Lerp(ar, br):X2}{Lerp(ag, bg):X2}{Lerp(ab, bb):X2}";
    }

    static (int R, int G, int B) Rgb(string hex)
    {
        var h = hex.TrimStart('#');
        return (int.Parse(h[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(h[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(h[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    readonly record struct Hsl(double H, double S, double L)
    {
        public static Hsl From(string hex)
        {
            var (r8, g8, b8) = Rgb(hex);
            double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var l = (max + min) / 2;
            if (max == min)
                return new Hsl(0, 0, l);
            var d = max - min;
            var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            var h = max == r ? (g - b) / d + (g < b ? 6 : 0)
                  : max == g ? (b - r) / d + 2
                  : (r - g) / d + 4;
            return new Hsl(h * 60, s, l);
        }

        public Hsl Rotate(double degrees) => this with { H = ((H + degrees) % 360 + 360) % 360 };

        public string ToHex()
        {
            double h = H, s = S, l = L;
            double C(double n)
            {
                var k = (n + h / 30) % 12;
                var a = s * Math.Min(l, 1 - l);
                return l - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1));
            }
            return $"#{(int)Math.Round(C(0) * 255):X2}{(int)Math.Round(C(8) * 255):X2}{(int)Math.Round(C(4) * 255):X2}";
        }
    }
}
