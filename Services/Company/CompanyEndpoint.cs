using System.Reflection;

namespace SqlStudio.Services.Company;

/// <summary>Where the company backend lives: the address built into this release, unless settings.json hides an override.</summary>
public static class CompanyEndpoint
{
    /// <summary>The build-time address (MSBuild property CompanyBackendUrl, stored as assembly metadata).</summary>
    public static string BuiltInUrl { get; } = typeof(CompanyEndpoint).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "CompanyBackendUrl")?.Value?.Trim() ?? "";

    /// <summary>The override wins when it is not empty. Either way the address must be https (or loopback http).</summary>
    public static Uri Resolve(CompanySettings settings, string? builtIn = null)
    {
        var text = string.IsNullOrWhiteSpace(settings.BackendUrlOverride) ? builtIn ?? BuiltInUrl : settings.BackendUrlOverride.Trim();
        return CompanyApiClient.ValidateBaseUrl(text);
    }
}
