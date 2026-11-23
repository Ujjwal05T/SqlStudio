namespace SqlStudio.Services.Company.Api;

public sealed record CompanyDatabaseDto(string Name, IReadOnlyList<string> Permissions);

public sealed record CompanyServerDto(
    Guid Id,
    string Name,
    string Host,
    string Environment,
    bool Encrypt,
    bool TrustServerCertificate,
    string Login,
    string Password,
    IReadOnlyList<CompanyDatabaseDto> Databases)
{
    private bool PrintMembers(System.Text.StringBuilder b)
    {
        b.Append($"Id = {Id}, Name = {Name}, Host = {Host}, Environment = {Environment}, Encrypt = {Encrypt}, TrustServerCertificate = {TrustServerCertificate}, Login = {Login}, Password = ***, Databases = {Databases.Count}");
        return true;
    }
}

public sealed record ConnectionsResponse(
    DateTimeOffset FetchedAt,
    int RefreshAfterSeconds,
    int OfflineGraceSeconds,
    IReadOnlyList<CompanyServerDto> Servers);
