using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SqlStudio.Services;

namespace SqlStudio.Views;

/// <summary>
/// Shows an ER diagram on a grey desk. Drag to pan, scroll to pan, hold Ctrl or Cmd while scrolling to zoom.
/// The sheet itself is always white, so what is on screen is what the SVG and PNG exports contain.
/// </summary>
public sealed class ErDiagramView : Control
{
    public static readonly StyledProperty<ErLayoutResult?> LayoutProperty =
        AvaloniaProperty.Register<ErDiagramView, ErLayoutResult?>(nameof(Layout));

    const double MinZoom = 0.05, MaxZoom = 3, Margin = 24;
    static readonly IBrush Desk = new SolidColorBrush(Color.Parse("#64748B"), 0.18);

    readonly ErPainter _painter = new();
    double _zoom = 1;
    Vector _offset;
    Point? _dragFrom;
    bool _fitPending = true;

    static ErDiagramView()
    {
        AffectsRender<ErDiagramView>(LayoutProperty);
        LayoutProperty.Changed.AddClassHandler<ErDiagramView>((view, _) => view.ZoomToFit());
        ClipToBoundsProperty.OverrideDefaultValue<ErDiagramView>(true);
    }

    public ErLayoutResult? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public double Zoom => _zoom;

    public void ZoomToFit()
    {
        FitToBounds();
        InvalidateVisual();
    }

    // Sets the zoom and offset only. Render calls this too, and a visual must not ask for a repaint while it is being painted.
    void FitToBounds()
    {
        if (Layout is not { Width: > 0, Height: > 0 } layout || Bounds.Width < 1 || Bounds.Height < 1)
        {
            _fitPending = true; // sized later, when the control has a size
            return;
        }
        _fitPending = false;
        _zoom = Math.Clamp(Math.Min((Bounds.Width - Margin * 2) / layout.Width, (Bounds.Height - Margin * 2) / layout.Height), MinZoom, 1);
        _offset = new Vector((Bounds.Width - layout.Width * _zoom) / 2, Math.Max(Margin, (Bounds.Height - layout.Height * _zoom) / 2));
    }

    public void ZoomBy(double factor, Point? anchor = null)
    {
        var at = anchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var next = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        // Keep the diagram point under the anchor where it is.
        var diagram = (at - new Point(_offset.X, _offset.Y)) / _zoom;
        _offset = new Vector(at.X - diagram.X * next, at.Y - diagram.Y * next);
        _zoom = next;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Desk, null, new Rect(Bounds.Size));
        if (Layout is not { } layout)
            return;
        if (_fitPending)
            FitToBounds();

        using (context.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_offset.X, _offset.Y)))
        {
            var visible = new Rect(-_offset.X / _zoom, -_offset.Y / _zoom, Bounds.Width / _zoom, Bounds.Height / _zoom);
            _painter.Paint(context, layout, visible);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_fitPending)
            ZoomToFit();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from)
            return;
        var now = e.GetPosition(this);
        _offset += now - from;
        _dragFrom = now;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            ZoomBy(Math.Pow(1.0015, e.Delta.Y * 100), e.GetPosition(this));
        else
        {
            _offset += new Vector(e.Delta.X, e.Delta.Y) * 48;
            InvalidateVisual();
        }
        e.Handled = true;
    }
}
