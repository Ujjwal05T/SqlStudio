using Microsoft.Data.SqlClient;
using SqlStudio.Models;
using SqlStudio.Services.Completion;

namespace SqlStudio.Services;

/// <summary>
/// One long-lived connection to a server for browsing. SqlConnection allows a single command at a time,
/// so work is serialised through a gate. The connection is opened on first use and reopened if it drops.
/// </summary>
public sealed class ServerSession : IDisposable
{
    readonly ICredentialStore _credentials;
    readonly SemaphoreSlim _gate = new(1, 1);
    SqlConnection? _connection;

    public ServerSession(ConnectionProfile profile, ICredentialStore credentials)
    {
        Profile = profile;
        _credentials = credentials;
        Schema = new SchemaCache(this);
    }

    /// <summary>What autocomplete knows about this server's tables and columns.</summary>
    public SchemaCache Schema { get; }

    public ConnectionProfile Profile { get; }

    internal ICredentialStore Credentials => _credentials;

    /// <summary>Whether the browsing connection is open right now.</summary>
    public bool IsConnected => _connection is { State: System.Data.ConnectionState.Open };

    /// <summary>Raised after the connection opens or is closed on purpose, so the sidebar can show it.</summary>
    public event Action? ConnectionChanged;

    public static async Task<SqlConnection> OpenAsync(
        ConnectionProfile profile, ICredentialStore credentials, string? database, CancellationToken ct = default)
    {
        DirectConnectionGate.Require(profile, credentials); // before anything else: no password lookup, no connection attempt
        string? password = null;
        if (profile.NeedsPassword)
        {
            password = await Task.Run(() => credentials.Get(profile.Id), ct);
            if (password is null)
                throw new InvalidOperationException(profile.IsManaged
                    ? "No password for this company server right now. Sign in to company connections again."
                    : "No saved password for this server. Edit the connection and enter it again.");
        }

        var connection = new SqlConnection(ConnectionFactory.BuildConnectionString(profile, password, database));
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A company login that may use one database often cannot see it in sys.databases (the server hides databases from logins
    /// without VIEW ANY DATABASE), although it can connect to it. For each granted name the list left out, try to open it:
    /// a database that opens is real and is returned; a granted name that does not exist on the server stays hidden.
    /// </summary>
    public static async Task<List<(string Name, int Id)>> FindUnlistedGrantedAsync(
        ConnectionProfile profile, ICredentialStore credentials, IEnumerable<string> listed)
    {
        var found = new List<(string, int)>();
        if (profile.Managed is not { } access)
            return found;
        var known = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
        foreach (var name in access.Databases.Where(n => !known.Contains(n)))
        {
            try
            {
                await using var connection = await OpenAsync(profile, credentials, name);
                await using var command = new SqlCommand("SELECT DB_ID()", connection);
                var id = await command.ExecuteScalarAsync() is int i ? i : 5;
                found.Add((name, Math.Max(id, 5))); // never a system database
            }
            catch (Exception)
            {
                // not there, or not usable: leave it out
            }
        }
        return found;
    }

    public async Task<T> UseAsync<T>(Func<SqlConnection, Task<T>> work)
    {
        await _gate.WaitAsync();
        try
        {
            if (_connection is not { State: System.Data.ConnectionState.Open })
            {
                _connection?.Dispose();
                _connection = await OpenAsync(Profile, _credentials, "master"); // browse from master so no user database is opened
                ConnectionChanged?.Invoke();
            }
            return await work(_connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the browsing connection. It reopens by itself the next time something needs it.</summary>
    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync(); // let any work in progress finish first
        try
        {
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _gate.Release();
        }
        ConnectionChanged?.Invoke();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
