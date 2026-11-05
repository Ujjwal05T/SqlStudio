using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Text;
using SqlStudio.Services;
using SqlStudio.ViewModels;

namespace SqlStudio.Views;

public partial class PlanView : UserControl
{
    /// <summary>Beyond this many pixels on a side a PNG is too big to hold in memory.</summary>
    const double MaxPngSide = 16000;

    public PlanView() => InitializeComponent();

    PlanViewModel? Vm => DataContext as PlanViewModel;

    void OnZoomIn(object? sender, RoutedEventArgs e) => Tree.ZoomBy(1.25);
    void OnZoomOut(object? sender, RoutedEventArgs e) => Tree.ZoomBy(0.8);
    void OnFit(object? sender, RoutedEventArgs e) => Tree.ZoomToFit();

    async void OnCopyScript(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PlanFinding { Script: { } script } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(script);
    }

    async void OnSavePlan(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        await SaveAsync("sqlplan", "SQL Server execution plan", () => new UTF8Encoding(false).GetBytes(vm.FileXml));
    }

    async void OnExportPng(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Layout: { } layout })
            return;
        if (Math.Max(layout.Width, layout.Height) > MaxPngSide)
        {
            await Report("Too big for a PNG", "This plan is too large to save as one picture. Save it as a .sqlplan file instead.");
            return;
        }
        var scale = Math.Max(layout.Width, layout.Height) * 2 <= MaxPngSide ? 2 : 1;
        await SaveAsync("png", "PNG image", () => ErPainter.RenderPng(layout, scale));
    }

    async Task SaveAsync(string extension, string label, Func<byte[]> build)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save execution plan",
            SuggestedFileName = $"query plan.{extension}",
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(label) { Patterns = [$"*.{extension}"] }],
        });
        if (file is null)
            return;
        try
        {
            var bytes = build();
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0); // overwriting a longer file must not leave its old tail behind
            await stream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            await Report("Could not save", ex.Message);
        }
    }

    async Task Report(string title, string message)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            await Dialogs.ConfirmAsync(owner, title, message, "OK", showCancel: false);
    }
}
