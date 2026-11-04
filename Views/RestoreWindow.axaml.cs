using Avalonia.Controls;
using Avalonia.Interactivity;
using SqlStudio.ViewModels;

namespace SqlStudio.Views;

public partial class RestoreWindow : Window
{
    public RestoreWindow()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            if (DataContext is RestoreViewModel { IsRunning: true } vm)
                vm.CancelCommand.Execute(null);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Ask from this dialog, so the prompt sits on top of it instead of behind it.
        if (DataContext is RestoreViewModel vm)
            vm.Confirm = (title, message, confirm, cancel) => Dialogs.ConfirmAsync(this, title, message, confirm, cancelLabel: cancel);
    }

    async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RestoreViewModel vm)
            return;
        if (await ServerFileBrowserWindow.PickAsync(this, vm.CreateBrowser()) is { } path)
        {
            vm.SourcePath = path;
            vm.ReadCommand.Execute(null); // the next thing anyone wants after picking a backup is to see what is in it
        }
    }

    async void OnBrowseFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RestoreViewModel vm && (sender as Control)?.DataContext is RestoreFileRow row &&
            await ServerFileBrowserWindow.PickAsync(this, vm.CreateFileBrowser(row)) is { } path)
            row.TargetPath = path;
    }

    void OnScript(object? sender, RoutedEventArgs e) => Close();

    void OnClose(object? sender, RoutedEventArgs e) => Close();
}
