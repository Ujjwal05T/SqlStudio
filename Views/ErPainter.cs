using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SqlStudio.Services;

namespace SqlStudio.Views;

/// <summary>
/// Draws a laid-out ER diagram in diagram coordinates. The screen view pushes its pan and zoom first; the PNG export
/// draws it as is. Only boxes inside the visible area are drawn, so panning a big schema stays smooth.
/// </summary>
public sealed class ErPainter
{
    static readonly FontFamily Mono = new("Menlo, Consolas, DejaVu Sans Mono, monospace");
    static readonly Typeface Regular = new(Mono);
    static readonly Typeface Bold = new(Mono, FontStyle.Normal, FontWeight.Bold);

    static readonly IBrush Paper = Brush(ErStyle.Paper);
    static readonly IBrush NodeFill = Brush(ErStyle.NodeFill);
    static readonly Dictionary<string, IBrush> Headers = ErStyle.HeaderColours.ToDictionary(c => c, Brush);
    static readonly IBrush TitleBrush = Brush(ErStyle.Title);
    static readonly IBrush TextBrush = Brush(ErStyle.Text);
    static readonly IBrush MutedBrush = Brush(ErStyle.Muted);
    static readonly IBrush PrimaryKeyBrush = Brush(ErStyle.PrimaryKey);
    static readonly IBrush ForeignKeyBrush = Brush(ErStyle.ForeignKey);
    static readonly IBrush EdgeBrush = Brush(ErStyle.Edge);
    static readonly Pen BorderPen = new(Brush(ErStyle.NodeBorder), 1);
    static readonly Pen EdgePen = new(EdgeBrush, 1.4);
    static readonly Dictionary<DiffStatus, IBrush> ChangeBrushes = Enum.GetValues<DiffStatus>().ToDictionary(s => s, s => Brush(ErStyle.ChangeColour(s)!));
    static readonly Dictionary<DiffStatus, IBrush> ChangeTints = Enum.GetValues<DiffStatus>().ToDictionary(s => s, s => Brush(ErStyle.ChangeTint(s)!));
    static readonly Dictionary<DiffStatus, Pen> ChangePens = Enum.GetValues<DiffStatus>().ToDictionary(s => s, s => new Pen(ChangeBrushes[s], 2));

    readonly Dictionary<(string Text, bool Bold, double Size, IBrush Brush), FormattedText> _text = [];

    static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public void Paint(DrawingContext ctx, ErLayoutResult layout, Rect visible)
    {
        ctx.DrawRectangle(Paper, null, new Rect(0, 0, layout.Width, layout.Height));

        foreach (var e in layout.Edges)
            if (EdgeBounds(e).Intersects(visible))
                DrawEdge(ctx, e);

        foreach (var n in layout.Nodes)
            if (new Rect(n.X, n.Y, n.Width, n.Height).Intersects(visible))
                DrawNode(ctx, n);

        foreach (var item in layout.Legend)
        {
            ctx.DrawRectangle(ChangeTints[item.Change], ChangePens[item.Change], new Rect(item.X, item.Y, 14, 14), 3, 3);
            Text(ctx, item.Label, false, ErLayout.FontSize, TextBrush, item.X + 22, item.Y + 11);
        }
    }

    static Rect EdgeBounds(ErEdge e)
    {
        var xs = new[] { e.Start.X, e.C1.X, e.C2.X, e.End.X };
        var ys = new[] { e.Start.Y, e.C1.Y, e.C2.Y, e.End.Y };
        return new Rect(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min()).Inflate(6);
    }

