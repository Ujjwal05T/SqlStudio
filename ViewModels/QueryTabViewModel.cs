using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.SqlClient;
using SqlStudio.Models;
using SqlStudio.Services;
using SqlStudio.Services.Company;
using SqlStudio.Services.Completion;
using SqlStudio.Services.Guard;

namespace SqlStudio.ViewModels;

public enum ConnectionStatus { Idle, Connecting, Connected, Failed }

/// <summary>
/// One query window. It owns a single open session, so USE, temp tables and transactions behave like SSMS.
/// </summary>
public sealed partial class QueryTabViewModel : ViewModelBase, IQuerySink, IDisposable
{
    readonly ConnectionProfile _profile;
    readonly ICredentialStore _credentials;
    readonly MessagesViewModel _messages = new();
    readonly IQueryAuditor? _auditor;
    readonly ServerSession _metadata;
    readonly bool _ownsMetadata;
    SqlConnection? _connection;
    CancellationTokenSource? _cts;
    Task? _connecting;
    bool _syncingDatabase;

    public QueryTabViewModel(ConnectionProfile profile, ICredentialStore credentials, string title, string? database = null, string? sql = null,
        ServerSession? metadata = null, IQueryAuditor? auditor = null)
    {
        _profile = profile.Clone();
        _auditor = auditor;
        _credentials = credentials;
        // Tabs on the same server share one schema cache, so suggestions are instant after the first load.
        _ownsMetadata = metadata is null;
        _metadata = metadata ?? new ServerSession(profile, credentials);
        Completion = new SqlCompletionProvider(_profile.Managed is { } access
            ? new RestrictedSchemaSource(_metadata.Schema, access)
            : _metadata.Schema);
        Title = title;
        Panels = [_messages];
        SelectedPanel = _messages;
        ScriptProvider = () => SqlText;
        SqlText = sql ?? "";
        _startSql = SqlText;
        // A company tab never starts in a database the user can't use
        SelectedDatabase = _profile.Managed is { } managed && database is not null && !managed.CanUse(database)
            ? _profile.DefaultDatabase
            : database;
    }

    readonly string _startSql;

    public Guid ProfileId => _profile.Id;
    public string ServerName => _profile.DisplayName;

    /// <summary>Opened by Claude's run_query, so the tab can be marked and replaced by Claude's next query.</summary>
    public bool FromClaude { get; set; }

    /// <summary>A Claude tab the user has not typed in, so Claude may replace it without losing anything of theirs.</summary>
    public bool IsUntouchedClaudeTab => FromClaude && SqlText == _startSql;

    public event Action<QueryTabViewModel>? CloseRequested;

    /// <summary>Raised after each run, so the app can remember it in the recent-queries list.</summary>
    public event Action<HistoryEntry>? Executed;

    public ObservableCollection<string> Databases { get; } = [];
    public ObservableCollection<ResultPanelViewModel> Panels { get; }

    /// <summary>Set by the view: returns the selected text, or the whole script when nothing is selected.</summary>
    public Func<string> ScriptProvider { get; set; }

    /// <summary>Kept here so the text survives the view being rebuilt when the user switches tabs.</summary>
    public string SqlText { get; set; } = "";

    public int RowLimit { get; set; } = QueryExecutor.DefaultRowLimit;

    /// <summary>Source of table, column and keyword suggestions. Settable so tests can plug in a fake server.</summary>
    public SqlCompletionProvider Completion { get; set; }

    /// <summary>Suggestions for the text at the caret, using whichever database this tab is currently in.</summary>
    public Task<CompletionResult?> GetCompletionAsync(string text, int caret, bool manual) =>
        Completion.GetAsync(text, caret, SelectedDatabase ?? _profile.DefaultDatabase, manual);

    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>Left side of the status bar: which server this tab talks to and whether it is reachable.</summary>
    [ObservableProperty]
    public partial string ConnectionText { get; set; } = "Not connected";

