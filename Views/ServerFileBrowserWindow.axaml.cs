using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SqlStudio.Services;
using SqlStudio.ViewModels;

namespace SqlStudio.Views;

public partial class ServerFileBrowserWindow : Window
{
    public ServerFileBrowserWindow()
    {
        InitializeComponent();
        PathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is ServerFileBrowserViewModel vm)
            {
                e.Handled = true;
                vm.GoToTypedCommand.Execute(null);
            }
        };
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    /// <summary>Opens the browser on top of the owner and returns the chosen path, or null if the user cancelled.</summary>
    public static async Task<string?> PickAsync(Window owner, ServerFileBrowserViewModel vm)
    {
        var window = new ServerFileBrowserWindow { DataContext = vm, Title = vm.Heading };
        vm.Chosen += () => window.Close();
        window.Opened += async (_, _) => await vm.InitializeAsync();
        await window.ShowDialog(owner);
        return vm.Result;
    }

    void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ServerFileBrowserViewModel { SelectedEntry: { } entry } vm)
            vm.ActivateCommand.Execute(entry);
    }

    void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ServerFileBrowserViewModel { SelectedEntry: { } entry } vm)
        {
            e.Handled = true;
            vm.ActivateCommand.Execute(entry);
        }
    }

    void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
