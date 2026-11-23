using System.Collections.Concurrent;

namespace SqlStudio.Services.Company;

/// <summary>Holds company server passwords for the life of the sign-in. Never touches disk.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    readonly ConcurrentDictionary<Guid, string> _passwords = new();

    public string? Get(Guid profileId) => _passwords.GetValueOrDefault(profileId);

    public void Set(Guid profileId, string password) => _passwords[profileId] = password;

    public void Delete(Guid profileId) => _passwords.TryRemove(profileId, out _);

    public void Clear() => _passwords.Clear();
}
