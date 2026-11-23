using SqlStudio.Services.Company.Api;

namespace SqlStudio.Models;

/// <summary>Live permissions for one company server. Shared by every tab, so it must be safe to read while it is being updated.</summary>
public sealed class ManagedServerAccess
{
    // Replaced as a whole, never edited in place, so readers never see a half-updated map.
    IReadOnlyDictionary<string, Permission> _databases;
    volatile string? _revokedReason;
    int _revoked;

    public ManagedServerAccess(Guid serverId, IReadOnlyDictionary<string, Permission> databases)
    {
        ServerId = serverId;
        _databases = Copy(databases);
    }

    public Guid ServerId { get; }

    public bool IsRevoked => Volatile.Read(ref _revoked) != 0;

    public string? RevokedReason => IsRevoked ? _revokedReason : null;

    public IReadOnlyCollection<string> Databases => IsRevoked ? [] : Volatile.Read(ref _databases).Keys.ToList();

    public event Action? Changed;

    public Permission PermissionsFor(string? database)
    {
        if (database is null || IsRevoked)
            return Permission.None;
        return Volatile.Read(ref _databases).GetValueOrDefault(database);
    }

    public bool CanUse(string? database) => PermissionsFor(database) != Permission.None;

    public bool Has(string? database, Permission permission) =>
        permission != Permission.None && (PermissionsFor(database) & permission) == permission;

    public void Update(IReadOnlyDictionary<string, Permission> databases)
    {
        Volatile.Write(ref _databases, Copy(databases));
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

    static Dictionary<string, Permission> Copy(IReadOnlyDictionary<string, Permission> source) =>
        new(source, StringComparer.OrdinalIgnoreCase);
}
