namespace SqlStudio.Services.Company;


/// <summary>Keeps the company refresh token between runs in the operating system's secure store. The token is never logged or written to a settings file.</summary>
public interface ISecureTokenStore
{
    string? Load();
    void Save(string token);
    void Delete();
}
