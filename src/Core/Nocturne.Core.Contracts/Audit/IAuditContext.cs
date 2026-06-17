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

    /// <summary>
    /// True for system/connector/background mutations (no human actor). These are high-volume
    /// automated data ingestion whose provenance is already captured on the records themselves
    /// (e.g. <c>data_source</c>), so they are NOT written to the mutation audit log. Defaults to
    /// false (user-attributed actions), so every existing implementer stays auditable.
    /// </summary>
    bool IsSystem => false;
}
