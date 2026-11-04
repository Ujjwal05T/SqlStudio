using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace SqlStudio.Views;

/// <summary>
/// One of the app's line icons (see <see cref="Icons"/>). It follows the surrounding text colour, like a font glyph,
/// unless Stroke is set, so it stays readable on buttons, in menus and in every theme.
/// </summary>
public sealed class Icon : Control
{
    const double Grid = 24;
    const double StrokeWidth = 1.75; // in the 24-unit grid, so the weight scales with the icon

    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Kind));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Icon, IBrush?>(nameof(Stroke));

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, StrokeProperty, TextElement.ForegroundProperty);
    }

    public Icon()
    {
        Width = 16;
        Height = 16;
        IsHitTestVisible = false;
    }

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Leave unset to use the text colour.</summary>
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Icons.Get(Kind) is not { } geometry)
            return;
        var brush = Stroke ?? TextElement.GetForeground(this);
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (brush is null || size <= 0)
            return;

        var scale = size / Grid;
        var offset = new Vector((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
            context.DrawGeometry(null, new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }
}
