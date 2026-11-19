using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SqlStudio.Services;
using SqlStudio.ViewModels;

namespace SqlStudio.Views;

/// <summary>
/// One cell of the result grid. It repaints itself when its row changes, so a clicked cell is outlined and edited,
/// new and deleted rows are tinted without rebuilding the grid.
/// </summary>
public sealed class GridCellView : Border
{
    static readonly Color Edited = Color.Parse("#F5A524");
    static readonly Color Added = Color.Parse("#27AE60");
    static readonly Color Deleted = Color.Parse("#E5484D");

    readonly GridRow _row;
    readonly int _column;
    readonly ResultSetViewModel _vm;
    readonly string _type;
    readonly TextBlock _text;

    public GridCellView(GridRow row, int column, ResultSetViewModel vm)
    {
        _row = row;
        _column = column;
        _vm = vm;
        _type = vm.Data.Columns[column].TypeName;
        _text = new TextBlock
        {
            Margin = new Thickness(9, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = CellFormatter.IsNumeric(_type) ? TextAlignment.Right : TextAlignment.Left,
        };
        Child = _text;
        BorderThickness = new Thickness(1.5); // always present, so outlining the clicked cell never shifts the text
        BorderBrush = Brushes.Transparent;
        Refresh();
        ActualThemeVariantChanged += (_, _) => Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _row.PropertyChanged += OnRowChanged;
        _vm.PropertyChanged += OnVmChanged;
        Refresh(); // the row may have changed while this cell was off screen
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _row.PropertyChanged -= OnRowChanged;
        _vm.PropertyChanged -= OnVmChanged;
    }

    void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    void Refresh()
    {
        var value = _row[_column];
        var unassigned = !_row.IsAssigned(_column);
        var dim = value is null || unassigned;

        _text.Text = unassigned ? (_vm.IsIdentity(_column) ? "(auto)" : "(default)") : CellFormatter.Format(value, _type);
        _text.FontStyle = dim ? FontStyle.Italic : FontStyle.Normal;
        _text.TextDecorations = _row.State == RowState.Deleted ? TextDecorations.Strikethrough : null;
        if (dim && this.TryFindResource("MutedText", ActualThemeVariant, out var muted) && muted is IBrush mutedBrush)
            _text.Foreground = mutedBrush;
        else
            _text.ClearValue(TextBlock.ForegroundProperty);

        Background = _row.State switch
        {
            RowState.Deleted => Tint(Deleted, 0.16),
            RowState.Added => Tint(Added, 0.14),
            RowState.Modified when _row.IsChanged(_column) => Tint(Edited, 0.24),
            _ => _vm.InBlock(_row, _column) ? BlockTint() : ColumnTint(),
        };
        BorderBrush = Brushes.Transparent; // the selection is shown by the tint alone
    }

    /// <summary>A soft accent wash down the whole column when it has been selected.</summary>
    IBrush ColumnTint() =>
        _vm.ColumnSelection == _column && this.TryFindResource("AccentBrush", ActualThemeVariant, out var accent) && accent is ISolidColorBrush solid
            ? Tint(solid.Color, 0.16)
            : Brushes.Transparent;

    /// <summary>The cells a copy will take, when more than one is selected.</summary>
    IBrush BlockTint() =>
        this.TryFindResource("AccentBrush", ActualThemeVariant, out var accent) && accent is ISolidColorBrush solid ? Tint(solid.Color, 0.26) : Brushes.Transparent;

    public GridRow Row => _row;
    public int Column => _column;

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResultSetViewModel.ColumnSelection) or nameof(ResultSetViewModel.RangeVersion))
            Refresh();
    }

    static IBrush Tint(Color color, double opacity) => new SolidColorBrush(color, opacity);
}
