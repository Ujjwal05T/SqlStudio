using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SqlStudio.Views;

public static class Dialogs
{
    /// <summary>Shows a small modal. Returns true when the user picks the confirm button.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirmLabel = "OK", bool showCancel = true, string cancelLabel = "Cancel")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var confirm = new Button { Content = confirmLabel, Classes = { "primary" } };
        var cancel = new Button { Content = cancelLabel, IsVisible = showCancel };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm },
                },
            },
        };
        return await dialog.ShowDialog<bool>(owner);
    }
}
