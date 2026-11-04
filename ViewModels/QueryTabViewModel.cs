using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.SqlClient;
using SqlStudio.Models;
using SqlStudio.Services;
using SqlStudio.Services.Completion;

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
    readonly ServerSession _metadata;
    readonly bool _ownsMetadata;
    SqlConnection? _connection;
    CancellationTokenSource? _cts;
    Task? _connecting;
    bool _syncingDatabase;

    public QueryTabViewModel(ConnectionProfile profile, ICredentialStore credentials, string title, string? database = null, string? sql = null,
        ServerSession? metadata = null)
    {
        _profile = profile.Clone();
        _credentials = credentials;
        // Tabs on the same server share one schema cache, so suggestions are instant after the first load.
        _ownsMetadata = metadata is null;
        _metadata = metadata ?? new ServerSession(profile, credentials);
        Completion = new SqlCompletionProvider(_metadata.Schema);
        Title = title;
        Panels = [_messages];
        SelectedPanel = _messages;
        ScriptProvider = () => SqlText;
        SqlText = sql ?? "";
        SelectedDatabase = database;
    }

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
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand), nameof(CancelCommand))]
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
        try
        {
            _connection?.Dispose();
            _connection = null;
            var connection = await ServerSession.OpenAsync(_profile, _credentials, SelectedDatabase);
            _connection = connection;

            var databases = await Task.Run(() => LoadDatabasesAsync(connection));
            Databases.Clear();
            foreach (var name in databases)
                Databases.Add(name);
            SyncDatabase();
            Status = ConnectionStatus.Connected;
            ConnectionText = $"{_profile.DisplayName} · SQL Server {connection.ServerVersion}";
        }
        catch (Exception ex)
        {
            Status = ConnectionStatus.Failed;
            ConnectionText = $"Couldn't connect to {_profile.DisplayName}";
            _messages.Add($"Couldn't connect to {_profile.DisplayName}. {ex.Message}", true);
            SelectedPanel = _messages;
        }
    }

    static async Task<List<string>> LoadDatabasesAsync(SqlConnection connection)
    {
        var names = new List<string>();
        await using var command = new SqlCommand(
            "SELECT name FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    bool CanExecute() => !IsExecuting;

    [RelayCommand(CanExecute = nameof(CanExecute))]
    async Task ExecuteAsync()
    {
        var script = ScriptProvider();
        if (string.IsNullOrWhiteSpace(script))
            return;

        IsExecuting = true;
        ClearResults();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
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
            // Off the UI thread: row reading must not run on the UI synchronization context.
            var summary = await Task.Run(() => QueryExecutor.ExecuteAsync(connection, script, this, RowLimit, ct));

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

    void ClearResults()
    {
        _messages.Lines.Clear();
        for (var i = Panels.Count - 1; i >= 0; i--)
            if (Panels[i] is ResultSetViewModel)
                Panels.RemoveAt(i);
    }

    partial void OnSelectedDatabaseChanged(string? value)
    {
        if (_syncingDatabase || value is null || IsExecuting || _connection is not { State: System.Data.ConnectionState.Open } connection)
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
