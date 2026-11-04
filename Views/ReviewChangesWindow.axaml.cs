using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SqlStudio.ViewModels;

namespace SqlStudio.Views;

public partial class ReviewChangesWindow : Window
{
    public ReviewChangesWindow() => InitializeComponent();

    async void OnCopyScript(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChangeReviewViewModel vm && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.Script);
    }

    void OnSave(object? sender, RoutedEventArgs e) => Close(true);

    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
