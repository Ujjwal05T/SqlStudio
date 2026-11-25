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
    public static ConnectionProfile ToProfile(CompanyServerDto dto, int signIn = 0)
    {
        var profile = new ConnectionProfile { Id = dto.Id, Auth = AuthMode.SqlLogin };
        CopyConnection(profile, dto);
        var map = PermissionMap(dto);
        // The first database the user can actually use; a name with no permissions would start the tab where every run is blocked.
        profile.DefaultDatabase = map.Where(kv => kv.Value != Permission.None).Select(kv => kv.Key)
            .Order(StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        profile.Managed = new ManagedServerAccess(dto.Id, map, signIn);
        return profile;
    }

    /// <summary>
    /// Checks a sign-in answer before anything is stored: tokens, servers, databases and the fields later code relies on.
    /// Throws <see cref="InvalidDataException"/> when something is missing, so a half-formed reply is never half-applied.
    /// </summary>
    public static void Validate(TokenResponse? tokens, ConnectionsResponse? snapshot)
    {
        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken) || string.IsNullOrEmpty(tokens.RefreshToken) || tokens.User is null)
            throw new InvalidDataException("Incomplete sign-in reply.");
        Validate(snapshot);
    }

    public static void Validate(ConnectionsResponse? snapshot)
    {
        if (snapshot?.Servers is null) throw new InvalidDataException("Incomplete connections reply.");
        var ids = new HashSet<Guid>();
        foreach (var s in snapshot.Servers)
        {
            if (s is null || s.Name is null || s.Host is null || s.Login is null || s.Password is null || s.Databases is null || !ids.Add(s.Id))
                throw new InvalidDataException("Incomplete connections reply.");
            foreach (var d in s.Databases)
                if (d?.Name is null) throw new InvalidDataException("Incomplete connections reply.");
        }
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
        var map = new Dictionary<string, Permission>(StringComparer.Ordinal); // names that differ only by case stay separate
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
