using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

    /// <summary>Shows a box to type a note in. Returns the text (empty to remove the note), or null when the user cancels.</summary>
    public static async Task<string?> EditNoteAsync(Window owner, string title, string hint, string current)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            MinWidth = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var box = new TextBox { Text = current, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, MaxHeight = 300, AcceptsTab = false };
        var count = new TextBlock { Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        void UpdateCount() => count.Text = $"{(box.Text ?? "").Length:N0} characters · saved notes are read by Claude, so shorter is cheaper";
        box.TextChanged += (_, _) => UpdateCount();
        UpdateCount();
        var save = new Button { Content = "Save", Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel" };
        save.Click += (_, _) => dialog.Close(box.Text ?? "");
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                dialog.Close(null);
            else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
                dialog.Close(box.Text ?? "");
        };
        dialog.Opened += (_, _) => box.Focus();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap },
                box,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        count,
                        new StackPanel
                        {
                            [Grid.ColumnProperty] = 1,
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children = { new TextBlock { Text = "⌘↵ save · Esc cancel", Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }, cancel, save },
                        },
                    },
                },
            },
        };
        return await dialog.ShowDialog<string?>(owner);
    }
}
