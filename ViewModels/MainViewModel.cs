using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlStudio.Models;
using SqlStudio.Services;
using SqlStudio.Services.Mcp;

namespace SqlStudio.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    readonly ProfileStore _store;
    readonly ICredentialStore _credentials;
    readonly SettingsStore _settings;
    readonly HistoryStore _historyStore;
    List<HistoryEntry> _history;
    int _queryCounter;

    public MainViewModel() : this(new ProfileStore(), CredentialStores.CreateDefault())
    {
    }

    public MainViewModel(ProfileStore store, ICredentialStore credentials, SettingsStore? settings = null, HistoryStore? history = null, SchemaNoteStore? notes = null)
    {
        _store = store;
        _credentials = credentials;
        _settings = settings ?? new SettingsStore();
        _historyStore = history ?? new HistoryStore();
        _history = _historyStore.Load();
        Notes = notes ?? new SchemaNoteStore();
        ExplorerNode.Notes = Notes;
        ExplorerNode.TabConnected = id => Tabs.Any(t => t.ProfileId == id && t.IsConnected);
        RefreshRecent();
        Themes = AppThemes.All.Select(p => new ThemeChoice(p, ApplyTheme)).ToList();
        MarkSelectedTheme();
        Profiles = new ObservableCollection<ConnectionProfile>(store.Load().OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase));
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoTabs));
        Servers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoServers));
        foreach (var profile in Profiles)
            Servers.Add(ExplorerNode.ForServer(new ServerSession(profile, credentials)));
        Claude = new ClaudeAccess(_settings, () => Profiles.ToList(), credentials, notes: Notes);
        Claude.QueryRan += (profile, database, sql) =>
        {
            ShowClaudeQuery(profile, database, sql);
            ShowWindowRequested?.Invoke();
        };
        Claude.Changed += () =>
        {
            OnPropertyChanged(nameof(ClaudeIsOn));
            OnPropertyChanged(nameof(ClaudeStatusText));
        };
        Claude.Start(); // only does anything if the user switched it on
    }

    /// <summary>Raised when Claude shows a query here, so the window can come out of the background or from being minimized.</summary>
    public event Action? ShowWindowRequested;

    /// <summary>The local server that lets the user's Claude Code use these connections.</summary>
    public ClaudeAccess Claude { get; }

    /// <summary>What the user told Claude about their databases and tables.</summary>
    public SchemaNoteStore Notes { get; }

    public bool ClaudeIsOn => Claude.IsListening;

    public string ClaudeStatusText =>
        Claude.Error ?? (Claude.IsListening ? $"Claude access is on (port {Claude.Port}). Click to manage." : "Let your Claude Code use these connections");

    /// <summary>
    /// Shows a query Claude ran. A Claude tab for the same server that the user has not typed in is replaced, so a debugging
    /// session leaves one Claude tab, not a dozen. A tab the user has edited is left alone.
    /// </summary>
    public void ShowClaudeQuery(ConnectionProfile profile, string database, string sql)
    {
        foreach (var old in Tabs.Where(t => t.ProfileId == profile.Id && t.IsUntouchedClaudeTab).ToList())
            CloseTab(old);
        OpenQuery(profile, database, sql, run: true, title: $"{profile.DisplayName} · Claude", fromClaude: true);
    }

    public ICredentialStore Credentials => _credentials;
    public IReadOnlyList<ThemeChoice> Themes { get; }
    public ObservableCollection<HistoryEntry> RecentQueries { get; } = [];
    public bool HasRecent => RecentQueries.Count > 0;
    public bool HasNoRecent => RecentQueries.Count == 0;
    public ObservableCollection<ConnectionProfile> Profiles { get; }
    public ObservableCollection<ExplorerNode> Servers { get; } = [];
    public ObservableCollection<QueryTabViewModel> Tabs { get; } = [];
    public bool HasNoTabs => Tabs.Count == 0;
    public bool HasNoServers => Servers.Count == 0;
    public bool HasNoMatches => Servers.Count > 0 && FilterText.Trim().Length > 0 && Servers.All(n => !n.IsFilterVisible);

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    partial void OnFilterTextChanged(string value)
    {
        ExplorerNode.Filter = value.Trim();
        foreach (var server in Servers)
            server.ApplyFilter();
        OnPropertyChanged(nameof(HasNoMatches));
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewQueryCommand))]
    public partial ExplorerNode? SelectedNode { get; set; }

    [ObservableProperty]
    public partial QueryTabViewModel? SelectedTab { get; set; }

    public void SaveProfile(ConnectionProfile profile, string? password)
    {
        var index = Profiles.ToList().FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
            Profiles[index] = profile;
        else
            Profiles.Add(profile);

        if (!string.IsNullOrEmpty(password) && profile.NeedsPassword)
            _credentials.Set(profile.Id, password);

        _store.Save(Profiles);

        // Rebuild the server's tree so it reconnects with the new settings.
        var node = ExplorerNode.ForServer(new ServerSession(profile, _credentials));
        node.ApplyFilter();
        var existing = Servers.FirstOrDefault(n => n.Profile.Id == profile.Id);
        if (existing is not null)
        {
            Servers[Servers.IndexOf(existing)] = node;
            existing.Session.Dispose();
        }
        else
        {
            Servers.Add(node);
        }
        SelectedNode = node;
    }

    public void DeleteProfile(ConnectionProfile profile)
    {
        Profiles.Remove(profile);
        _credentials.Delete(profile.Id);
        _store.Save(Profiles);
        if (Servers.FirstOrDefault(n => n.Profile.Id == profile.Id) is { } node)
        {
            Servers.Remove(node);
            node.Session.Dispose();
        }
    }

    bool HasSelectedProfile() => SelectedNode?.Session is not null; // placeholder rows have no server

    [RelayCommand(CanExecute = nameof(HasSelectedProfile))]
    void NewQuery()
    {
        if (SelectedNode is { Session: not null } node)
            OpenQuery(node.Profile, node.Database);
    }

    public void OpenQuery(ConnectionProfile profile, string? database = null, string? sql = null, bool run = false, string? title = null, bool fromClaude = false)
    {
        title ??= $"{profile.DisplayName} · Query {++_queryCounter}";
        var tab = new QueryTabViewModel(profile, _credentials, title, database, sql,
            metadata: Servers.FirstOrDefault(n => n.Profile.Id == profile.Id)?.Session);
        tab.FromClaude = fromClaude;
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueryTabViewModel.Status))
                RefreshConnection(tab.ProfileId); // the server row's connected dot follows its tabs too
        };
        tab.CloseRequested += CloseTab;
        tab.Executed += OnExecuted;
        Tabs.Add(tab);
        SelectedTab = tab;
        _ = run ? tab.ConnectAndRunAsync() : tab.ConnectAsync();
    }

    public void SelectTop(ExplorerNode node)
    {
        if (node.Object is { } obj)
            OpenQuery(node.Profile, node.Database, $"SELECT TOP (1000) *\nFROM {obj.FullName};", run: true);
    }

    void ApplyTheme(ThemePalette palette)
    {
        ThemeService.Apply(palette);
        MarkSelectedTheme();
        try
        {
            var settings = _settings.Load(); // keep the other settings: only the theme changes
            settings.ThemeId = palette.Id;
            _settings.Save(settings);
        }
        catch (IOException)
        {
            // Not being able to remember the choice should never get in the way of using it.
        }
    }

    void MarkSelectedTheme()
    {
        foreach (var choice in Themes)
            choice.IsSelected = choice.Palette.Id == ThemeService.Current.Id;
    }

    void OnExecuted(HistoryEntry entry)
    {
        _history = HistoryStore.Add(_history, entry);
        RefreshRecent();
        try
        {
            _historyStore.Save(_history);
        }
        catch (IOException)
        {
        }
    }

    void RefreshRecent()
    {
        RecentQueries.Clear();
        foreach (var entry in _history.Take(8))
            RecentQueries.Add(entry);
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(HasNoRecent));
    }

    /// <summary>Reopens a recent query on its server. Returns false when that server has since been removed.</summary>
    public bool OpenHistory(HistoryEntry entry)
    {
        var profile = Profiles.FirstOrDefault(p => p.Id == entry.ProfileId);
        if (profile is null)
            return false;
        OpenQuery(profile, entry.Database, entry.Sql);
        return true;
    }

    [RelayCommand]
    void CloseSelectedTab()
    {
        if (SelectedTab is { } tab)
            CloseTab(tab);
    }

    void RefreshConnection(Guid profileId) => Servers.FirstOrDefault(n => n.Profile.Id == profileId)?.ConnectionChanged();

    /// <summary>Open query tabs on this server that currently hold a connection.</summary>
    public int ConnectedTabs(Guid profileId) => Tabs.Count(t => t.ProfileId == profileId && t.IsConnected);

    /// <summary>Disconnects the server: its tree connection and every query tab on it. Tabs stay open and reconnect when run.</summary>
    public async Task DisconnectAsync(ExplorerNode server)
    {
        foreach (var tab in Tabs.Where(t => t.ProfileId == server.Profile.Id).ToList())
            tab.Disconnect();
        await server.DisconnectAsync();
    }

    void CloseTab(QueryTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        tab.Dispose();
        RefreshConnection(tab.ProfileId);
        if (Tabs.Count > 0)
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }
}
