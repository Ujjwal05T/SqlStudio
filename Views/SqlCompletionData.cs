using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using SqlStudio.Services;
using SqlStudio.Services.Completion;

namespace SqlStudio.Views;

/// <summary>One row in the suggestion popup: an icon for its kind, the name, and a muted detail (type, "table", ...).</summary>
public sealed class SqlCompletionData : ICompletionData
{
    readonly CompletionItem _item;
    readonly int _extendBefore;
    readonly int _extendAfter;
    readonly Action<string> _inserted;

    public SqlCompletionData(CompletionItem item, double priority, int extendBefore, int extendAfter, Action<string> inserted)
    {
        _item = item;
        Priority = priority;
        _extendBefore = extendBefore;
        _extendAfter = extendAfter;
        _inserted = inserted;
        Content = BuildContent(item);
    }

    public Avalonia.Media.IImage? Image => null;

    /// <summary>What the popup filters on while the user keeps typing.</summary>
    public string Text => _item.Label;

    public object Content { get; }

    // The kind/type already shows inline; returning a description would add a second, redundant tooltip box.
    public object? Description => null;

    public double Priority { get; }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        var doc = textArea.Document;
        // Replacing from the opening [ to its closing ] means "[Ord|]" becomes "Orders" or "[Order Details]" cleanly.
        var start = Math.Max(0, completionSegment.Offset - _extendBefore);
        var end = Math.Min(doc.TextLength, completionSegment.EndOffset + _extendAfter);
        doc.Replace(start, end - start, _item.InsertText);
        textArea.Caret.Offset = start + _item.InsertText.Length;
        _inserted(_item.InsertText);
    }

    static Control BuildContent(CompletionItem item)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new Icon
        {
            Kind = IconFor(item.Kind),
            VerticalAlignment = VerticalAlignment.Center,
            Stroke = new SolidColorBrush(Color.Parse(ThemeService.Current.Accent)),
        });
        panel.Children.Add(new TextBlock
        {
            Text = item.Label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse(ThemeService.Current.Text)),
        });
        if (!string.IsNullOrEmpty(item.Detail))
            panel.Children.Add(new TextBlock
            {
                Text = item.Detail,
                FontSize = 11,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse(ThemeService.Current.Muted)),
            });
        return panel;
    }

    static string IconFor(ItemKind kind) => kind switch
    {
        ItemKind.Table => "table",
        ItemKind.View => "view",
        ItemKind.Procedure => "procedure",
        ItemKind.Function => "function",
        ItemKind.Schema => "layers",
        ItemKind.Column => "column",
        ItemKind.Database => "database",
        _ => "list",
    };
}
