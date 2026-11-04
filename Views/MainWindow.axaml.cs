using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SqlStudio.Services;
using SqlStudio.Models;
using SqlStudio.ViewModels;

namespace SqlStudio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DragStrip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
    }

    MainViewModel Vm => (MainViewModel)DataContext!;

    static ExplorerNode? NodeOf(object? sender) => (sender as Control)?.DataContext as ExplorerNode;

    async void OnAddConnection(object? sender, RoutedEventArgs e) => await EditAsync(null);

    void OnNodeDoubleTapped(object? sender, TappedEventArgs e)
    {
        switch (Vm.SelectedNode)
        {
            case { IsServer: true } server:
                server.IsExpanded = !server.IsExpanded;
                break;
            case { Kind: NodeKind.Database } db when db.Info is { IsOnline: true }:
                Vm.OpenQuery(db.Profile, db.Database);
                break;
            case { IsTableOrView: true } table:
                Vm.SelectTop(table);
                break;
        }
    }

    void OnNodeNewQuery(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
            Vm.OpenQuery(node.Profile, node.Database);
    }

    void OnNodeSelectTop(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
            Vm.SelectTop(node);
    }

    async void OnNodeRefresh(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
            await node.RefreshAsync();
    }

    async void OnNodeEdit(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
            await EditAsync(node.Profile);
    }

    async void OnNodeDelete(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node)
            return;
        var profile = node.Profile;
        if (await Dialogs.ConfirmAsync(this, $"Delete “{profile.DisplayName}”?",
                "The server and its saved password will be removed from this Mac. Open query tabs keep running. This can't be undone.",
                "Delete server", cancelLabel: "Keep server"))
            Vm.DeleteProfile(profile);
    }

    async void OnNodeBackup(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { Database: { } database } node)
            return;
        var vm = new BackupViewModel(node.Profile, database, Vm.Credentials, sql => Vm.OpenQuery(node.Profile, "master", sql));
        var window = new BackupWindow { DataContext = vm };
        await window.ShowDialog(this);
    }

    void OnNodeCompare(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node)
            return;
        var vm = new SchemaCompareViewModel(Vm.Profiles.ToList(), Vm.Credentials, node.Profile, node.Database);
        new SchemaCompareWindow { DataContext = vm }.Show(this);
    }

    async void OnNodeRestore(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
            await ShowRestoreAsync(node);
    }

    async Task ShowRestoreAsync(ExplorerNode node)
    {
        var vm = new RestoreViewModel(node.Profile, node.IsDatabase ? node.Database : null, Vm.Credentials, sql => Vm.OpenQuery(node.Profile, "master", sql));
        var window = new RestoreWindow { DataContext = vm };
        await window.ShowDialog(this);
        // A restore may have created or replaced a database.
        await node.RefreshAsync();
    }

    // ---- home screen actions ----

    /// <summary>The server the user is pointing at, or the first one when nothing is selected.</summary>
    ExplorerNode? TargetServer() =>
        Vm.SelectedNode is { Session: not null } selected ? selected : Vm.Servers.FirstOrDefault();

    Task NeedServerAsync() => Dialogs.ConfirmAsync(this, "Add a server first",
        "This needs a SQL Server to work with. Add one from the sidebar, then try again.", "OK", showCancel: false);

    void OnServerCard(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
            Vm.OpenQuery(node.Profile);
    }

    async void OnRecentClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not HistoryEntry entry)
            return;
        if (!Vm.OpenHistory(entry))
            await Dialogs.ConfirmAsync(this, "That server was removed",
                $"“{entry.ServerName}” is no longer in your list, so this query can't be reopened on it.", "OK", showCancel: false);
    }

    async void OnHomeRestore(object? sender, RoutedEventArgs e)
    {
        if (TargetServer() is { } node)
            await ShowRestoreAsync(node);
        else
            await NeedServerAsync();
    }

    async void OnOpenSqlFile(object? sender, RoutedEventArgs e)
    {
        if (TargetServer() is not { } node)
        {
            await NeedServerAsync();
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SQL file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SQL scripts") { Patterns = ["*.sql"] }, FilePickerFileTypes.All],
        });
        if (files.Count == 0)
            return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            Vm.OpenQuery(node.Profile, node.IsDatabase ? node.Database : null, text, title: files[0].Name);
        }
        catch (Exception ex)
        {
            await Dialogs.ConfirmAsync(this, "Couldn't open the file", ex.Message, "OK", showCancel: false);
        }
    }

    async Task EditAsync(ConnectionProfile? existing)
    {
        var editor = new ConnectionEditorViewModel(existing, Vm.Credentials);
        var window = new ConnectionEditorWindow { DataContext = editor };
        if (!await window.ShowDialog<bool>(this))
            return;
        try
        {
            Vm.SaveProfile(editor.ToProfile(), editor.Password);
        }
        catch (Exception ex)
        {
            await Dialogs.ConfirmAsync(this, "Couldn't save the server", ex.Message, "OK", showCancel: false);
        }
    }
}