    /// <summary>Right side of the status bar: what the last run did.</summary>
    [ObservableProperty]
    public partial string ResultSummary { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsConnecting), nameof(IsFailed), nameof(IsBusy))]
    public partial ConnectionStatus Status { get; set; }

    [ObservableProperty]
    public partial string? SelectedDatabase { get; set; }

    [ObservableProperty]
    public partial ResultPanelViewModel? SelectedPanel { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand), nameof(CancelCommand), nameof(ShowEstimatedPlanCommand), nameof(ShowActualPlanCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial bool IsExecuting { get; set; }

    public bool HasEnv => _profile.HasEnvironment;
    public string EnvShort => _profile.EnvironmentShort;
    public string EnvLabel => _profile.EnvironmentLabel;
    public bool IsDev => _profile.Environment == ServerEnvironment.Development;
    public bool IsStaging => _profile.Environment == ServerEnvironment.Staging;
    public bool IsProd => _profile.Environment == ServerEnvironment.Production;

    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsConnecting => Status == ConnectionStatus.Connecting;
    public bool IsFailed => Status == ConnectionStatus.Failed;
    public bool IsBusy => IsExecuting || IsConnecting;

    public Task ConnectAsync() => _connecting = ConnectCoreAsync();

    int _epoch; // bumped on disconnect, so a connection that was still opening cannot come back to life

    /// <summary>Closes this tab's connection (cancelling anything running). The next run reconnects by itself.</summary>
    public void Disconnect()
    {
        _epoch++;
        _cts?.Cancel();
        _connection?.Dispose();
        _connection = null;
        _connecting = null;
        Status = ConnectionStatus.Idle;
        ConnectionText = $"Disconnected from {_profile.DisplayName}";
    }

    /// <summary>Connects, then runs the tab's script straight away (used by "Select top 1000").</summary>
    public async Task ConnectAndRunAsync()
    {
        await ConnectAsync();
        if (_connection is { State: System.Data.ConnectionState.Open })
            await ExecuteAsync();
    }

    async Task ConnectCoreAsync()
    {
        Status = ConnectionStatus.Connecting;
        ConnectionText = $"Connecting to {_profile.DisplayName}…";
        var epoch = _epoch;
        try
        {
            _connection?.Dispose();
            _connection = null;
            var connection = await ServerSession.OpenAsync(_profile, _credentials, SelectedDatabase);
            if (epoch != _epoch)
            {
                await connection.DisposeAsync(); // disconnected while this was opening
                return;
            }
            _connection = connection;

            Status = ConnectionStatus.Connected;
            _auditor?.Connected(_profile, connection.Database);
            ConnectionText = $"{_profile.DisplayName} · SQL Server {connection.ServerVersion}";
            _ = LoadDatabaseListAsync(epoch); // the editor is usable now; the dropdown fills in when it arrives
        }
        catch (Exception ex)
        {
            Status = ConnectionStatus.Failed;
            ConnectionText = $"Couldn't connect to {_profile.DisplayName}";
            _messages.Add($"Couldn't connect to {_profile.DisplayName}. {ex.Message}", true);
            SelectedPanel = _messages;
        }
    }

    /// <summary>
    /// Fills the database dropdown in the background. It uses its own short connection to master, because the tab's
    /// connection may be running the user's script, and master never wakes an AUTO_CLOSE database.
    /// </summary>
    async Task LoadDatabaseListAsync(int epoch)
    {
        try
        {
            await using var master = await ServerSession.OpenAsync(_profile, _credentials, "master");
            var databases = await LoadDatabasesAsync(master);
            if (epoch != _epoch)
                return; // disconnected meanwhile
            ShowDatabases(databases, _connection?.Database ?? SelectedDatabase);
        }
        catch (Exception ex)
        {
            if (epoch == _epoch)
                _messages.Add($"Couldn't load the database list. {ex.Message}", true);
        }
    }

    /// <summary>
    /// Fills the dropdown and selects <paramref name="current"/>. The tab may already hold that name (it was set before the list
    /// existed, so the dropdown could not show it), and assigning an equal value tells the dropdown nothing, so the value is cleared first.
    /// </summary>
    public void ShowDatabases(IEnumerable<string> names, string? current)
    {
        Databases.Clear();
        foreach (var name in names)
            if (_profile.Managed is not { } managed || managed.CanUse(name))
                Databases.Add(name);
        _syncingDatabase = true;
        try
        {
            SelectedDatabase = null; // a real change, so the dropdown looks again
            SelectedDatabase = current;
        }
        finally
        {
            _syncingDatabase = false;
        }
    }

    static async Task<List<string>> LoadDatabasesAsync(SqlConnection connection)
    {
        var names = new List<string>();
        await using var command = new SqlCommand(
            // No HAS_DBACCESS: it opens every database to check access, which wakes AUTO_CLOSE databases (20-30 s on first connect).
            "SELECT name FROM sys.databases WHERE state = 0 ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    bool CanExecute() => !IsExecuting;

    [RelayCommand(CanExecute = nameof(CanExecute))]
    Task ExecuteAsync() => RunAsync(PlanMode.None);

    /// <summary>Asks the server how it would run the script, without running it.</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    Task ShowEstimatedPlanAsync() => RunAsync(PlanMode.Estimated);

    /// <summary>Runs the script and shows the plan the server really used, with real row counts.</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    Task ShowActualPlanAsync() => RunAsync(PlanMode.Actual);

    async Task RunAsync(PlanMode planMode)
    {
        var script = ScriptProvider();
        if (string.IsNullOrWhiteSpace(script))
            return;

        // Claimed before the guard's await: a double click or a dropdown change during the check must not start a second run.
        IsExecuting = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            string? checkedDatabase = null;
            if (_profile.Managed is { } managed)
            {
                // Before anything is opened or sent. The verdict message may quote SQL (parse errors), so it goes to Messages only.
                var database = _connection is { State: System.Data.ConnectionState.Open } live
                    ? live.Database
                    : SelectedDatabase ?? _profile.DefaultDatabase;
                checkedDatabase = database;
                var verdict = await QueryGuard.CheckAsync(script, database, managed, _profile.DisplayName);
                if (!verdict.Allowed)
                {
                    ClearResults();
                    _messages.Add(verdict.Message ?? "Blocked by company access.", true);
                    ResultSummary = "Blocked by company access";
                    SelectedPanel = _messages;
                    _auditor?.Blocked(_profile, database);
                    return;
                }
            }

            ClearResults();
            ResultSummary = "Running…";
            await (_connecting ?? ConnectAsync());
            if (_connection is not { State: System.Data.ConnectionState.Open })
            {
                await ConnectAsync(); // session dropped (timeout, failover): reconnect to the last database
                if (_connection is not { State: System.Data.ConnectionState.Open })
                {
                    ResultSummary = "Not run: no connection";
                    return;
                }
            }

            var connection = _connection;
            // The script was checked against one database: it must not run in another.
            if (checkedDatabase is not null && !string.Equals(connection.Database, checkedDatabase, StringComparison.OrdinalIgnoreCase))
            {
                ResultSummary = "Not run: the database changed";
                _messages.Add($"Not run. The query was checked against {checkedDatabase}, but this tab is now in {connection.Database}. Run it again.", true);
                SelectedPanel = _messages;
                return;
            }
            // Off the UI thread: row reading must not run on the UI synchronization context.
            var summary = await Task.Run(() => QueryExecutor.ExecuteAsync(connection, script, this, RowLimit, ct, planMode));

            if (_profile.Managed is { } access && !access.CanUse(connection.Database))
            {
                var left = connection.Database;
                Disconnect();
                _messages.Add($"This tab was disconnected because it ended up in {left}, which you don't have access to.", true);
                SelectedPanel = _messages;
            }
            SyncDatabase();
            var rows = Panels.OfType<ResultSetViewModel>().Sum(p => p.Data.Rows.Count);
            var elapsed = FormatElapsed(summary.Elapsed);
            ResultSummary = summary switch
            {
                { Cancelled: true } => $"Cancelled after {elapsed}",
                { Errors: > 0 } => $"Finished with {summary.Errors} error{(summary.Errors == 1 ? "" : "s")} · {elapsed}",
                _ when rows > 0 => $"{rows:N0} row{(rows == 1 ? "" : "s")} · {elapsed}",
                _ => $"Completed · {elapsed}",
            };
            // A script that created or dropped objects makes the cached names stale
            if (SchemaCache.LooksLikeSchemaChange(script))
                _metadata.Schema.Invalidate(SelectedDatabase);
            if (summary.Cancelled)
                _messages.Add("Query cancelled.", true);
            if (planMode != PlanMode.Estimated && !_profile.IsManaged) // company runs are never remembered
                // an estimate runs nothing, so it is not a run worth remembering
                Executed?.Invoke(new HistoryEntry(
                    DateTime.Now, _profile.Id, _profile.DisplayName, SelectedDatabase, script.Trim(),
                    summary is { Errors: 0, Cancelled: false }, summary.Elapsed.TotalSeconds));
            SelectedPanel = Panels.Count > 1 && summary.Errors == 0 ? Panels[0] : _messages;
        }
        catch (Exception ex)
        {
            ResultSummary = "Failed";
            _messages.Add(ex.Message, true);
            SelectedPanel = _messages;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsExecuting = false;
        }
    }

    static string FormatElapsed(TimeSpan t) => t.TotalSeconds switch
    {
        < 1 => $"{t.TotalMilliseconds:N0} ms",
        < 60 => $"{t.TotalSeconds:0.00} s",
        _ => $"{(int)t.TotalMinutes}:{t.Seconds:00}",
    };

    [RelayCommand(CanExecute = nameof(IsExecuting))]
    void Cancel() => _cts?.Cancel();

    [RelayCommand]
    void Close() => CloseRequested?.Invoke(this);

    /// <summary>Removes result grids, plans and messages (used when a company sign-out must leave nothing on screen).</summary>
    public void ClearResults()
    {
        ResultSummary = "";
        _messages.Lines.Clear();
        for (var i = Panels.Count - 1; i >= 0; i--)
            if (Panels[i] is not MessagesViewModel)
                Panels.RemoveAt(i);
    }

    partial void OnSelectedDatabaseChanged(string? value)
    {
        if (_syncingDatabase || value is null || IsExecuting)
            return;
        if (_profile.Managed is { } managed)
        {
            var verdict = QueryGuard.CheckDatabaseSwitch(value, managed, _profile.DisplayName);
            if (!verdict.Allowed)
            {
                _messages.Add(verdict.Message ?? $"You don't have access to {value}.", true);
                SelectedPanel = _messages;
                _auditor?.Blocked(_profile, value);
                if (_connection is { State: System.Data.ConnectionState.Open })
                    SyncDatabase();
                else
                    RestoreDefaultDatabase();
                return;
            }
        }
        if (_connection is not { State: System.Data.ConnectionState.Open } connection)
            return;
        _ = ChangeDatabaseAsync(connection, value);
    }

    async Task ChangeDatabaseAsync(SqlConnection connection, string database)
    {
        try
        {
            await connection.ChangeDatabaseAsync(database);
        }
        catch (Exception ex)
        {
            _messages.Add($"Couldn't switch to {database}. {ex.Message}", true);
            SelectedPanel = _messages;
            SyncDatabase();
        }
    }

    void RestoreDefaultDatabase()
    {
        _syncingDatabase = true;
        try
        {
            SelectedDatabase = _profile.DefaultDatabase;
        }
        finally
        {
            _syncingDatabase = false;
        }
    }

    /// <summary>Mirror the session's current database into the combo (a script may have run USE).</summary>
    void SyncDatabase()
    {
        if (_connection is not { State: System.Data.ConnectionState.Open } connection)
            return;
        _syncingDatabase = true;
        try
        {
            SelectedDatabase = connection.Database;
        }
        finally
        {
            _syncingDatabase = false;
        }
    }

    // IQuerySink: called from the thread running the query; results are applied on the UI thread, in order.
    void IQuerySink.Message(string text, bool isError) => Dispatcher.UIThread.Invoke(() => _messages.Add(text, isError));

    void IQuerySink.PlanReady(string xml) => Dispatcher.UIThread.Invoke(() =>
    {
        var number = Panels.OfType<PlanViewModel>().Count() + 1;
        Panels.Insert(Panels.Count - 1, new PlanViewModel(xml, number == 1 ? "Plan" : $"Plan {number}"));
    });

    void IQuerySink.ResultSetReady(ResultSet set) => Dispatcher.UIThread.Invoke(() =>
    {
        var index = Panels.Count; // Messages is always last, so this is 1-based
        Panels.Insert(Panels.Count - 1, new ResultSetViewModel(set, index, new ResultEditContext(_profile, _credentials)));
    });

    public void Dispose()
    {
        _cts?.Cancel();
        _connection?.Dispose();
        _connection = null;
        if (_ownsMetadata)
            _metadata.Dispose();
    }
}
