namespace SqlStudio.Services.Company;

/// <summary>Keeps the token in memory only, so it is gone when the app closes. Used where there is no secure store, and as a test spy.</summary>
public sealed class MemoryOnlyTokenStore : ISecureTokenStore
{
    readonly object _gate = new();
    string? _token;

    public string? Load() { lock (_gate) return _token; }
    public void Save(string token) { lock (_gate) _token = token; }
    public void Delete() { lock (_gate) _token = null; }
    public override string ToString() => nameof(MemoryOnlyTokenStore);
}
