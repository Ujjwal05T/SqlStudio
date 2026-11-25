using System.Collections.Concurrent;

namespace SqlStudio.Services.Company;

/// <summary>Managed ids go to memory only, so a company password can never reach the platform store.</summary>
public sealed class RoutingCredentialStore(ICredentialStore platform, InMemoryCredentialStore memory) : ICredentialStore, IDirectConnectionGate
{
    /// <summary>Whether personal (non-company) connections may be opened right now. Closed until the app wires it to the company session.</summary>
    public Func<bool> DirectUsable { get; set; } = () => false;

    public bool DirectConnectionsUsable => DirectUsable();

    readonly ConcurrentDictionary<Guid, byte> _managed = new();

    public void RegisterManaged(Guid id) => _managed[id] = 0;

    public void UnregisterAll() => _managed.Clear();

    ICredentialStore For(Guid id) => _managed.ContainsKey(id) ? memory : platform;

    public string? Get(Guid profileId) => For(profileId).Get(profileId);

    public void Set(Guid profileId, string password) => For(profileId).Set(profileId, password);

    public void Delete(Guid profileId) => For(profileId).Delete(profileId);
}
