namespace Nocturne.Core.Contracts.Identity;

public record MembershipRequestDto(
    Guid Id,
    Guid SubjectId,
    string? SubjectName,
    string? AvatarUrl,
    string? Message,
    string Status,
    DateTime CreatedAt);

public enum CreateMembershipRequestOutcome
{
    Created,
    Refused,
    AlreadyMember,
    AlreadyPending,
}

public record CreateMembershipRequestResult(CreateMembershipRequestOutcome Outcome, string? Error)
{
    public bool Success => Outcome == CreateMembershipRequestOutcome.Created;
}

public record DecideMembershipRequestResult(bool Success, string? Error)
{
    /// <summary>The problem code of a refusal the client tells apart; null for any other outcome.</summary>
    public string? Code { get; init; }
}

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
    /// <remarks>
    /// A requester still awaiting access (<c>ApprovalStatus</c> Pending, inactive) is a visitor who
    /// asked through the anonymous passkey ceremony, which files its one request on the tenant it
    /// ran on. Approving activates that subject in the same transaction as the membership, so the
    /// passkey it registered signs in. Any other inactive or deleted requester is refused.
    /// </remarks>
    /// <param name="granterScopes">
    /// The approving caller's resolved scopes. Required, not optional: approving confers the
    /// requested roles, so it is bound by the same grant ceiling as a direct permission edit.
    /// </param>
    /// <param name="limitTo24Hours">
    /// Whether the new member may read only the last 24 hours: the approver's own ceiling, which a
    /// membership it confers inherits. Refused when the roles would exempt the member.
    /// </param>
    /// <param name="accessEndsAt">
    /// When the new membership ends, in UTC; null when it does not. Refused with the owner role. A
    /// requester whose membership has ended gets a new one in place of it.
    /// </param>
    Task<DecideMembershipRequestResult> ApproveRequestAsync(
        Guid requestId, Guid tenantId, List<Guid> roleIds,
        Guid decidedBySubjectId, IReadOnlyCollection<string> granterScopes,
        DateTime? accessEndsAt,
        bool limitTo24Hours = false,
        CancellationToken ct = default);

    /// <summary>
    /// Denies the request. A requester still awaiting access is marked Denied and stays inactive, in
    /// the same transaction, so it leaves the platform administrators' access-request list.
    /// </summary>
    Task<DecideMembershipRequestResult> DenyRequestAsync(
        Guid requestId, Guid tenantId,
        Guid decidedBySubjectId, CancellationToken ct = default);

    /// <summary>Whether the tenant currently accepts requests to join (<c>AllowAccessRequests</c>).</summary>
    Task<bool> GetAllowRequestsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Enable or disable whether people can request to join the tenant. Returns the new value.</summary>
    Task<bool> SetAllowRequestsAsync(Guid tenantId, bool allowRequests, CancellationToken ct = default);
}
