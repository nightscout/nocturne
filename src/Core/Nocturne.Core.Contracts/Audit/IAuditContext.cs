namespace Nocturne.Core.Contracts.Audit;

/// <summary>
/// Provides actor and request metadata for mutation audit logging.
/// Populated per-request by middleware (HTTP) or manually (background services).
/// </summary>
public interface IAuditContext
{
    Guid? SubjectId { get; }
    string? SubjectName { get; }
    string? AuthType { get; }
    string? IpAddress { get; }
    Guid? TokenId { get; }
    string? CorrelationId { get; }
    string? Endpoint { get; }
}
