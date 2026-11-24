using SqlStudio.Models;

namespace SqlStudio.Services.Company;

/// <summary>
/// Told when a company tab connects or is blocked. It takes no SQL text, statement kinds or messages on purpose,
/// so nothing a user typed can reach the audit log.
/// </summary>
public interface IQueryAuditor
{
    void Connected(ConnectionProfile p, string? database);
    void Blocked(ConnectionProfile p, string? database);
}
