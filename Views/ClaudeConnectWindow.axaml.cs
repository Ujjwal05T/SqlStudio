using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SqlStudio.ViewModels;

namespace SqlStudio.Views;

public partial class ClaudeConnectWindow : Window
{
    public ClaudeConnectWindow() => InitializeComponent();

    async void OnCopyCommand(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ClaudeConnectViewModel { IsEnabled: true } vm && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.AddCommand);
    }
}
