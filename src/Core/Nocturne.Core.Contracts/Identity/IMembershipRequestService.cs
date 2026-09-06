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

    /// <summary>
    /// Approves the request and adds the subject as a member with <paramref name="roleIds"/>.
    /// </summary>
    /// <param name="granterScopes">
    /// The approving caller's resolved scopes. Required, not optional: approving confers the
    /// requested roles, so it is bound by the same grant ceiling as a direct permission edit.
    /// </param>
    Task<DecideMembershipRequestResult> ApproveRequestAsync(
        Guid requestId, Guid tenantId, List<Guid> roleIds,
        Guid decidedBySubjectId, IReadOnlyCollection<string> granterScopes,
        CancellationToken ct = default);

    Task<DecideMembershipRequestResult> DenyRequestAsync(
        Guid requestId, Guid tenantId,
        Guid decidedBySubjectId, CancellationToken ct = default);

    /// <summary>Whether the tenant currently accepts requests to join (<c>AllowAccessRequests</c>).</summary>
    Task<bool> GetAllowRequestsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Enable or disable whether people can request to join the tenant. Returns the new value.</summary>
    Task<bool> SetAllowRequestsAsync(Guid tenantId, bool allowRequests, CancellationToken ct = default);
}
