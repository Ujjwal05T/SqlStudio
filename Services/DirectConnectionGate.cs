using SqlStudio.Models;

namespace SqlStudio.Services;

/// <summary>Says whether personal (non-company) connections may be opened right now. The credential store every caller already holds carries it.</summary>
public interface IDirectConnectionGate
{
    bool DirectConnectionsUsable { get; }
}

/// <summary>Thrown instead of a connection attempt when personal connections are locked.</summary>
public sealed class DirectConnectionsLockedException() : InvalidOperationException(DirectConnectionGate.Message);

/// <summary>
/// The one check in front of every personal connection. Company (managed) profiles pass untouched. A store that carries no gate
/// counts as locked, so a code path that forgot to wire one can never connect.
/// </summary>
public static class DirectConnectionGate
{
    public const string Message = "Direct connections need a company sign-in and permission. Sign in, or ask your admin.";

    public const string SignInNote = "Your own SQL Server connections work only while you are signed in here and your admin has allowed them.";

    public static bool IsUsable(ICredentialStore credentials) => (credentials as IDirectConnectionGate)?.DirectConnectionsUsable == true;

    public static bool Allows(ConnectionProfile profile, ICredentialStore credentials) => profile.IsManaged || IsUsable(credentials);

    /// <summary>Throws <see cref="DirectConnectionsLockedException"/> for a personal profile while locked.</summary>
    public static void Require(ConnectionProfile profile, ICredentialStore credentials)
    {
        if (!Allows(profile, credentials))
            throw new DirectConnectionsLockedException();
    }
}
