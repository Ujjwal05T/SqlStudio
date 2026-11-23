using System.Text.Json.Serialization;

namespace SqlStudio.Models;

public enum ServerEnvironment
{
    None,
    Development,
    Staging,
    Production,
}

public enum AuthMode
{
    SqlLogin,
    EntraInteractive,
}

/// <summary>
/// A saved server. The password is never part of this object's JSON; it lives in the Keychain.
/// </summary>
public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public AuthMode Auth { get; set; } = AuthMode.SqlLogin;
    public string Username { get; set; } = "";
    public string? DefaultDatabase { get; set; }
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }

    /// <summary>Colour-codes this server's tabs so dev and production are hard to mix up.</summary>
    public ServerEnvironment Environment { get; set; }

    [JsonIgnore]
    public bool HasEnvironment => Environment != ServerEnvironment.None;

    [JsonIgnore]
    public string EnvironmentShort => Environment switch
    {
        ServerEnvironment.Development => "DEV",
        ServerEnvironment.Staging => "STAGE",
        ServerEnvironment.Production => "PROD",
        _ => "",
    };

    [JsonIgnore]
    public string EnvironmentLabel => Environment == ServerEnvironment.None ? "" : Environment.ToString().ToUpperInvariant();

    [JsonIgnore]
    public bool NeedsPassword => Auth == AuthMode.SqlLogin;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Server : Name;

    /// <summary>Set for company servers. Memory only, and shared by clones so every tab sees permission changes.</summary>
    [JsonIgnore]
    public ManagedServerAccess? Managed { get; set; }

    [JsonIgnore]
    public bool IsManaged => Managed is not null;

    public ConnectionProfile Clone() => (ConnectionProfile)MemberwiseClone();
}
