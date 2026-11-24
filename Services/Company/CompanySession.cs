using SqlStudio.Models;
using SqlStudio.Services.Company.Api;

namespace SqlStudio.Services.Company;

public enum CompanyState { SignedOut, SignedIn, Offline, Locked }

/// <summary>
/// The company sign-in. Tokens and server passwords live in memory only; settings keep just the email.
/// Auto refresh, offline grace and a saved sign-in are added later and call <see cref="ApplySnapshot"/>.
/// </summary>
public sealed class CompanySession(
    CompanyApiClient api,
    RoutingCredentialStore credentials,
    InMemoryCredentialStore secrets,
    SettingsStore settings,
    TimeProvider clock,
    Action<Action> post) : IDisposable
{
    readonly object _gate = new();
    List<ConnectionProfile> _profiles = [];
    string? _accessToken;
    string? _refreshToken;
    string _lockedReason = "";
    CompanyAudit? _audit;

    /// <summary>Receives connect and blocked events from managed query tabs and reports them to the company server.</summary>
    public CompanyAudit Audit { get { lock (_gate) return _audit ??= new CompanyAudit(SendAuditAsync, clock); } }

    public CompanyState State { get; private set; } = CompanyState.SignedOut;
    public UserDto? User { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }

    public IReadOnlyList<ConnectionProfile> Profiles { get { lock (_gate) return _profiles.ToList(); } }

    public string StatusText => State switch
    {
        CompanyState.SignedIn => $"Signed in as {User?.Email}",
        CompanyState.Offline => $"Offline · last checked {Math.Max(0, (int)(clock.GetUtcNow() - (LastChecked ?? clock.GetUtcNow())).TotalMinutes)} min ago",
        CompanyState.Locked => $"Locked: {_lockedReason}",
        _ => "",
    };

    public event Action? Changed;
    public event Action<string>? SignedOut;
    /// <summary>Raised when the backend changed how to reach a server (host, login, password, ...), so open connections should be redone.</summary>
    public event Action<IReadOnlyList<ConnectionProfile>>? ConnectionChanged;

    Uri Endpoint => CompanyEndpoint.Resolve(settings.Load().Company);

    public async Task SignInAsync(string email, string password, CancellationToken ct)
    {
        var baseUrl = Endpoint;
        var tokens = await api.LoginAsync(baseUrl, email, password, ct).ConfigureAwait(false);
        ConnectionsResponse snapshot;
        try { snapshot = await api.GetConnectionsAsync(baseUrl, tokens.AccessToken, ct).ConfigureAwait(false); }
        catch
        {
            // Sign-in did not finish: end the server-side session and keep nothing.
            try { await api.LogoutAsync(baseUrl, tokens.RefreshToken, CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }

        lock (_gate)
        {
            _accessToken = tokens.AccessToken;
            _refreshToken = tokens.RefreshToken;
            User = tokens.User;
            _profiles = [];
            foreach (var dto in snapshot.Servers) AddServer(dto);
            State = CompanyState.SignedIn;
            LastChecked = clock.GetUtcNow();
        }
        Audit.Start();

        var s = settings.Load();
        s.Company.LastEmail = email;
        settings.Save(s);
        Raise(Changed);
    }

    public async Task SignOutAsync(string? reason = null)
    {
        string? refresh;
        lock (_gate) refresh = _refreshToken;
        if (refresh is not null)
        {
            // Report what is queued while the access token still works. Best effort, and bounded like logout.
            try
            {
                using var auditCts = new CancellationTokenSource(CompanyApiClient.Timeout);
                await Audit.FlushAsync(auditCts.Token).ConfigureAwait(false);
            }
            catch { }

            try
            {
                using var cts = new CancellationTokenSource(CompanyApiClient.Timeout);
                await api.LogoutAsync(Endpoint, refresh, cts.Token).ConfigureAwait(false);
            }
            catch { } // best effort: local sign-out must always finish
        }

        Audit.Stop();
        Audit.Clear(); // the next person to sign in must not inherit these

        lock (_gate)
        {
            _accessToken = null;
            _refreshToken = null;
            User = null;
            LastChecked = null;
            secrets.Clear();
            credentials.UnregisterAll();
            foreach (var p in _profiles) p.Managed?.Revoke(reason ?? "You're signed out of company connections.");
            _profiles = [];
            State = CompanyState.SignedOut;
        }
        var text = reason ?? "You signed out.";
        post(() => { Changed?.Invoke(); SignedOut?.Invoke(text); });
    }

    /// <summary>Brings the held profiles in line with a new snapshot. Existing access objects are updated in place so open tabs see it.</summary>
    public void ApplySnapshot(ConnectionsResponse snapshot)
    {
        var connectionChanged = new List<ConnectionProfile>();
        lock (_gate)
        {
            var diff = CompanyProfiles.Diff(_profiles, snapshot);
            var dtos = snapshot.Servers.ToDictionary(s => s.Id);

            foreach (var (profile, dto) in diff.PermissionsChanged)
                profile.Managed!.Update(CompanyProfiles.PermissionMap(dto));

            foreach (var (profile, dto) in diff.ConnectionChanged)
            {
                CompanyProfiles.CopyConnection(profile, dto);
                connectionChanged.Add(profile);
            }
            // A new password also means open connections are stale.
            foreach (var p in _profiles)
                if (dtos.TryGetValue(p.Id, out var dto) && secrets.Get(p.Id) != dto.Password)
                {
                    secrets.Set(p.Id, dto.Password);
                    if (!connectionChanged.Contains(p)) connectionChanged.Add(p);
                }

            foreach (var p in diff.Removed)
            {
                p.Managed?.Revoke("This server is no longer available to you.");
                secrets.Delete(p.Id);
                _profiles.Remove(p);
            }
            foreach (var dto in diff.Added) AddServer(dto);

            LastChecked = clock.GetUtcNow();
        }
        Raise(Changed);
        if (connectionChanged.Count > 0) post(() => ConnectionChanged?.Invoke(connectionChanged));
    }

    /// <summary>Sends one audit batch with the current access token. Throws when signed out so the events stay queued.</summary>
    Task SendAuditAsync(AuditBatchRequest batch, CancellationToken ct)
    {
        string? token;
        lock (_gate) token = _accessToken;
        if (token is null) throw new InvalidOperationException("Not signed in.");
        return api.PostAuditAsync(Endpoint, token, batch, ct);
    }

    void AddServer(CompanyServerDto dto)
    {
        credentials.RegisterManaged(dto.Id);
        secrets.Set(dto.Id, dto.Password);
        _profiles.Add(CompanyProfiles.ToProfile(dto));
    }

    void Raise(Action? e) => post(() => e?.Invoke());

    public void Dispose() => _audit?.Dispose();
}
