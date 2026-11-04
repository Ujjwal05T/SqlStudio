using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SqlStudio.Services;

/// <summary>
/// Windows: passwords are encrypted with DPAPI for the current user, so only the same Windows
/// account can decrypt them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore(string? path = null) : EncryptedFileCredentialStore(path)
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SqlStudio");

    protected override string Encrypt(string password) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));

    protected override string? Decrypt(string blob)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null; // written by another account or damaged
        }
    }
}
