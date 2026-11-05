using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using Avalonia.Threading;
using Microsoft.Data.SqlClient;
using SqlStudio.Models;

namespace SqlStudio.Services.Mcp;

/// <summary>
/// Lets the user's own Claude Code (or Claude Desktop) use SqlStudio's saved connections through a local MCP server.
/// Claude never receives a password: it asks this app, which opens the connection itself. The user switches the server on,
/// decides which servers Claude may use, and can see every call it makes.
/// </summary>
public sealed class ClaudeAccess : IMcpDatabases, IDisposable
{
    public const int DefaultPort = 47821;
    const int MaxActivity = 200;

    readonly SettingsStore _settings;
    readonly Func<IReadOnlyList<ConnectionProfile>> _profiles;
    readonly ICredentialStore _credentials;
    readonly Action<Action> _post;
    readonly SchemaNoteStore? _notes;
    McpHttpServer? _server;

    public ClaudeAccess(SettingsStore settings, Func<IReadOnlyList<ConnectionProfile>> profiles, ICredentialStore credentials, Action<Action>? post = null, SchemaNoteStore? notes = null)
    {
        _notes = notes;
        _settings = settings;
        _profiles = profiles;
        _credentials = credentials;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
    }

    /// <summary>Newest first. Changes on the UI thread, so a window can bind to it.</summary>
    public ObservableCollection<McpActivity> Activity { get; } = [];

    public event Action? Changed;

    /// <summary>Raised on the UI thread after Claude's run_query succeeds, so the app can show the same query and its rows in a tab.</summary>
    public event Action<ConnectionProfile, string, string>? QueryRan;

    /// <summary>Why the server could not start (usually: the port is in use). Null when all is well or it is off.</summary>
    public string? Error { get; private set; }

    public bool IsEnabled => Load().Enabled;
    public bool IsListening => _server?.IsRunning == true;
    public int Port => Load().Port;

    ClaudeAccessSettings Load() => _settings.Load().ClaudeAccess;

    void Update(Action<ClaudeAccessSettings> change)
    {
        var all = _settings.Load();
        change(all.ClaudeAccess);
        try
        {
            _settings.Save(all);
        }
        catch (IOException)
        {
            // Not being able to remember a choice must not stop it applying for this session.
        }
    }

    /// <summary>The one command the user runs to connect their Claude Code.</summary>
    public string AddCommand => $"claude mcp add --transport http sqlstudio http://127.0.0.1:{Port}/mcp --header \"Authorization: Bearer {EnsureToken()}\"";

    string EnsureToken()
    {
        var token = Load().Token;
        if (!string.IsNullOrEmpty(token))
            return token;
        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        Update(s => s.Token = token);
        return token;
    }

    /// <summary>Servers default to allowed when tagged Development or Staging and not allowed otherwise, until the user decides.</summary>
    public bool IsAllowed(ConnectionProfile profile) =>
        Load().Allow.TryGetValue(profile.Id.ToString(), out var chosen)
            ? chosen
            : profile.Environment is ServerEnvironment.Development or ServerEnvironment.Staging;

    public void SetAllowed(ConnectionProfile profile, bool allowed)
    {
        Update(s => s.Allow[profile.Id.ToString()] = allowed);
        Changed?.Invoke();
    }

    public void SetEnabled(bool enabled)
    {
        Update(s => s.Enabled = enabled);
        if (enabled)
            Start();
        else
            Stop();
    }

    /// <summary>Starts the server when the user has switched it on. Does nothing when it is off or already running.</summary>
    public void Start()
    {
        if (!IsEnabled || IsListening)
            return;
        var tools = new SqlStudioTools(this, entry => _post(() => Record(entry)),
            (profile, database, sql) => _post(() => QueryRan?.Invoke(profile, database, sql)), _notes);
        _server = new McpHttpServer(new McpDispatcher(tools.All, "sqlstudio", "1.0"), Port, EnsureToken());
        try
        {
            _server.Start();
            Error = null;
        }
        catch (HttpListenerException ex)
        {
            _server.Dispose();
            _server = null;
            Error = $"Could not listen on port {Port}: {ex.Message}. Another program may be using it.";
        }
        Changed?.Invoke();
    }

    public void Stop()
    {
        _server?.Dispose();
        _server = null;
        Error = null;
        Changed?.Invoke();
    }

    void Record(McpActivity entry)
    {
        Activity.Insert(0, entry);
        while (Activity.Count > MaxActivity)
            Activity.RemoveAt(Activity.Count - 1);
    }

    IReadOnlyList<ConnectionProfile> IMcpDatabases.Allowed() => _profiles().Where(IsAllowed).ToList();

    Task<SqlConnection> IMcpDatabases.OpenAsync(ConnectionProfile profile, string? database, CancellationToken ct) =>
        ServerSession.OpenAsync(profile, _credentials, database, ct);

    public void Dispose() => Stop();
}
