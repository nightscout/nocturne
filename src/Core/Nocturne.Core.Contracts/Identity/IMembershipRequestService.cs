namespace Nocturne.Core.Contracts.Identity;

public record MembershipRequestDto(
    Guid Id,
    Guid SubjectId,
    string? SubjectName,
    string? AvatarUrl,
    string? Message,
    string Status,
    DateTime CreatedAt);

public record CreateMembershipRequestResult(bool Success, string? Error);
public record DecideMembershipRequestResult(bool Success, string? Error);

public interface IMembershipRequestService
{
    Task<CreateMembershipRequestResult> CreateRequestAsync(
        Guid tenantId, Guid subjectId, string? message, CancellationToken ct = default);

    Task<MembershipRequestDto?> GetMyRequestAsync(
        Guid tenantId, Guid subjectId, CancellationToken ct = default);

    Task<List<MembershipRequestDto>> GetPendingRequestsAsync(
        Guid tenantId, CancellationToken ct = default);

    Task<DecideMembershipRequestResult> ApproveRequestAsync(
        Guid requestId, Guid tenantId, List<Guid> roleIds,
        Guid decidedBySubjectId, CancellationToken ct = default);

    Task<DecideMembershipRequestResult> DenyRequestAsync(
        Guid requestId, Guid tenantId,
        Guid decidedBySubjectId, CancellationToken ct = default);
}
