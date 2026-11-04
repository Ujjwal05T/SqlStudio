using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlStudio.Models;
using SqlStudio.Services;

namespace SqlStudio.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    readonly ProfileStore _store;
    readonly ICredentialStore _credentials;
    readonly SettingsStore _settings;
    readonly HistoryStore _historyStore;
    List<HistoryEntry> _history;
    int _queryCounter;

    public MainViewModel() : this(new ProfileStore(), new MacKeychain())
    {
    }

    public MainViewModel(ProfileStore store, ICredentialStore credentials, SettingsStore? settings = null, HistoryStore? history = null)
    {
        _store = store;
        _credentials = credentials;
        _settings = settings ?? new SettingsStore();
        _historyStore = history ?? new HistoryStore();
        _history = _historyStore.Load();
        RefreshRecent();
        Themes = AppThemes.All.Select(p => new ThemeChoice(p, ApplyTheme)).ToList();
        MarkSelectedTheme();
        Profiles = new ObservableCollection<ConnectionProfile>(store.Load().OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase));
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoTabs));
        Servers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoServers));
        foreach (var profile in Profiles)
            Servers.Add(ExplorerNode.ForServer(new ServerSession(profile, credentials)));
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

    public void OpenQuery(ConnectionProfile profile, string? database = null, string? sql = null, bool run = false, string? title = null)
    {
        title ??= $"{profile.DisplayName} · Query {++_queryCounter}";
        var tab = new QueryTabViewModel(profile, _credentials, title, database, sql,
            metadata: Servers.FirstOrDefault(n => n.Profile.Id == profile.Id)?.Session);
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
            _settings.Save(new AppSettings { ThemeId = palette.Id });
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

    void CloseTab(QueryTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        tab.Dispose();
        if (Tabs.Count > 0)
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }
}
