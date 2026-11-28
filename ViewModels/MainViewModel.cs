using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlStudio.Models;
using Avalonia.Threading;
using SqlStudio.Services;
using SqlStudio.Services.Company;
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
    // Every company server id seen this session. Kept after sign-out so a late history entry is still dropped.
    readonly HashSet<Guid> _companyIds = [];

    public MainViewModel() : this(new ProfileStore(), CredentialStores.CreateDefault(), tokenStore: SecureTokenStores.CreateDefault())
    {
    }

    public MainViewModel(ProfileStore store, ICredentialStore credentials, SettingsStore? settings = null, HistoryStore? history = null, SchemaNoteStore? notes = null, CompanySession? company = null, ISecureTokenStore? tokenStore = null)
    {
        _store = store;
        _settings = settings ?? new SettingsStore();
        // Company passwords go to memory only; everything else reaches the platform store. A caller that already
        // passes a routing store (tests injecting a session) keeps it.
        var secrets = new InMemoryCredentialStore();
        var routing = credentials as RoutingCredentialStore ?? new RoutingCredentialStore(credentials, secrets);
        _credentials = routing;
        Company = company ?? new CompanySession(new CompanyApiClient(CompanyApiClient.CreateHttpClient()), routing, secrets, _settings, TimeProvider.System, a => Dispatcher.UIThread.Post(a), tokenStore);
        routing.DirectUsable = () => Company.DirectConnectionsUsable; // the one gate every personal connection goes through
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
            Servers.Add(LockIfNeeded(ExplorerNode.ForServer(new ServerSession(profile, _credentials))));
        CompanyServers.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasNoCompanyServers)); OnPropertyChanged(nameof(HasNoMatches)); };
        Company.Changed += () => { ApplyCompanySnapshot(Company.Profiles); if (Company.State == CompanyState.Locked) DisconnectLockedCompany(); RaiseCompanyState(); };
        Company.DirectConnectionsUsableChanged += _ => OnDirectUsableChanged();
        Company.SnapshotChanged += OnCompanySnapshotChanged;
        Company.ConnectionChanged += OnCompanyConnectionChanged;
        Company.SignedOut += reason => { LockCompany(reason); RaiseCompanyState(); };
        Claude = new ClaudeAccess(_settings, () => Profiles.ToList(), _credentials, notes: Notes);
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

    /// <summary>True while personal connections may be opened: signed in to the company, online, and allowed by the admin.</summary>
    public bool DirectConnectionsUsable => Company.DirectConnectionsUsable;

    /// <summary>The reason shown on the disabled "Add server" controls, or null when they are enabled.</summary>
    public string? DirectLockedMessage => DirectConnectionsUsable ? null : DirectConnectionGate.Message;

    /// <summary>Asked for by the Add server buttons; the window opens the editor.</summary>
    public event Action? AddConnectionRequested;

    [RelayCommand(CanExecute = nameof(DirectConnectionsUsable))]
    void AddConnection() => AddConnectionRequested?.Invoke();

    ExplorerNode LockIfNeeded(ExplorerNode node)
    {
        node.DirectLocked = !DirectConnectionsUsable;
        return node;
    }

    /// <summary>
    /// Personal connections just became usable or locked. Locking works like <see cref="LockCompany"/> does for company servers:
    /// open connections close, tabs keep their text but lose their results, and the personal servers show disabled.
    /// </summary>
    void OnDirectUsableChanged()
    {
        var usable = DirectConnectionsUsable;
        foreach (var node in Servers)
            node.DirectLocked = !usable;
        if (!usable)
        {
            foreach (var tab in Tabs.Where(t => !_companyIds.Contains(t.ProfileId)).ToList())
            {
                tab.Disconnect();
                tab.ClearResults();
            }
            foreach (var node in Servers.Where(n => n.Session.IsConnected || n.Children.Count > 0).ToList())
                _ = DisconnectNodeAsync(node);
        }
        OnPropertyChanged(nameof(DirectConnectionsUsable));
        OnPropertyChanged(nameof(DirectLockedMessage));
        AddConnectionCommand.NotifyCanExecuteChanged();
        NewQueryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The company sign-in and its managed servers.</summary>
    public CompanySession Company { get; }

    public ObservableCollection<ConnectionProfile> CompanyProfiles { get; } = [];
    public ObservableCollection<ExplorerNode> CompanyServers { get; } = [];
    /// <summary>A fresh sign-in form: the sidebar lock button opens it in a window.</summary>
    public CompanySignInViewModel CreateCompanySignIn() => new(Company, _settings);
    public bool IsCompanySignedIn => Company.State is CompanyState.SignedIn or CompanyState.Offline;
    public bool ShowCompanySection => Company.State != CompanyState.SignedOut;
    public bool ShowCompanySignInCard => Company.State == CompanyState.SignedOut && !_resumingCompany;

    bool _resumingCompany;

    /// <summary>
    /// Startup: signs in again from the saved sign-in. The sign-in card stays hidden while this runs, and while the company server
    /// can't be reached (State is Offline then, and the retry loop keeps trying). It shows when nothing was saved or the saved sign-in was refused.
    /// </summary>
    public async Task ResumeCompanyAsync()
    {
        _resumingCompany = true;
        try { await Task.Run(() => Company.TryResumeAsync(default)).ConfigureAwait(true); }
        catch { } // a broken secure store or network must not stop the app starting
        finally
        {
            _resumingCompany = false;
            RaiseCompanyState();
        }
    }
    public bool HasNoCompanyServers => CompanyServers.Count == 0;
    public string CompanyStatusText => Company.StatusText;

    void RaiseCompanyState()
    {
        OnPropertyChanged(nameof(IsCompanySignedIn));
        OnPropertyChanged(nameof(ShowCompanySection));
        OnPropertyChanged(nameof(ShowCompanySignInCard));
        OnPropertyChanged(nameof(CompanyStatusText));
    }

    /// <summary>Rebuilds the Company tree from the session's profiles. Rows whose profile is unchanged are kept, so a refresh doesn't fold the tree.</summary>
    public void ApplyCompanySnapshot(IReadOnlyList<ConnectionProfile> profiles)
    {
        var sorted = profiles.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var old = CompanyServers.ToList();
        var nodes = new List<ExplorerNode>();
        foreach (var p in sorted)
        {
            _companyIds.Add(p.Id);
            var node = old.FirstOrDefault(n => ReferenceEquals(n.Profile, p)) ?? ExplorerNode.ForServer(new ServerSession(p, _credentials));
            node.ApplyFilter();
            nodes.Add(node);
        }
        var selected = SelectedCompanyNode;
        CompanyProfiles.Clear();
        foreach (var p in sorted)
            CompanyProfiles.Add(p);
        CompanyServers.Clear();
        foreach (var n in nodes)
            CompanyServers.Add(n);
        foreach (var gone in old.Where(o => !nodes.Contains(o)))
            gone.Session.Dispose();
        if (selected is not null && !nodes.Contains(selected))
            ClearCompanySelection();
    }

    /// <summary>Company access ended: revoke every server, disconnect their tabs (kept open for the SQL text) and clear the tree.</summary>
    public void LockCompany(string reason)
    {
        foreach (var p in CompanyProfiles)
            p.Managed?.Revoke(reason);
        foreach (var tab in Tabs.Where(t => _companyIds.Contains(t.ProfileId)).ToList())
        {
            tab.Disconnect();
            tab.ClearResults(); // sign-out leaves the SQL text only
        }
        var old = CompanyServers.ToList();
        CompanyProfiles.Clear();
        CompanyServers.Clear();
        foreach (var n in old)
            n.Session.Dispose();
        ClearCompanySelection();
    }

    void DisconnectCompanyTabs(Guid profileId)
    {
        foreach (var tab in Tabs.Where(t => t.ProfileId == profileId).ToList())
            tab.Disconnect();
    }

    /// <summary>The grace ran out: the access objects are already revoked, so close what is open. Tabs stay for their SQL text.</summary>
    void DisconnectLockedCompany()
    {
        foreach (var tab in Tabs.Where(t => _companyIds.Contains(t.ProfileId)).ToList())
            tab.Disconnect();
        foreach (var node in CompanyServers.Where(n => n.Session.IsConnected).ToList())
            _ = DisconnectNodeAsync(node);
    }

    static async Task DisconnectNodeAsync(ExplorerNode node)
    {
        try { await node.DisconnectAsync(); } catch { } // closing a stale connection must never break a refresh
    }

    /// <summary>A refresh removed servers: the tree already dropped them and their access is revoked; close their tabs' connections.</summary>
    void OnCompanySnapshotChanged(SnapshotDiff diff)
    {
        foreach (var gone in diff.Removed)
            DisconnectCompanyTabs(gone.Id);
    }

    /// <summary>The backend changed how to reach a server (profile already updated in place): drop connections made the old way.</summary>
    void OnCompanyConnectionChanged(IReadOnlyList<ConnectionProfile> changed)
    {
        foreach (var p in changed)
        {
            DisconnectCompanyTabs(p.Id);
            if (CompanyServers.FirstOrDefault(n => n.Profile.Id == p.Id) is { } node)
                _ = DisconnectNodeAsync(node);
        }
    }

    void ClearCompanySelection()
    {
        SelectedCompanyNode = null;
        if (SelectedNode is { } n && _companyIds.Contains(n.Profile.Id))
            SelectedNode = null;
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
    public bool HasNoMatches => (Servers.Count > 0 || CompanyServers.Count > 0) && FilterText.Trim().Length > 0
        && Servers.All(n => !n.IsFilterVisible) && CompanyServers.All(n => !n.IsFilterVisible);

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    partial void OnFilterTextChanged(string value)
    {
        ExplorerNode.Filter = value.Trim();
        foreach (var server in Servers.Concat(CompanyServers))
            server.ApplyFilter();
        OnPropertyChanged(nameof(HasNoMatches));

        // Databases can only be matched once their server's list is loaded, so load it for servers that are already open to us.
        if (ExplorerNode.Filter.Length > 0)
            foreach (var server in Servers.Concat(CompanyServers).Where(n => n.CanLoadForSearch).ToList())
                _ = LoadForSearchAsync(server);
    }

    async Task LoadForSearchAsync(ExplorerNode server)
    {
        try { await server.LoadForSearchAsync(); } catch { } // a failed lookup only means this server is not searched
        OnPropertyChanged(nameof(HasNoMatches));
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewQueryCommand))]
    public partial ExplorerNode? SelectedNode { get; set; }

    [ObservableProperty]
    public partial ExplorerNode? SelectedUserNode { get; set; }

    [ObservableProperty]
    public partial ExplorerNode? SelectedCompanyNode { get; set; }

    partial void OnSelectedUserNodeChanged(ExplorerNode? value)
    {
        if (value is null)
            return;
        SelectedCompanyNode = null;
        SelectedNode = value;
    }

    partial void OnSelectedCompanyNodeChanged(ExplorerNode? value)
    {
        if (value is null)
            return;
        SelectedUserNode = null;
        SelectedNode = value;
    }

    const string ManagedMessage = "Company connections are managed by your admin.";

    bool IsManagedId(ConnectionProfile profile) => profile.IsManaged || _companyIds.Contains(profile.Id);

    [ObservableProperty]
    public partial QueryTabViewModel? SelectedTab { get; set; }

    public void SaveProfile(ConnectionProfile profile, string? password)
    {
        if (IsManagedId(profile))
            throw new InvalidOperationException(ManagedMessage);
        var index = Profiles.ToList().FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
            Profiles[index] = profile;
        else
            Profiles.Add(profile);

        if (!string.IsNullOrEmpty(password) && profile.NeedsPassword)
            _credentials.Set(profile.Id, password);

        _store.Save(Profiles);

        // Rebuild the server's tree so it reconnects with the new settings.
        var node = LockIfNeeded(ExplorerNode.ForServer(new ServerSession(profile, _credentials)));
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
        SelectedUserNode = node;
    }

    public void DeleteProfile(ConnectionProfile profile)
    {
        if (IsManagedId(profile))
            throw new InvalidOperationException(ManagedMessage);
        Profiles.Remove(profile);
        _credentials.Delete(profile.Id);
        _store.Save(Profiles);
        if (Servers.FirstOrDefault(n => n.Profile.Id == profile.Id) is { } node)
        {
            Servers.Remove(node);
            node.Session.Dispose();
        }
    }

    bool HasSelectedProfile() => SelectedNode?.Session is not null && SelectedNode is { DirectLocked: false }; // placeholder rows have no server

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
            metadata: FindServer(profile.Id)?.Session,
            auditor: IsManagedId(profile) ? Company.Audit : null);
        tab.FromClaude = fromClaude;
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueryTabViewModel.Status))
                RefreshConnection(tab.ProfileId); // the server row's connected dot follows its tabs too
        };
        tab.CloseRequested += CloseTab;
        if (!IsManagedId(profile))
            tab.Executed += RecordHistory; // company runs are never recorded
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

    public void RecordHistory(HistoryEntry entry)
    {
        if (_companyIds.Contains(entry.ProfileId))
            return;
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

    ExplorerNode? FindServer(Guid id) => Servers.Concat(CompanyServers).FirstOrDefault(n => n.Profile.Id == id);

    void RefreshConnection(Guid profileId) => FindServer(profileId)?.ConnectionChanged();

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
