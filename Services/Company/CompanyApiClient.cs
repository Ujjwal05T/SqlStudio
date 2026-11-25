using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using SqlStudio.Services.Company.Api;

namespace SqlStudio.Services.Company;

public enum CompanyApiError { InvalidCredentials, UserDisabled, LockedOut, Unauthorized, Unreachable, BadResponse, InsecureUrl, RateLimited, AdminAccount }

/// <summary>A failed company call. The message is fixed text, safe to show; it never carries a password, token or server reply.</summary>
public sealed class CompanyApiException(CompanyApiError kind, string message) : Exception(message)
{
    public CompanyApiError Kind { get; } = kind;

    /// <summary>The HTTP status the server answered with, when there was one.</summary>
    public int? StatusCode { get; init; }
}

public sealed class CompanyApiClient(HttpClient http)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>An HttpClient with the 15 second limit this client expects.</summary>
    public static HttpClient CreateHttpClient() => new() { Timeout = Timeout };

    static string ClientName { get; } =
        $"SqlStudio {typeof(CompanyApiClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0"} ({Platform.Current})";

    static string Message(CompanyApiError kind) => kind switch
    {
        CompanyApiError.InvalidCredentials => "That email and password don't match a company account.",
        CompanyApiError.UserDisabled => "Your company account is disabled. Ask your admin.",
        CompanyApiError.LockedOut => "Too many failed attempts. Try again in 15 minutes.",
        CompanyApiError.RateLimited => "Too many attempts. Wait a minute and try again.",
        CompanyApiError.Unauthorized => "Your company sign-in is no longer valid. Sign in again.",
        CompanyApiError.Unreachable => "Can't reach the company server. Check your network and try again.",
        CompanyApiError.InsecureUrl => "The company server address must start with https://.",
        CompanyApiError.AdminAccount => "Admin accounts can't sign in to the desktop app. Use your own non-admin account, or ask your admin for one.",
        _ => "The company server sent a reply this version of SqlStudio doesn't understand.",
    };

    static CompanyApiException Fail(CompanyApiError kind) => new(kind, Message(kind));

    /// <summary>Accepts https, or http only for this computer (localhost, 127.0.0.1, [::1]).</summary>
    public static Uri ValidateBaseUrl(string text)
    {
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri)) throw Fail(CompanyApiError.InsecureUrl);
        var ok = uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)));
        if (!ok) throw Fail(CompanyApiError.InsecureUrl);
        return uri;
    }

    public async Task<TokenResponse> LoginAsync(Uri baseUrl, string email, string password, CancellationToken ct)
    {
        var body = await SendAsync<JsonElement>(baseUrl, HttpMethod.Post, "api/auth/login", null,
            JsonContent.Create(new LoginRequest(email, password, ClientName), options: CompanyJson.Options), CompanyApiError.InvalidCredentials, ct).ConfigureAwait(false);
        // Admins get an email-code challenge instead of tokens. The desktop app never completes that step: admins use the admin site.
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String && status.GetString() == "otp_required")
            throw Fail(CompanyApiError.AdminAccount);
        try { return body.Deserialize<TokenResponse>(CompanyJson.Options) ?? throw Fail(CompanyApiError.BadResponse); }
        catch (JsonException) { throw Fail(CompanyApiError.BadResponse); }
    }

    public Task<TokenResponse> RefreshAsync(Uri baseUrl, string refreshToken, CancellationToken ct) =>
        SendAsync<TokenResponse>(baseUrl, HttpMethod.Post, "api/auth/refresh", null,
            JsonContent.Create(new RefreshRequest(refreshToken), options: CompanyJson.Options), CompanyApiError.Unauthorized, ct);

    public Task LogoutAsync(Uri baseUrl, string refreshToken, CancellationToken ct) =>
        SendAsync<object>(baseUrl, HttpMethod.Post, "api/auth/logout", null,
            JsonContent.Create(new LogoutRequest(refreshToken), options: CompanyJson.Options), CompanyApiError.Unauthorized, ct, readBody: false);

    public Task<ConnectionsResponse> GetConnectionsAsync(Uri baseUrl, string accessToken, CancellationToken ct) =>
        SendAsync<ConnectionsResponse>(baseUrl, HttpMethod.Get, "api/me/connections", accessToken, null, CompanyApiError.Unauthorized, ct);

    public Task PostAuditAsync(Uri baseUrl, string accessToken, AuditBatchRequest batch, CancellationToken ct) =>
        SendAsync<AuditBatchResponse>(baseUrl, HttpMethod.Post, "api/me/audit", accessToken,
            JsonContent.Create(batch, options: CompanyJson.Options), CompanyApiError.Unauthorized, ct, readBody: false);

    async Task<T> SendAsync<T>(Uri baseUrl, HttpMethod method, string path, string? bearer, HttpContent? content,
        CompanyApiError on401, CancellationToken ct, bool readBody = true)
    {
        baseUrl = ValidateBaseUrl(baseUrl.ToString());
        using var request = new HttpRequestMessage(method, new Uri(baseUrl, path)) { Content = content };
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new CompanyApiException(Map(response.StatusCode, on401), Message(Map(response.StatusCode, on401))) { StatusCode = (int)response.StatusCode };
            return readBody
                ? await response.Content.ReadFromJsonAsync<T>(CompanyJson.Options, ct).ConfigureAwait(false) ?? throw Fail(CompanyApiError.BadResponse)
                : default!;
        }
        catch (CompanyApiException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Fail(CompanyApiError.Unreachable); } // the 15 s timeout
        catch (HttpRequestException) { throw Fail(CompanyApiError.Unreachable); }
        catch (JsonException) { throw Fail(CompanyApiError.BadResponse); }
        catch (NotSupportedException) { throw Fail(CompanyApiError.BadResponse); }
    }

    static CompanyApiError Map(HttpStatusCode status, CompanyApiError on401) => (int)status switch
    {
        401 => on401,
        403 => CompanyApiError.UserDisabled,
        423 => CompanyApiError.LockedOut,
        429 => CompanyApiError.RateLimited,
        >= 500 => CompanyApiError.Unreachable, // a stopped backend behind a tunnel or proxy answers 502/503/504
        _ => CompanyApiError.BadResponse,
    };
}
