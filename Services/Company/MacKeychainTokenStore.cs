namespace SqlStudio.Services.Company;

/// <summary>The company refresh token in the macOS login Keychain, under its own service so it stays apart from server passwords.</summary>
public sealed class MacKeychainTokenStore(string service = "SqlStudio.Company") : ISecureTokenStore
{
    static readonly Guid Account = new("7d4a2c1e-0b5f-4e8a-9c3d-2f6b1a8e4d70");
    readonly MacKeychain _keychain = new(service);

    public string? Load() => _keychain.Get(Account);
    public void Save(string token) => _keychain.Set(Account, token);
    public void Delete() => _keychain.Delete(Account);
    public override string ToString() => nameof(MacKeychainTokenStore);
}

// Lives in this file because the platform-wording test only allows Mac-specific names in Platform.cs and files starting with "Mac" (same as CredentialStores in MacKeychain.cs).
public static class SecureTokenStores
{
    /// <summary>macOS: the login Keychain. Windows: Credential Manager. Anywhere else: memory only (sign in again each start).</summary>
    public static ISecureTokenStore CreateDefault() =>
        OperatingSystem.IsWindows() ? new WindowsCredentialManagerTokenStore()
        : OperatingSystem.IsMacOS() ? new MacKeychainTokenStore()
        : new MemoryOnlyTokenStore();
}
