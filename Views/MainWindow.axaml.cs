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
        Closing += OnWindowClosing;
        AddHandler(KeyDownEvent, OnWindowShortcut);
        BuildBackupRestoreMenu();
        if (OperatingSystem.IsWindows())
            WatchPopupsToBlurBackground();

        // The window buttons are placed by macOS in the corner; move them in, and again after anything that may reset them.
        Opened += (_, _) => PlaceWindowButtons();
        SizeChanged += (_, _) => PlaceWindowButtons();
        Activated += (_, _) => PlaceWindowButtons();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                PlaceWindowButtons();
        };
    }

    /// <summary>New query and close tab, on the platform's command key (⌘ on a Mac, Ctrl elsewhere; the Windows key is not usable).</summary>
    void OnWindowShortcut(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != Shortcuts.Command || DataContext is not MainViewModel vm)
            return;
        if (e.Key == Key.T && vm.NewQueryCommand.CanExecute(null))
        {
            vm.NewQueryCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.W && vm.CloseSelectedTabCommand.CanExecute(null))
        {
            vm.CloseSelectedTabCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Windows only: while a popup (any window owned by this one) is open, the main window behind it is softly blurred and dimmed,
    /// so the popup stands out. One check here covers every popup; nothing else has to know about it. A Mac is untouched.
    /// </summary>
    void WatchPopupsToBlurBackground()
    {
        var blur = new Avalonia.Media.BlurEffect { Radius = 3 };
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            var popupOpen = OwnedWindows.Any(w => w.IsVisible);
            RootGrid.Effect = popupOpen ? blur : null;
            RootGrid.Opacity = popupOpen ? 0.88 : 1;
        };
        timer.Start();
    }

    void PlaceWindowButtons()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        // After AppKit has finished its own layout for this change.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (TryGetPlatformHandle() is { } handle && handle.HandleDescriptor == "NSWindow")
                MacTrafficLights.TryPlace(handle.Handle, titleBarHeight: 40, left: 16);
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    // Backup and Restore share one sidebar button; each item is enabled only when the selected node allows it.
    void BuildBackupRestoreMenu()
    {
        var backup = new MenuItem { Header = "Back up database…" };
        backup.Click += OnNodeBackup;
        var restore = new MenuItem { Header = "Restore database…" };
        restore.Click += OnNodeRestore;
        var flyout = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedBottom, ItemsSource = new[] { backup, restore } };
        flyout.Opening += (_, _) =>
        {
            var node = (DataContext as MainViewModel)?.SelectedNode;
            backup.IsEnabled = node?.CanBackup == true;
            restore.IsEnabled = node?.CanRestore == true;
        };
        BackupRestoreButton.Flyout = flyout;
    }

    async void OnRefreshSelected(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedNode is { CanRefresh: true } node)
            await node.RefreshAsync();
    }

    /// <summary>
    /// On a Mac the red button hides the window and leaves the app running (connections, open tabs and unsaved queries stay put) instead of
    /// quitting it. Quitting is ⌘Q, which arrives here as an application shutdown, as does a system shutdown.
    /// </summary>
    public static bool ShouldMinimizeInsteadOfClosing(bool isMac, WindowCloseReason reason) =>
        isMac && reason == WindowCloseReason.WindowClosing;

    void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!ShouldMinimizeInsteadOfClosing(OperatingSystem.IsMacOS(), e.CloseReason))
            return;
        e.Cancel = true;
        Hide(); // the window goes away but the app, its connections and the MCP server keep running; the Dock icon brings it back
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
            vm.ShowWindowRequested += BringToFront;
    }

    public void BringToFront()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    MainViewModel Vm => (MainViewModel)DataContext!;

    ExplorerNode? NodeOf(object? sender) => (sender as Control)?.DataContext as ExplorerNode ?? Vm.SelectedNode;

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
                $"The server and its saved password will be removed from {Platform.ThisComputerText}. Open query tabs keep running. This can't be undone.",
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

    void OnNodeDiagram(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { Database: { } database } node)
            return;
        new ErDiagramWindow { DataContext = new ErDiagramViewModel(node.Profile, database, Vm.Credentials) }.Show(this);
    }

    void OnNodeDependencies(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { Database: { } database } node)
            return;
        // On a table, view, procedure or function the diagram follows that object; on a database it shows every dependency.
        DependencyObject? focus = node.Object is { } o
            ? new DependencyObject(o.Schema, o.Name, node.Kind switch
            {
                NodeKind.View => "view",
                NodeKind.Procedure => "procedure",
                NodeKind.Function => "function",
                _ => "table",
            })
            : null;
        var vm = new ErDiagramViewModel(node.Profile, database, Vm.Credentials, DiagramSource.Dependencies(focus));
        new ErDiagramWindow { DataContext = vm }.Show(this);
    }

    async void OnNodeDisconnect(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { IsServer: true } node)
            return;
        var tabs = Vm.ConnectedTabs(node.Profile.Id);
        if (tabs > 0 && !await Dialogs.ConfirmAsync(this, $"Disconnect from “{node.Profile.DisplayName}”?",
                $"{tabs} open query tab{(tabs == 1 ? "" : "s")} on it will disconnect too and reconnect the next time you run a query. " +
                "Anything running is cancelled, and temp tables or open transactions are lost.",
                "Disconnect", cancelLabel: "Stay connected"))
            return;
        await Vm.DisconnectAsync(node);
    }

    async void OnNodeNote(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { Database: { } database } node)
            return;
        var table = node.NoteTable;
        var what = table ?? database;
        var text = await Dialogs.EditNoteAsync(this, $"Notes for Claude: {what}",
            table is null
                ? "What should Claude know about this database? It sees this in schema_overview."
                : "What should Claude know about this table, such as what a status code means or how it joins to other tables? It sees this in schema_overview.",
            Vm.Notes.Get(node.Profile.Id, database, table) ?? "");
        if (text is not null)
        {
            Vm.Notes.Set(node.Profile.Id, database, table, text);
            node.NoteChanged();
        }
    }

    void OnConnectClaude(object? sender, RoutedEventArgs e) =>
        new ClaudeConnectWindow { DataContext = new ClaudeConnectViewModel(Vm.Claude, Vm.Profiles.ToList()) }.Show(this);

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
