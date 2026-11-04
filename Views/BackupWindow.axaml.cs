using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SqlStudio.ViewModels;

namespace SqlStudio.Views;

public partial class BackupWindow : Window
{
    public BackupWindow()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            if (DataContext is BackupViewModel { IsRunning: true } vm)
                vm.CancelCommand.Execute(null); // do not leave a backup running behind a closed dialog
        };
    }

    // The command runs first (it opens the script tab); then the dialog gets out of the way.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Ask from this dialog, so the prompt sits on top of it instead of behind it.
        if (DataContext is BackupViewModel vm)
            vm.Confirm = (title, message, confirm, cancel) => Dialogs.ConfirmAsync(this, title, message, confirm, cancelLabel: cancel);
    }

    async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BackupViewModel vm)
            return;
        if (await ServerFileBrowserWindow.PickAsync(this, vm.CreateBrowser()) is { } path)
            vm.Path = path;
    }

    void OnScript(object? sender, RoutedEventArgs e) => Close();

    async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BackupViewModel vm)
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Save the zip in" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            vm.LocalFolder = path;
    }

    void OnReveal(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BackupViewModel { HasDownload: true } vm)
            System.Diagnostics.Process.Start("/usr/bin/open", ["-R", vm.DownloadedZip]);
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();
}
