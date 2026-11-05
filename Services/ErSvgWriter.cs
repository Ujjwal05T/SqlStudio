using System.Globalization;
using System.Net;
using System.Text;

namespace SqlStudio.Services;

/// <summary>The colours of a drawn diagram. Used by the screen, the SVG and the PNG so all three look the same.</summary>
public static class ErStyle
{
    public const string Paper = "#FFFFFF";
    public const string NodeFill = "#FFFFFF";
    public const string NodeBorder = "#94A3B8";
    public const string HeaderFill = "#E2E8F0";
    public const string Title = "#0F172A";
    public const string Text = "#1E293B";
    public const string Muted = "#64748B";
    public const string PrimaryKey = "#B45309";
    public const string ForeignKey = "#2563EB";
    public const string Edge = "#64748B";
    public const string SourceOnly = "#16A34A";
    public const string TargetOnly = "#DC2626";
    public const string Different = "#D97706";
    public const string SourceOnlyTint = "#DCFCE7";
    public const string TargetOnlyTint = "#FEE2E2";
    public const string DifferentTint = "#FEF3C7";

    /// <summary>Green: only in the source (the reference). Red: only in the target. Amber: in both but different.</summary>
    public static string? ChangeColour(DiffStatus? change) => change switch
    {
        DiffStatus.OnlyInSource => SourceOnly,
        DiffStatus.OnlyInTarget => TargetOnly,
        DiffStatus.Different => Different,
        _ => null,
    };

    public static string? ChangeTint(DiffStatus? change) => change switch
    {
        DiffStatus.OnlyInSource => SourceOnlyTint,
        DiffStatus.OnlyInTarget => TargetOnlyTint,
        DiffStatus.Different => DifferentTint,
        _ => null,
    };
    public const string ViewHeader = "#E0E7FF";
    public const string ProcedureHeader = "#FCE7F3";
    public const string FunctionHeader = "#CCFBF1";
    public const string FocusHeader = "#FDE68A";
    public static readonly string[] HeaderColours =
        [HeaderFill, ViewHeader, ProcedureHeader, FunctionHeader, FocusHeader, SourceOnlyTint, TargetOnlyTint, DifferentTint];

    /// <summary>The header colour of a box: the focus object stands out, otherwise the colour says what kind of object it is.</summary>
    public static string HeaderFor(ErTable t) => ChangeTint(t.Change) ?? (t.IsFocus ? FocusHeader : t.Kind switch
    {
        "view" => ViewHeader,
        "procedure" => ProcedureHeader,
        "function" => FunctionHeader,
        _ => HeaderFill,
    });
    public const string Font = "Menlo, Consolas, 'DejaVu Sans Mono', monospace";
}

