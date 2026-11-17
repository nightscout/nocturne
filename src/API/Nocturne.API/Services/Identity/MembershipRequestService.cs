using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Manages tenant membership requests: creation, listing pending requests,
/// and approval/denial with automatic notification delivery.
/// </summary>
public class MembershipRequestService : IMembershipRequestService
{
    private static readonly DecideMembershipRequestResult AlreadyAMember =
        new(false, "The subject is already a member.");

    private static readonly DecideMembershipRequestResult NoLongerPending =
        new(false, "Request is no longer pending.");

    private readonly ITenantRepository _tenants;
    private readonly ITenantMembershipRepository _members;
    private readonly ISubjectRepository _subjects;
    private readonly INocturneDbContextFactory _contexts;
    private readonly ITenantRoleService _tenantRoleService;
    private readonly IInAppNotificationService _notificationService;
    private readonly TimeProvider _time;
    private readonly ILogger<MembershipRequestService> _logger;

    public MembershipRequestService(
        ITenantRepository tenants,
        ITenantMembershipRepository members,
        ISubjectRepository subjects,
        INocturneDbContextFactory contexts,
        ITenantRoleService tenantRoleService,
        IInAppNotificationService notificationService,
        TimeProvider timeProvider,
        ILogger<MembershipRequestService> logger)
    {
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _members = members ?? throw new ArgumentNullException(nameof(members));
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _tenantRoleService = tenantRoleService;
        _notificationService = notificationService;
        _time = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CreateMembershipRequestResult> CreateRequestAsync(
        Guid tenantId, Guid subjectId, string? message, CancellationToken ct = default)
    {
        var tenant = await _tenants.GetAsync(tenantId, ct);
        if (tenant == null)
            return new CreateMembershipRequestResult(false, "Tenant not found.");
        if (!tenant.AllowAccessRequests)
            return new CreateMembershipRequestResult(false, "This profile is not accepting membership requests.");

        if (await _members.AnyPendingRequestAsync(tenantId, subjectId, ct))
            return new CreateMembershipRequestResult(false, "A pending request already exists.");

        var requestId = await _members.CreateRequestAsync(
            new NewMembershipRequest(tenantId, subjectId, message, _time.GetUtcNow().UtcDateTime), ct);

        _logger.LogInformation(
            "MembershipRequestAudit: {Event} request_id={RequestId} tenant_id={TenantId} subject_id={SubjectId}",
            "request_created", requestId, tenantId, subjectId);

        var subjectName = (await _subjects.GetManyAsync([subjectId], ct)).SingleOrDefault()?.Name ?? "Someone";

        var membersToNotify = (await _members.ListPeopleAsync(tenantId, ct))
            .Where(m => m.Holds(Scope.MembersManage))
            .Select(m => m.SubjectId)
            .Distinct()
            .ToList();

        foreach (var recipientSubjectId in membersToNotify)
        {
            await _notificationService.CreateNotificationAsync(
                recipientSubjectId.ToString(),
                "membership.requested",
                $"Membership request from {subjectName}",
                subtitle: message,
                sourceId: requestId.ToString(),
                cancellationToken: ct);
        }

        return new CreateMembershipRequestResult(true, null);
    }

    /// <inheritdoc />
    public async Task<MembershipRequestDto?> GetMyRequestAsync(
        Guid tenantId, Guid subjectId, CancellationToken ct = default)
    {
        var request = await _members.FindLatestRequestAsync(tenantId, subjectId, ct);
        if (request == null)
            return null;

        var subject = await _subjects.GetAsync(subjectId, ct);

        return ToDto(request, subject?.Name, subject?.AvatarUrl);
    }

    /// <inheritdoc />
    public async Task<List<MembershipRequestDto>> GetPendingRequestsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        var requests = await _members.ListPendingRequestsAsync(tenantId, ct);
        if (requests.Count == 0)
            return [];

        var subjectIds = requests.Select(r => r.SubjectId).Distinct().ToList();
        var subjects = (await _subjects.GetManyAsync(subjectIds, ct)).ToDictionary(s => s.Id);

        return requests.Select(r =>
        {
            subjects.TryGetValue(r.SubjectId, out var subject);
            return ToDto(r, subject?.Name, subject?.AvatarUrl);
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<DecideMembershipRequestResult> ApproveRequestAsync(
        Guid requestId, Guid tenantId, List<Guid> roleIds,
        Guid decidedBySubjectId, IReadOnlyCollection<string> granterScopes,
        bool limitTo24Hours = false,
        CancellationToken ct = default)
    {
        var roleGrant = await _tenantRoleService.ValidateRoleGrantAsync(
            tenantId, roleIds, granterScopes, ct);
        if (!roleGrant.Ok)
            return new DecideMembershipRequestResult(false, roleGrant.ErrorDescription);

        if (limitTo24Hours
            && MemberScopeResolver.IsExemptFromHistoryClamp(
                await _tenantRoleService.GetRolePermissionsAsync(tenantId, roleIds, ct)))
            return new DecideMembershipRequestResult(false, MemberScopeResolver.ExemptFromHistoryClampDetail);

        var request = await _members.GetRequestAsync(tenantId, requestId, ct);

        if (request == null)
            return new DecideMembershipRequestResult(false, "Request not found.");

        if (request.Status != MembershipRequestRecord.Pending)
            return NoLongerPending;

        DecideMembershipRequestResult? refusal;
        await using (var unit = await DbUnitOfWork.OpenForTenantAsync(_contexts, tenantId, ct))
        {
            var members = new TenantMembershipRepository(unit);
            try
            {
                refusal = await unit.CommitAsync<DecideMembershipRequestResult?>(async token =>
                {
                    if (!await members.AddMemberAsync(
                            new NewTenantMember(tenantId, request.SubjectId, roleIds, LimitTo24Hours: limitTo24Hours), token))
                        return AlreadyAMember;

                    if (!await members.DecideRequestAsync(
                            tenantId, requestId,
                            new MembershipRequestDecision(MembershipRequestRecord.Approved, decidedBySubjectId, _time.GetUtcNow().UtcDateTime, roleIds),
                            token))
                        return NoLongerPending;

                    await unit.Context.SaveChangesAsync(token);
                    return null;
                }, ct: ct);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                return AlreadyAMember;
            }
        }

        if (refusal is not null)
            return refusal;

        _logger.LogInformation(
            "MembershipRequestAudit: {Event} request_id={RequestId} tenant_id={TenantId} subject_id={SubjectId} decided_by={DecidedBy}",
            "request_approved", requestId, tenantId, request.SubjectId, decidedBySubjectId);

        await _notificationService.CreateNotificationAsync(
            request.SubjectId.ToString(),
            "membership.approved",
            "Your membership request has been approved",
            sourceId: requestId.ToString(),
            cancellationToken: ct);

        return new DecideMembershipRequestResult(true, null);
    }

    /// <inheritdoc />
    public async Task<DecideMembershipRequestResult> DenyRequestAsync(
        Guid requestId, Guid tenantId,
        Guid decidedBySubjectId, CancellationToken ct = default)
    {
        var request = await _members.GetRequestAsync(tenantId, requestId, ct);

        if (request == null)
            return new DecideMembershipRequestResult(false, "Request not found.");

        if (request.Status != MembershipRequestRecord.Pending)
            return NoLongerPending;

        if (!await _members.DecideRequestAsync(
                tenantId, requestId,
                new MembershipRequestDecision(MembershipRequestRecord.Denied, decidedBySubjectId, _time.GetUtcNow().UtcDateTime, null),
                ct))
            return NoLongerPending;

        _logger.LogInformation(
            "MembershipRequestAudit: {Event} request_id={RequestId} tenant_id={TenantId} subject_id={SubjectId} decided_by={DecidedBy}",
            "request_denied", requestId, tenantId, request.SubjectId, decidedBySubjectId);

        await _notificationService.CreateNotificationAsync(
            request.SubjectId.ToString(),
            "membership.denied",
            "Your membership request has been denied",
            sourceId: requestId.ToString(),
            cancellationToken: ct);

        return new DecideMembershipRequestResult(true, null);
    }

    /// <inheritdoc />
    public async Task<bool> GetAllowRequestsAsync(Guid tenantId, CancellationToken ct = default) =>
        (await RequireTenantAsync(tenantId, ct)).AllowAccessRequests;

    /// <inheritdoc />
    public async Task<bool> SetAllowRequestsAsync(Guid tenantId, bool allowRequests, CancellationToken ct = default)
    {
        await RequireTenantAsync(tenantId, ct);
        await _tenants.UpdateAsync(tenantId, new TenantEdit(AllowAccessRequests: allowRequests), ct);
        return allowRequests;
    }

    private async Task<TenantRecord> RequireTenantAsync(Guid tenantId, CancellationToken ct) =>
        await _tenants.GetAsync(tenantId, ct) ?? throw new InvalidOperationException($"Tenant {tenantId} not found");

    private static MembershipRequestDto ToDto(MembershipRequestRecord request, string? subjectName, string? avatarUrl) => new(
        request.Id,
        request.SubjectId,
        subjectName,
        avatarUrl,
        request.Message,
        request.Status,
        request.CreatedAt);
}
