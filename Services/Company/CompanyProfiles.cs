using SqlStudio.Models;
using SqlStudio.Services.Company.Api;

namespace SqlStudio.Services.Company;

/// <summary>What changed between the profiles we hold and a new snapshot from the backend.</summary>
public sealed record SnapshotDiff(
    IReadOnlyList<CompanyServerDto> Added,
    IReadOnlyList<(ConnectionProfile Profile, CompanyServerDto Dto)> PermissionsChanged,
    IReadOnlyList<(ConnectionProfile Profile, CompanyServerDto Dto)> ConnectionChanged,
    IReadOnlyList<ConnectionProfile> Removed);

/// <summary>Builds managed profiles from a snapshot and works out what changed between two snapshots.</summary>
public static class CompanyProfiles
{
    public static ConnectionProfile ToProfile(CompanyServerDto dto)
    {
        var profile = new ConnectionProfile { Id = dto.Id, Auth = AuthMode.SqlLogin };
        CopyConnection(profile, dto);
        profile.DefaultDatabase = dto.Databases.Select(d => d.Name).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        profile.Managed = new ManagedServerAccess(dto.Id, PermissionMap(dto));
        return profile;
    }

    /// <summary>Copies the connection fields (not the password, not the permissions) from the backend's view.</summary>
    public static void CopyConnection(ConnectionProfile profile, CompanyServerDto dto)
    {
        profile.Name = dto.Name;
        profile.Server = dto.Host;
        profile.Username = dto.Login;
        profile.Encrypt = dto.Encrypt;
        profile.TrustServerCertificate = dto.TrustServerCertificate;
        profile.Environment = ParseEnvironment(dto.Environment);
    }

    public static IReadOnlyDictionary<string, Permission> PermissionMap(CompanyServerDto dto)
    {
        var map = new Dictionary<string, Permission>(StringComparer.OrdinalIgnoreCase);
        foreach (var db in dto.Databases)
            map[db.Name] = map.GetValueOrDefault(db.Name) | PermissionNames.Parse(db.Permissions);
        return map;
    }

    public static ServerEnvironment ParseEnvironment(string? text) =>
        Enum.TryParse<ServerEnvironment>(text?.Trim(), ignoreCase: true, out var e) && Enum.IsDefined(e) ? e : ServerEnvironment.None;

    public static SnapshotDiff Diff(IReadOnlyList<ConnectionProfile> current, ConnectionsResponse next)
    {
        var byId = current.ToDictionary(p => p.Id);
        var nextIds = next.Servers.Select(s => s.Id).ToHashSet();
        var added = new List<CompanyServerDto>();
        var perms = new List<(ConnectionProfile, CompanyServerDto)>();
        var conn = new List<(ConnectionProfile, CompanyServerDto)>();

        foreach (var dto in next.Servers)
        {
            if (!byId.TryGetValue(dto.Id, out var p)) { added.Add(dto); continue; }
            if (p.Managed is { } m && !SamePermissions(m, PermissionMap(dto))) perms.Add((p, dto));
            if (ConnectionDiffers(p, dto)) conn.Add((p, dto));
        }
        var removed = current.Where(p => !nextIds.Contains(p.Id)).ToList();
        return new SnapshotDiff(added, perms, conn, removed);
    }

    static bool SamePermissions(ManagedServerAccess access, IReadOnlyDictionary<string, Permission> map) =>
        access.Databases.Count == map.Count && map.All(kv => access.PermissionsFor(kv.Key) == kv.Value);

    static bool ConnectionDiffers(ConnectionProfile p, CompanyServerDto dto) =>
        p.Name != dto.Name || p.Server != dto.Host || p.Username != dto.Login
        || p.Encrypt != dto.Encrypt || p.TrustServerCertificate != dto.TrustServerCertificate
        || p.Environment != ParseEnvironment(dto.Environment);
}
