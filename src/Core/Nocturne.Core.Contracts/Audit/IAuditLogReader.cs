using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Audit;

/// <summary>
/// Reads the current tenant's mutation audit log and read access log as pages. Both reads apply
/// one window, filter, sort and page implementation. Rows within the window are ordered by
/// creation time alone; rows created at the same instant have no defined order.
/// </summary>
public interface IAuditLogReader
{
    Task<PaginatedResponse<MutationAuditEntry>> GetMutationsAsync(
        AuditLogWindow window, MutationAuditFilter filter, CancellationToken ct = default);

    Task<PaginatedResponse<ReadAccessAuditEntry>> GetReadsAsync(
        AuditLogWindow window, ReadAccessLogFilter filter, CancellationToken ct = default);
}

/// <summary>The part of a query both logs share: an inclusive time range, a page and the filters on shared columns.</summary>
public record AuditLogWindow(
    DateTime From,
    DateTime To,
    int Limit,
    int Offset,
    bool OldestFirst,
    Guid? ActorSubjectId,
    string? EntityType);

public record MutationAuditFilter(string? Action, Guid? EntityId);

public record ReadAccessLogFilter(string? Endpoint, int? StatusCode);

public record MutationAuditEntry(
    Guid Id,
    DateTime CreatedAt,
    string EntityType,
    Guid? EntityId,
    string Action,
    string? Changes,
    Guid? ActorSubjectId,
    string? ActorName,
    string? AuthType,
    string? IpAddress,
    string? Endpoint);

public record ReadAccessAuditEntry(
    Guid Id,
    DateTime CreatedAt,
    string Endpoint,
    string? EntityType,
    int? RecordCount,
    int StatusCode,
    string? QueryParameters,
    Guid? ActorSubjectId,
    string? ActorName,
    string? AuthType,
    string? IpAddress,
    string? CredentialFingerprint);
