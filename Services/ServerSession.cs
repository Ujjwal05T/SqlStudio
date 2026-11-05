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

    /// <summary>Whether the browsing connection is open right now.</summary>
    public bool IsConnected => _connection is { State: System.Data.ConnectionState.Open };

    /// <summary>Raised after the connection opens or is closed on purpose, so the sidebar can show it.</summary>
    public event Action? ConnectionChanged;

    public static async Task<SqlConnection> OpenAsync(
        ConnectionProfile profile, ICredentialStore credentials, string? database, CancellationToken ct = default)
    {
        string? password = null;
        if (profile.NeedsPassword)
        {
            password = await Task.Run(() => credentials.Get(profile.Id), ct);
            if (password is null)
                throw new InvalidOperationException("No saved password for this server. Edit the connection and enter it again.");
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

    public async Task<T> UseAsync<T>(Func<SqlConnection, Task<T>> work)
    {
        await _gate.WaitAsync();
        try
        {
            if (_connection is not { State: System.Data.ConnectionState.Open })
            {
                _connection?.Dispose();
                _connection = await OpenAsync(Profile, _credentials, null);
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
