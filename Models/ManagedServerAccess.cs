using SqlStudio.Services.Company.Api;

namespace SqlStudio.Models;

/// <summary>Live permissions for one company server. Shared by every tab, so it must be safe to read while it is being updated.</summary>
public sealed class ManagedServerAccess
{
    // Replaced as a whole, never edited in place, so readers never see a half-updated map.
    Table _databases;

    /// <summary>
    /// The granted names exactly as given, plus a case-insensitive view (SQL Server's usual collation). When two granted
    /// names differ only by case, the looser view cannot say which one is meant, so only an exact-case name matches.
    /// </summary>
    sealed class Table
    {
        readonly Dictionary<string, Permission> _exact;
        readonly Dictionary<string, Permission> _loose = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _ambiguous = new(StringComparer.OrdinalIgnoreCase);

        public Table(IReadOnlyDictionary<string, Permission> source)
        {
            _exact = new Dictionary<string, Permission>(StringComparer.Ordinal);
            foreach (var (name, p) in source) _exact[name] = _exact.GetValueOrDefault(name) | p;
            foreach (var (name, p) in _exact)
                if (!_loose.TryAdd(name, p)) _ambiguous.Add(name);
        }

        public IReadOnlyCollection<string> Names => _exact.Keys;

        public Permission For(string database)
        {
            if (_exact.TryGetValue(database, out var p)) return p;
            if (_ambiguous.Contains(database)) return Permission.None;
            return _loose.GetValueOrDefault(database);
        }
    }
    volatile string? _revokedReason;
    int _revoked;

    public ManagedServerAccess(Guid serverId, IReadOnlyDictionary<string, Permission> databases, int signIn = 0)
    {
        ServerId = serverId;
        SignIn = signIn;
        _databases = new Table(databases);
    }

    public Guid ServerId { get; }

    /// <summary>Which sign-in this access belongs to (the session's epoch when it was made), so late reports from an earlier one can be told apart.</summary>
    public int SignIn { get; }

    public bool IsRevoked => Volatile.Read(ref _revoked) != 0;

    public string? RevokedReason => IsRevoked ? _revokedReason : null;

    public IReadOnlyCollection<string> Databases => IsRevoked ? [] : Volatile.Read(ref _databases).Names.ToList();

    public event Action? Changed;

    public Permission PermissionsFor(string? database)
    {
        if (database is null || IsRevoked)
            return Permission.None;
        return Volatile.Read(ref _databases).For(database);
    }

    public bool CanUse(string? database) => PermissionsFor(database) != Permission.None;

    public bool Has(string? database, Permission permission) =>
        permission != Permission.None && (PermissionsFor(database) & permission) == permission;

    public void Update(IReadOnlyDictionary<string, Permission> databases)
    {
        Volatile.Write(ref _databases, new Table(databases));
        Changed?.Invoke();
    }

    public void Revoke(string reason)
    {
        _revokedReason = reason;
        Volatile.Write(ref _revoked, 1);
        Changed?.Invoke();
    }

    public void Restore()
    {
        Volatile.Write(ref _revoked, 0);
        _revokedReason = null;
        Changed?.Invoke();
    }
}
