using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SqlStudio.ViewModels;

namespace SqlStudio.Views;

public partial class ClaudeConnectWindow : Window
{
    public ClaudeConnectWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindows())
            Opened += (_, _) => FitOnScreen();
    }

    void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Windows only: the popup is tall, so centred on its owner its title bar (and the system close button) can end up above the top of the
    /// screen. Keep the whole window, title bar included, inside the usable screen area.
    /// </summary>
    void FitOnScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen)
            return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        // Room for the system title bar and a little margin, in device-independent units.
        var maxHeight = area.Height / scale - 60;
        if (Height > maxHeight)
            Height = Math.Max(MinHeight, maxHeight);
        var frameTop = 40 * scale; // roughly the title bar height in pixels
        if (Position.Y < area.Y + frameTop)
            Position = new Avalonia.PixelPoint(Position.X, (int)(area.Y + frameTop));
    }

    async void OnCopyCommand(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ClaudeConnectViewModel { IsEnabled: true } vm && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.AddCommand);
    }
}
