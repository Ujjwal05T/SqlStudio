using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace SqlStudio.Views;

/// <summary>Shows one cell's full value, which the grid cuts off. JSON and XML are indented so they can be read.</summary>
public partial class CellViewerWindow : Window
{
    readonly string _raw;
    readonly string? _formatted;

    public CellViewerWindow()
    {
        InitializeComponent();
        _raw = "";
    }

    public CellViewerWindow(string column, string type, string text) : this()
    {
        _raw = text;
        Heading.Text = column;
        _formatted = TryFormat(text, out var kind);
        Detail.Text = $"{type} · {text.Length:N0} character{(text.Length == 1 ? "" : "s")}" + (kind is null ? "" : $" · shown as formatted {kind}");
        RawBox.IsVisible = _formatted is not null;
        Box.Text = _formatted ?? text;
    }

    /// <summary>Indented JSON or XML, or null when the text is neither.</summary>
    public static string? TryFormat(string text, out string? kind)
    {
        kind = null;
        var trimmed = text.Trim();
        try
        {
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                using var doc = JsonDocument.Parse(trimmed);
                kind = "JSON";
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            if (trimmed.StartsWith('<'))
            {
                var xml = XDocument.Parse(trimmed).ToString();
                kind = "XML";
                return xml;
            }
        }
        catch (Exception ex) when (ex is JsonException or System.Xml.XmlException)
        {
            // not valid JSON or XML after all: show the text as it is
        }
        return null;
    }

    void OnWrapChanged(object? sender, RoutedEventArgs e) =>
        Box.TextWrapping = WrapBox.IsChecked == true ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap;

    void OnRawChanged(object? sender, RoutedEventArgs e) => Box.Text = RawBox.IsChecked == true ? _raw : _formatted ?? _raw;

    async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(Box.Text ?? "");
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();
}
