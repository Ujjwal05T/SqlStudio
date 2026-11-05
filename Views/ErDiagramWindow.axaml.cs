using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SqlStudio.Services;
using SqlStudio.ViewModels;

namespace SqlStudio.Views;

public partial class ErDiagramWindow : Window
{
    /// <summary>Beyond this many pixels on a side a PNG is too big to hold in memory; the SVG has no such limit.</summary>
    const double MaxPngSide = 16000;

    public ErDiagramWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is ErDiagramViewModel { Layout: null } vm)
                _ = vm.LoadAsync();
        };
        Closing += (_, _) => (DataContext as ErDiagramViewModel)?.Cancel();
    }

    void OnZoomIn(object? sender, RoutedEventArgs e) => Diagram.ZoomBy(1.25);
    void OnZoomOut(object? sender, RoutedEventArgs e) => Diagram.ZoomBy(0.8);
    void OnFit(object? sender, RoutedEventArgs e) => Diagram.ZoomToFit();

    async void OnExportSvg(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ErDiagramViewModel { Layout: { } layout } vm)
            return;
        var file = await PickFileAsync(vm, "svg", "SVG image");
        if (file is null)
            return;
        await SaveAsync(vm, file, () => new UTF8Encoding(false).GetBytes(ErSvgWriter.Write(layout)));
    }

    async void OnExportPng(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ErDiagramViewModel { Layout: { } layout } vm)
            return;
        if (Math.Max(layout.Width, layout.Height) > MaxPngSide)
        {
            await Dialogs.ConfirmAsync(this, "Too big for a PNG",
                "This diagram is too large to save as one picture. Export it as SVG instead; it keeps every detail at any zoom.",
                "OK", showCancel: false);
            return;
        }
        var file = await PickFileAsync(vm, "png", "PNG image");
        if (file is null)
            return;
        // Twice the pixels for sharpness, unless that would be too large.
        var scale = Math.Max(layout.Width, layout.Height) * 2 <= MaxPngSide ? 2 : 1;
        await SaveAsync(vm, file, () => ErPainter.RenderPng(layout, scale));
    }

    Task<IStorageFile?> PickFileAsync(ErDiagramViewModel vm, string extension, string label) =>
        StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export diagram",
            SuggestedFileName = $"{vm.Database} {vm.Source.FileStem}.{extension}",
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(label) { Patterns = [$"*.{extension}"] }],
        });

    async Task SaveAsync(ErDiagramViewModel vm, IStorageFile file, Func<byte[]> build)
    {
        try
        {
            var bytes = build();
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0); // overwriting a longer file must not leave its old tail behind
            await stream.WriteAsync(bytes);
            vm.StatusText = $"Saved {file.Name}";
        }
        catch (Exception ex)
        {
            await Dialogs.ConfirmAsync(this, "Could not export", ex.Message, "OK", showCancel: false);
        }
    }
}
