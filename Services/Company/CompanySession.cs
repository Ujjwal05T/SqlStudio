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
    Action<Action> post,
    ISecureTokenStore? tokenStore = null) : IDisposable
{
    readonly object _gate = new();
    readonly ISecureTokenStore _tokenStore = tokenStore ?? new MemoryOnlyTokenStore();
    bool _resumedOffline; // resumed without reaching the server: the audit starts once a refresh gets through
    List<ConnectionProfile> _profiles = [];
    string? _accessToken;
    string? _refreshToken;
    string _lockedReason = "";
    CompanyAudit? _audit;
    DateTimeOffset _accessExpiresAt;
    int _refreshAfterSeconds = 180;
    int _graceSeconds = 900;
    long _lastSuccessStamp;
    int _epoch; // bumped on sign-in and sign-out: a refresh that started earlier must not touch the new state
    readonly SemaphoreSlim _refreshGate = new(1, 1);
    CancellationTokenSource? _loopCts;

    public const string LockReason = "Company access couldn't be checked for 15 minutes. It will come back when the company server can be reached.";
    static readonly TimeSpan OfflineRetry = TimeSpan.FromSeconds(60);

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
    /// <summary>Raised after a refresh brought a different snapshot, with what was added, removed or changed.</summary>
    public event Action<SnapshotDiff>? SnapshotChanged;
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

        Establish(tokens, snapshot);

        var s = settings.Load();
        s.Company.LastEmail = email;
        settings.Save(s);
        Raise(Changed);
    }

    /// <summary>Takes a finished sign-in (or resume) as the current state and keeps its refresh token in the secure store.</summary>
    void Establish(TokenResponse tokens, ConnectionsResponse snapshot)
    {
        lock (_gate)
        {
            _epoch++;
            _accessToken = tokens.AccessToken;
            _refreshToken = tokens.RefreshToken;
            _accessExpiresAt = tokens.AccessTokenExpiresAt;
            _refreshAfterSeconds = snapshot.RefreshAfterSeconds > 0 ? snapshot.RefreshAfterSeconds : 180;
            _graceSeconds = snapshot.OfflineGraceSeconds > 0 ? snapshot.OfflineGraceSeconds : 900;
            _lastSuccessStamp = clock.GetTimestamp();
            _lockedReason = "";
            _resumedOffline = false;
            User = tokens.User;
            _profiles = [];
            foreach (var dto in snapshot.Servers) AddServer(dto);
            State = CompanyState.SignedIn;
            LastChecked = clock.GetUtcNow();
            SaveToken(tokens.RefreshToken);
        }
        Audit.Start();
        StartAutoRefresh();
    }

    // Called with _gate held, so a save can never land after a sign-out's delete. A broken store must not break the sign-in.
    void SaveToken(string token) { try { _tokenStore.Save(token); } catch { } }
    void DeleteToken() { try { _tokenStore.Delete(); } catch { } }

    /// <summary>
    /// Startup: signs in again from the refresh token kept in the secure store, without asking for a password.
    /// True when signed in. False with no saved token, when it was refused (the token is deleted; 403 and 423 also raise
    /// <see cref="SignedOut"/>), or when the server can't be reached (the token is kept, State is Offline and the usual 60 s retry runs).
    /// </summary>
    public async Task<bool> TryResumeAsync(CancellationToken ct)
    {
        string? saved;
        try { saved = _tokenStore.Load(); } catch { saved = null; }
        if (string.IsNullOrEmpty(saved)) return false;
        lock (_gate) { if (State != CompanyState.SignedOut) return false; }

        var baseUrl = Endpoint;
        try
        {
            var tokens = await api.RefreshAsync(baseUrl, saved, ct).ConfigureAwait(false);
            // Keep the rotated token at once: the old one is spent even if the connections call below fails.
            lock (_gate) SaveToken(tokens.RefreshToken);
            ConnectionsResponse snapshot;
            try { snapshot = await api.GetConnectionsAsync(baseUrl, tokens.AccessToken, ct).ConfigureAwait(false); }
            catch (CompanyApiException ex) when (ex.Kind is CompanyApiError.Unreachable or CompanyApiError.BadResponse or CompanyApiError.RateLimited)
            {
                GoOffline(tokens.RefreshToken, tokens.User);
                return false;
            }
            Establish(tokens, snapshot);
            Raise(Changed);
            return true;
        }
        catch (CompanyApiException ex)
        {
            string? reason = ex.Kind switch
            {
                CompanyApiError.UserDisabled => "Your company account was disabled.",
                CompanyApiError.LockedOut => "Your company account is locked. Try again later.",
                _ => null,
            };
            if (ex.Kind is CompanyApiError.Unauthorized or CompanyApiError.InvalidCredentials)
            {
                lock (_gate) DeleteToken();
                return false;
            }
            if (reason is not null)
            {
                lock (_gate) DeleteToken();
                post(() => { Changed?.Invoke(); SignedOut?.Invoke(reason); });
                return false;
            }
            GoOffline(saved, null);
            return false;
        }
    }

    void GoOffline(string refreshToken, UserDto? user)
    {
        lock (_gate)
        {
            _epoch++;
            _refreshToken = refreshToken;
            _accessToken = null;
            _accessExpiresAt = default; // the next round renews first
            _lastSuccessStamp = clock.GetTimestamp();
            _resumedOffline = true;
            User = user;
            LastChecked = clock.GetUtcNow();
            State = CompanyState.Offline;
        }
        StartAutoRefresh();
        Raise(Changed);
    }

    public async Task SignOutAsync(string? reason = null)
    {
        StopAutoRefresh();
        string? access, refresh;
        List<AuditEventDto> pending;
        var audit = Audit;
        audit.Stop(); // no timer flush may start from here on
        // Everything that ends access happens first, under the lock, before any network call.
        lock (_gate)
        {
            access = _accessToken;
            refresh = _refreshToken;
            State = CompanyState.SignedOut;
            _epoch++;
            _accessToken = null;
            _refreshToken = null;
            _resumedOffline = false;
            foreach (var p in _profiles) p.Managed?.Revoke(reason ?? "You're signed out of company connections.");
            _profiles = [];
            secrets.Clear();
            credentials.UnregisterAll();
            pending = audit.Drain(); // the next person to sign in must not inherit these
            DeleteToken();
            User = null;
            LastChecked = null;
        }
        var text = reason ?? "You signed out.";
        post(() => { Changed?.Invoke(); SignedOut?.Invoke(text); });

        // Best effort with the captured tokens: report what was queued, then end the server-side session.
        if (access is not null && pending.Count > 0)
        {
            try
            {
                using var auditCts = new CancellationTokenSource(CompanyApiClient.Timeout);
                for (var i = 0; i < pending.Count; i += CompanyAudit.MaxEventsPerRequest)
                    await api.PostAuditAsync(Endpoint, access, new AuditBatchRequest(pending.Skip(i).Take(CompanyAudit.MaxEventsPerRequest).ToList()), auditCts.Token).ConfigureAwait(false);
            }
            catch { }
        }
        if (refresh is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(CompanyApiClient.Timeout);
                await api.LogoutAsync(Endpoint, refresh, cts.Token).ConfigureAwait(false);
            }
            catch { } // best effort: local sign-out must always finish
        }
    }

    /// <summary>Brings the held profiles in line with a new snapshot. Existing access objects are updated in place so open tabs see it. Does nothing once signed out.</summary>
    public void ApplySnapshot(ConnectionsResponse snapshot) => TryApply(snapshot, null);

    bool TryApply(ConnectionsResponse snapshot, int? epoch)
    {
        var connectionChanged = new List<ConnectionProfile>();
        SnapshotDiff diff;
        lock (_gate)
        {
            // A sign-out that raced this update wins: nothing may come back after it.
            if (State == CompanyState.SignedOut || (epoch is { } e && e != _epoch)) return false;

            if (State == CompanyState.Locked)
                foreach (var p in _profiles) p.Managed?.Restore();
            State = CompanyState.SignedIn;
            _lockedReason = "";

            diff = CompanyProfiles.Diff(_profiles, snapshot);
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
            _lastSuccessStamp = clock.GetTimestamp();
            if (snapshot.RefreshAfterSeconds > 0) _refreshAfterSeconds = snapshot.RefreshAfterSeconds;
            if (snapshot.OfflineGraceSeconds > 0) _graceSeconds = snapshot.OfflineGraceSeconds;
        }
        Raise(Changed);
        post(() => SnapshotChanged?.Invoke(diff));
        if (connectionChanged.Count > 0) post(() => ConnectionChanged?.Invoke(connectionChanged));
        return true;
    }

    /// <summary>
    /// One refresh round: renews the access token when it is about to run out, fetches the connections and applies them.
    /// A dead sign-in (401, 403, 423) signs out; a network failure keeps the last snapshot and later locks it. Overlapping calls wait.
    /// </summary>
    public async Task RefreshNowAsync(CancellationToken ct)
    {
        if (State == CompanyState.SignedOut) return;
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int epoch;
            bool renew;
            lock (_gate)
            {
                if (State == CompanyState.SignedOut || _refreshToken is null) return;
                epoch = _epoch;
                renew = _accessExpiresAt - clock.GetUtcNow() <= TimeSpan.FromSeconds(_refreshAfterSeconds + 30);
            }

            try
            {
                if (renew) await RenewAsync(epoch, ct).ConfigureAwait(false);
                ConnectionsResponse snapshot;
                try { snapshot = await api.GetConnectionsAsync(Endpoint, CurrentToken(epoch), ct).ConfigureAwait(false); }
                catch (CompanyApiException ex) when (ex.Kind == CompanyApiError.Unauthorized && !renew)
                {
                    // The access token was refused early (clock difference): get a new one once and ask again.
                    await RenewAsync(epoch, ct).ConfigureAwait(false);
                    snapshot = await api.GetConnectionsAsync(Endpoint, CurrentToken(epoch), ct).ConfigureAwait(false);
                }
                if (TryApply(snapshot, epoch))
                {
                    bool start;
                    lock (_gate) { start = _resumedOffline; _resumedOffline = false; }
                    if (start) Audit.Start();
                }
            }
            catch (CompanyApiException ex)
            {
                await HandleFailureAsync(ex, epoch).ConfigureAwait(false);
            }
        }
        finally { _refreshGate.Release(); }
    }

    string CurrentToken(int epoch)
    {
        lock (_gate) return epoch == _epoch && _accessToken is { } t ? t : throw new OperationCanceledException();
    }

    async Task RenewAsync(int epoch, CancellationToken ct)
    {
        string refresh;
        lock (_gate) refresh = epoch == _epoch && _refreshToken is { } r ? r : throw new OperationCanceledException();
        var tokens = await api.RefreshAsync(Endpoint, refresh, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (epoch != _epoch) throw new OperationCanceledException(); // signed out meanwhile: drop the new tokens
            _accessToken = tokens.AccessToken;
            _refreshToken = tokens.RefreshToken;
            _accessExpiresAt = tokens.AccessTokenExpiresAt;
            User ??= tokens.User;
            SaveToken(tokens.RefreshToken);
        }
    }

    async Task HandleFailureAsync(CompanyApiException ex, int epoch)
    {
        lock (_gate) { if (epoch != _epoch || State == CompanyState.SignedOut) return; }
        switch (ex.Kind)
        {
            case CompanyApiError.Unauthorized:
                await SignOutAsync("Your company session ended. Sign in again.").ConfigureAwait(false);
                return;
            case CompanyApiError.UserDisabled:
                await SignOutAsync("Your company account was disabled.").ConfigureAwait(false);
                return;
            case CompanyApiError.LockedOut:
                await SignOutAsync("Your company account is locked. Try again later.").ConfigureAwait(false);
                return;
        }

        // Network trouble (or an answer we can't use): keep the last snapshot until the grace runs out.
        lock (_gate)
        {
            if (epoch != _epoch || State == CompanyState.SignedOut) return;
            if (clock.GetElapsedTime(_lastSuccessStamp) >= TimeSpan.FromSeconds(_graceSeconds))
            {
                if (State != CompanyState.Locked)
                {
                    State = CompanyState.Locked;
                    _lockedReason = LockReason;
                    foreach (var p in _profiles) p.Managed?.Revoke(LockReason);
                }
            }
            else State = CompanyState.Offline;
        }
        Raise(Changed);
    }

    /// <summary>Starts the background refresh: every <c>refreshAfterSeconds</c> (180), every 60 s while offline.</summary>
    public void StartAutoRefresh()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_loopCts is not null) return;
            _loopCts = new CancellationTokenSource();
            token = _loopCts.Token;
        }
        _ = Task.Run(() => LoopAsync(token));
    }

    public void StopAutoRefresh()
    {
        CancellationTokenSource? cts;
        lock (_gate) { cts = _loopCts; _loopCts = null; }
        try { cts?.Cancel(); } catch { }
    }

    TimeSpan Period => State is CompanyState.Offline or CompanyState.Locked ? OfflineRetry : TimeSpan.FromSeconds(_refreshAfterSeconds);

    async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(Period, clock);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (State == CompanyState.SignedOut) continue;
                try { await RefreshNowAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (OperationCanceledException) { } // the sign-in changed under this round
                catch { } // a refresh must never take the app down; the next tick tries again
                timer.Period = Period;
            }
        }
        catch (OperationCanceledException) { }
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

    public void Dispose()
    {
        StopAutoRefresh();
        _audit?.Dispose();
    }
}