/// <summary>Writes a laid-out diagram as a standalone SVG document. Plain text, so it stays sharp at any zoom.</summary>
public static class ErSvgWriter
{
    public static string Write(ErLayoutResult layout)
    {
        var sb = new StringBuilder();
        var w = Num(Math.Max(layout.Width, 1));
        var h = Num(Math.Max(layout.Height, 1));
        sb.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}" font-family="{ErStyle.Font}" font-size="{Num(ErLayout.FontSize)}">""");
        sb.AppendLine($"""  <rect width="100%" height="100%" fill="{ErStyle.Paper}"/>""");

        // Lines first, so boxes sit on top of them.
        foreach (var e in layout.Edges)
        {
            sb.AppendLine($"""  <g class="relationship"><title>{Escape(e.Relation.Name)}</title>""");
            var colour = ErStyle.ChangeColour(e.Relation.Change) ?? ErStyle.Edge;
            sb.AppendLine($"""    <path d="M{Pt(e.Start)} C{Pt(e.C1)} {Pt(e.C2)} {Pt(e.End)}" fill="none" stroke="{colour}" stroke-width="{(e.Relation.Change is null ? "1.4" : "2")}"/>""");
            sb.AppendLine($"""    <polygon points="{string.Join(' ', e.Arrow.Select(Pt))}" fill="{colour}"/>""");
            sb.AppendLine("  </g>");
        }

        foreach (var n in layout.Nodes)
            WriteNode(sb, n);

        foreach (var item in layout.Legend)
        {
            sb.AppendLine($"""  <g class="legend"><rect x="{Num(item.X)}" y="{Num(item.Y)}" width="14" height="14" rx="3" fill="{ErStyle.ChangeTint(item.Change)}" stroke="{ErStyle.ChangeColour(item.Change)}" stroke-width="2"/>""");
            sb.AppendLine($"""    <text x="{Num(item.X + 22)}" y="{Num(item.Y + 11)}" fill="{ErStyle.Text}">{Escape(item.Label)}</text></g>""");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    static void WriteNode(StringBuilder sb, ErNode n)
    {
        sb.AppendLine($"""  <g class="table" data-table="{Escape(n.Table.Id)}">""");
        sb.AppendLine($"""    <rect x="{Num(n.X)}" y="{Num(n.Y)}" width="{Num(n.Width)}" height="{Num(n.Height)}" rx="6" fill="{ErStyle.NodeFill}" stroke="{ErStyle.ChangeColour(n.Table.Change) ?? ErStyle.NodeBorder}"{(n.Table.Change is null ? "" : " stroke-width=\"2\"")}/>""");
        sb.AppendLine($"""    <path d="M{Num(n.X)} {Num(n.Y + ErLayout.HeaderHeight)} V{Num(n.Y + 6)} Q{Num(n.X)} {Num(n.Y)} {Num(n.X + 6)} {Num(n.Y)} H{Num(n.Right - 6)} Q{Num(n.Right)} {Num(n.Y)} {Num(n.Right)} {Num(n.Y + 6)} V{Num(n.Y + ErLayout.HeaderHeight)} Z" fill="{ErStyle.HeaderFor(n.Table)}" stroke="{ErStyle.NodeBorder}"/>""");
        sb.AppendLine($"""    <text x="{Num(n.X + ErLayout.Padding)}" y="{Num(n.Y + 18.5)}" font-weight="bold" fill="{ErStyle.Title}">{Escape(n.Table.Title)}</text>""");

        if (n.Table.Columns.Count == 0 && n.Table.Subtitle is { } subtitle)
            sb.AppendLine($"""    <text x="{Num(n.X + ErLayout.Padding)}" y="{Num(n.Y + ErLayout.HeaderHeight + 14)}" fill="{ErStyle.Muted}">{Escape(subtitle)}</text>""");

        for (var i = 0; i < n.Table.Columns.Count; i++)
        {
            var c = n.Table.Columns[i];
            var baseline = n.Y + ErLayout.HeaderHeight + ErLayout.RowHeight * i + 14;
            if (ErStyle.ChangeTint(c.Change) is { } tint)
                sb.AppendLine($"""    <rect x="{Num(n.X + 1)}" y="{Num(n.Y + ErLayout.HeaderHeight + ErLayout.RowHeight * i)}" width="{Num(n.Width - 2)}" height="{Num(ErLayout.RowHeight)}" fill="{tint}"/>""");
            var badge = (c.IsPrimaryKey, c.IsForeignKey) switch { (true, true) => "PK FK", (true, _) => "PK", (_, true) => "FK", _ => "" };
            if (badge.Length > 0)
                sb.AppendLine($"""    <text x="{Num(n.X + ErLayout.Padding)}" y="{Num(baseline)}" font-size="10" font-weight="bold" fill="{(c.IsPrimaryKey ? ErStyle.PrimaryKey : ErStyle.ForeignKey)}">{badge}</text>""");
            sb.AppendLine($"""    <text x="{Num(n.X + ErLayout.Padding + ErLayout.BadgeWidth)}" y="{Num(baseline)}"{(c.IsPrimaryKey ? " font-weight=\"bold\"" : "")} fill="{ErStyle.ChangeColour(c.Change) ?? ErStyle.Text}">{Escape(c.Name)}</text>""");
            sb.AppendLine($"""    <text x="{Num(n.X + n.TypeOffset)}" y="{Num(baseline)}" fill="{ErStyle.Muted}">{Escape(c.DisplayType)}</text>""");
        }
        sb.AppendLine("  </g>");
    }

    static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    static string Pt(ErPoint p) => $"{Num(p.X)} {Num(p.Y)}";

    /// <summary>Escapes markup characters and drops control characters, which XML cannot carry.</summary>
    public static string Escape(string text)
    {
        var clean = new string(text.Where(ch => ch >= ' ' || ch is '\t').ToArray());
        return WebUtility.HtmlEncode(clean);
    }
}