    static void DrawEdge(DrawingContext ctx, ErEdge e)
    {
        var change = e.Relation.Change;
        var pen = change is { } c ? new Pen(ChangeBrushes[c], 2) : EdgePen;
        var arrowBrush = change is { } c2 ? ChangeBrushes[c2] : EdgeBrush;
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(e.Start), false);
            g.CubicBezierTo(P(e.C1), P(e.C2), P(e.End));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, line);

        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(P(e.Arrow[0]), true);
            g.LineTo(P(e.Arrow[1]));
            g.LineTo(P(e.Arrow[2]));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(arrowBrush, null, arrow);
    }

    void DrawNode(DrawingContext ctx, ErNode n)
    {
        var box = new Rect(n.X, n.Y, n.Width, n.Height);
        ctx.DrawRectangle(NodeFill, null, box, 6, 6);
        using (ctx.PushClip(new RoundedRect(box, 6)))
            ctx.DrawRectangle(Headers[ErStyle.HeaderFor(n.Table)], null, new Rect(n.X, n.Y, n.Width, ErLayout.HeaderHeight));
        ctx.DrawRectangle(null, n.Table.Change is { } change ? ChangePens[change] : BorderPen, box, 6, 6);
        ctx.DrawLine(BorderPen, new Point(n.X, n.Y + ErLayout.HeaderHeight), new Point(n.Right, n.Y + ErLayout.HeaderHeight));

        Text(ctx, n.Table.Title, true, ErLayout.FontSize, TitleBrush, n.X + ErLayout.Padding, n.Y + 18.5);

        if (n.Table.Columns.Count == 0 && n.Table.Subtitle is { } subtitle)
            Text(ctx, subtitle, false, ErLayout.FontSize, MutedBrush, n.X + ErLayout.Padding, n.Y + ErLayout.HeaderHeight + 14);

        for (var i = 0; i < n.Table.Columns.Count; i++)
        {
            var c = n.Table.Columns[i];
            var baseline = n.Y + ErLayout.HeaderHeight + ErLayout.RowHeight * i + 14;
            if (c.Change is { } columnChange)
                ctx.DrawRectangle(ChangeTints[columnChange], null, new Rect(n.X + 1, n.Y + ErLayout.HeaderHeight + ErLayout.RowHeight * i, n.Width - 2, ErLayout.RowHeight));
            var badge = (c.IsPrimaryKey, c.IsForeignKey) switch { (true, true) => "PK FK", (true, _) => "PK", (_, true) => "FK", _ => "" };
            if (badge.Length > 0)
                Text(ctx, badge, true, 10, c.IsPrimaryKey ? PrimaryKeyBrush : ForeignKeyBrush, n.X + ErLayout.Padding, baseline);
            Text(ctx, c.Name, c.IsPrimaryKey, ErLayout.FontSize, c.Change is { } nameChange ? ChangeBrushes[nameChange] : TextBrush, n.X + ErLayout.Padding + ErLayout.BadgeWidth, baseline);
            Text(ctx, c.DisplayType, false, ErLayout.FontSize, MutedBrush, n.X + n.TypeOffset, baseline);
        }
    }

    // Text is positioned by its baseline in the SVG; here by its top edge, about 11.5 above the baseline at 12px.
    void Text(DrawingContext ctx, string text, bool bold, double size, IBrush brush, double x, double baseline)
    {
        if (!_text.TryGetValue((text, bold, size, brush), out var formatted))
            _text[(text, bold, size, brush)] = formatted = new FormattedText(
                text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? Bold : Regular, size, brush);
        ctx.DrawText(formatted, new Point(x, baseline - size * 0.96));
    }

    static Point P(ErPoint p) => new(p.X, p.Y);

    /// <summary>The diagram as PNG bytes at the given scale (2 gives retina sharpness). Must run on the UI thread.</summary>
    public static byte[] RenderPng(ErLayoutResult layout, double scale)
    {
        var size = new Size(Math.Max(layout.Width, 1), Math.Max(layout.Height, 1));
        var paper = new Sheet(layout) { Width = size.Width, Height = size.Height };
        paper.Measure(size);
        paper.Arrange(new Rect(size));

        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(paper);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    sealed class Sheet(ErLayoutResult layout) : Control
    {
        readonly ErPainter _painter = new();

        public override void Render(DrawingContext context) =>
            _painter.Paint(context, layout, new Rect(0, 0, layout.Width, layout.Height));
    }
}
