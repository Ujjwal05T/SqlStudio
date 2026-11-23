namespace SqlStudio.Services.Company.Api;

public enum AuditKind { SignIn, SignInFailed, SignOut, TokenReuseDetected, AdminChange, Connect, BlockedAttempts }

// Deliberately no field for SQL text.
public sealed record AuditEventDto(Guid ClientEventId, DateTimeOffset At, AuditKind Kind, Guid? ServerId, string? Database, int? Count);

public sealed record AuditBatchRequest(IReadOnlyList<AuditEventDto> Events);

public sealed record AuditBatchResponse(int Accepted);
