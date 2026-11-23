namespace SqlStudio.Services.Company.Api;

public sealed record LoginRequest(string Email, string Password, string Client)
{
    private bool PrintMembers(System.Text.StringBuilder b)
    {
        b.Append($"Email = {Email}, Password = ***, Client = {Client}");
        return true;
    }
}

public sealed record RefreshRequest(string RefreshToken)
{
    private bool PrintMembers(System.Text.StringBuilder b)
    {
        b.Append("RefreshToken = ***");
        return true;
    }
}

public sealed record LogoutRequest(string RefreshToken)
{
    private bool PrintMembers(System.Text.StringBuilder b)
    {
        b.Append("RefreshToken = ***");
        return true;
    }
}

public sealed record UserDto(Guid Id, string Email, string DisplayName);

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserDto User)
{
    private bool PrintMembers(System.Text.StringBuilder b)
    {
        b.Append($"AccessToken = ***, AccessTokenExpiresAt = {AccessTokenExpiresAt:O}, RefreshToken = ***, RefreshTokenExpiresAt = {RefreshTokenExpiresAt:O}, User = {User}");
        return true;
    }
}
