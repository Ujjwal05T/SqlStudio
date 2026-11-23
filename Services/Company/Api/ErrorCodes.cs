namespace SqlStudio.Services.Company.Api;

public sealed record ErrorResponse(string Error, string Message);

public static class ErrorCodes
{
    public const string InvalidCredentials = "invalid_credentials";
    public const string UserDisabled = "user_disabled";
    public const string LockedOut = "locked_out";
    public const string InvalidRefresh = "invalid_refresh";
    public const string Unauthorized = "unauthorized";
    public const string BadRequest = "bad_request";
}
