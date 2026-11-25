using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
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
    public const string ApprovedNotificationType = "membership.approved";
    public const string DeniedNotificationType = "membership.denied";

    private static readonly DecideMembershipRequestResult AlreadyAMember =
        new(false, "The subject is already a member.");

    private static readonly DecideMembershipRequestResult OwnRequest =
        new(false, SelfAdmission.OwnRequestDetail) { Code = SelfAdmission.OwnRequestCode };

    private static readonly DecideMembershipRequestResult NoLongerPending =
        new(false, "Request is no longer pending.");

    private static readonly DecideMembershipRequestResult RequesterGone =
        new(false, "The requester no longer exists.");

    private static readonly DecideMembershipRequestResult RequesterInactive =
        new(false, "The requester's account is not active.");

    private readonly ITenantRepository _tenants;
    private readonly ITenantMembershipRepository _members;
    private readonly ISubjectRepository _subjects;
    private readonly INocturneDbContextFactory _contexts;
    private readonly ITenantRoleService _tenantRoleService;
    private readonly IInAppNotificationService _notificationService;
    private readonly IAuthAuditService _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<MembershipRequestService> _logger;
    private readonly IAccountLinkRoute _linkRoute;
    private readonly GrantRevocationService _grantRevocation;
    private readonly TeamStaffAnnouncer _teamStaff;

    public MembershipRequestService(
        ITenantRepository tenants,
        ITenantMembershipRepository members,
        ISubjectRepository subjects,
        INocturneDbContextFactory contexts,
        ITenantRoleService tenantRoleService,
        IInAppNotificationService notificationService,
        IAuthAuditService audit,
        TimeProvider timeProvider,
        ILogger<MembershipRequestService> logger,
        IAccountLinkRoute linkRoute,
        GrantRevocationService grantRevocation,
        TeamStaffAnnouncer teamStaff)
    {
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _members = members ?? throw new ArgumentNullException(nameof(members));
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _tenantRoleService = tenantRoleService;
        _notificationService = notificationService;
        _audit = audit;
        _time = timeProvider;
        _logger = logger;
        _linkRoute = linkRoute;
        _grantRevocation = grantRevocation ?? throw new ArgumentNullException(nameof(grantRevocation));
        _teamStaff = teamStaff;
    }

    /// <inheritdoc />
    public async Task<CreateMembershipRequestResult> CreateRequestAsync(
        Guid tenantId, Guid subjectId, string? message, CancellationToken ct = default)
    {
        var tenant = await _tenants.GetAsync(tenantId, ct);
        if (tenant == null)
            return new CreateMembershipRequestResult(CreateMembershipRequestOutcome.Refused, "Tenant not found.");
        if (!tenant.AllowAccessRequests)
            return new CreateMembershipRequestResult(CreateMembershipRequestOutcome.Refused, "This profile is not accepting membership requests.");

        if (await _members.IsDirectMemberAsync(tenantId, subjectId, _time.GetUtcNow().UtcDateTime, ct))
            return new CreateMembershipRequestResult(CreateMembershipRequestOutcome.AlreadyMember, "You are already a member.");

        if (await _members.AnyPendingRequestAsync(tenantId, subjectId, ct))
            return new CreateMembershipRequestResult(CreateMembershipRequestOutcome.AlreadyPending, "A pending request already exists.");

        var requestId = await _members.CreateRequestAsync(
            new NewMembershipRequest(tenantId, subjectId, message, _time.GetUtcNow().UtcDateTime), ct);

        _logger.LogInformation(
            "MembershipRequestAudit: {Event} request_id={RequestId} tenant_id={TenantId} subject_id={SubjectId}",
            "request_created", requestId, tenantId, subjectId);

        await TellDecidersAsync(tenantId, subjectId, requestId, message);

        return new CreateMembershipRequestResult(CreateMembershipRequestOutcome.Created, null);
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
        DateTime? accessEndsAt,
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

        if (accessEndsAt is not null
            && await _members.FindRoleIdBySlugAsync(tenantId, RoleSeeds.Owner, ct) is { } ownerRole
            && roleIds.Contains(ownerRole))
            return new DecideMembershipRequestResult(false, AccessEndRequest.OwnerDetail);

        var request = await _members.GetRequestAsync(tenantId, requestId, ct);

        if (request == null)
            return new DecideMembershipRequestResult(false, "Request not found.");

        if (request.Status != MembershipRequestRecord.Pending)
            return NoLongerPending;

        DecideMembershipRequestResult? refusal = null;
        IReadOnlyList<OAuthGrantRecord> revoked = [];
        var activated = false;
        await using (var unit = await DbUnitOfWork.OpenForTenantAsync(_contexts, tenantId, ct))
        {
            var members = new TenantMembershipRepository(unit, _linkRoute);
            var subjects = new SubjectRepository(unit, _linkRoute, _time);
            try
            {
                await unit.CommitAsync(async token =>
                {
                    activated = false;
                    await TenantMembershipRepository.LockOwnerChangeAsync(unit, tenantId, request.SubjectId, token);
                    switch (await subjects.GetAsync(request.SubjectId, token))
                    {
                        case null:
                            throw new RefusedCommitException<DecideMembershipRequestResult>(RequesterGone);
                        case { ApprovalStatus: nameof(ApprovalStatus.Pending), IsActive: false }:
                            await subjects.UpdateAsync(request.SubjectId, SubjectEdit.AccessApproved, token);
                            activated = true;
                            break;
                        case { IsActive: false }:
                            throw new RefusedCommitException<DecideMembershipRequestResult>(RequesterInactive);
                    }

                    revoked = await MembershipRemoval.ReplaceEndedAsync(
                            unit, members, _grantRevocation, tenantId, request.SubjectId, _time.GetUtcNow().UtcDateTime, token)
                        ?? throw new RefusedCommitException<DecideMembershipRequestResult>(AlreadyAMember);

                    if (request.SubjectId == decidedBySubjectId
                        && await members.FindMemberAsync(tenantId, request.SubjectId, token) is { DirectReason: null })
                        throw new RefusedCommitException<DecideMembershipRequestResult>(OwnRequest);

                    if (!await members.AddDirectMemberAsync(
                            new NewTenantMember(
                                tenantId, request.SubjectId, roleIds, LimitTo24Hours: limitTo24Hours, AccessEndsAt: accessEndsAt)
                                { How = MembershipJoinHow.Request, By = decidedBySubjectId },
                            _time.GetUtcNow().UtcDateTime,
                            token))
                        throw new RefusedCommitException<DecideMembershipRequestResult>(AlreadyAMember);

                    if (!await members.DecideRequestAsync(
                            tenantId, requestId,
                            new MembershipRequestDecision(MembershipRequestRecord.Approved, decidedBySubjectId, _time.GetUtcNow().UtcDateTime, roleIds),
                            token))
                        throw new RefusedCommitException<DecideMembershipRequestResult>(NoLongerPending);

                    await unit.Context.SaveChangesAsync(token);
                    revoked = [.. revoked, .. await TeamReasonWrites.SyncOwnerChangeAsync(
                        unit, _grantRevocation, tenantId, request.SubjectId, _time.GetUtcNow().UtcDateTime, token)];
                }, ct);
            }
            catch (RefusedCommitException<DecideMembershipRequestResult> refused)
            {
                refusal = refused.Refusal;
            }
            catch (DbUpdateConcurrencyException)
            {
                return RequesterGone;
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                return AlreadyAMember;
            }
        }

        if (refusal is not null)
            return refusal;

        _grantRevocation.EvictSessions(revoked);
        await _teamStaff.RecountOwnerChangeAsync(tenantId, request.SubjectId);

        _logger.LogInformation(
            "MembershipRequestAudit: {Event} request_id={RequestId} tenant_id={TenantId} subject_id={SubjectId} decided_by={DecidedBy}",
            "request_approved", requestId, tenantId, request.SubjectId, decidedBySubjectId);

        if (activated)
            await AuditAccessDecisionAsync(request, nameof(ApprovalStatus.Approved));

        await _notificationService.CreateNotificationAsync(
            request.SubjectId.ToString(),
            ApprovedNotificationType,
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

        bool decided;
        var denied = false;
        await using (var unit = await DbUnitOfWork.OpenForTenantAsync(_contexts, tenantId, ct))
        {
            var members = new TenantMembershipRepository(unit, _linkRoute);
            var subjects = new SubjectRepository(unit, _linkRoute, _time);
            decided = await unit.CommitAsync(async token =>
            {
                denied = false;
                if (await subjects.GetAsync(request.SubjectId, token) is { ApprovalStatus: nameof(ApprovalStatus.Pending), IsActive: false })
                {
                    await subjects.UpdateAsync(request.SubjectId, SubjectEdit.AccessDenied, token);
                    denied = true;
                }

                if (!await members.DecideRequestAsync(
                        tenantId, requestId,
                        new MembershipRequestDecision(MembershipRequestRecord.Denied, decidedBySubjectId, _time.GetUtcNow().UtcDateTime, null),
                        token))
                    return false;

                await unit.Context.SaveChangesAsync(token);
                return true;
            }, ct: ct);
        }

        if (!decided)
            return NoLongerPending;

        if (denied)
            await AuditAccessDecisionAsync(request, nameof(ApprovalStatus.Denied));

        _logger.LogInformation(
            "MembershipRequestAudit: {Event} request_id={RequestId} tenant_id={TenantId} subject_id={SubjectId} decided_by={DecidedBy}",
            "request_denied", requestId, tenantId, request.SubjectId, decidedBySubjectId);

        await _notificationService.CreateNotificationAsync(
            request.SubjectId.ToString(),
            DeniedNotificationType,
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

    /// <summary>
    /// Tells every member who can decide the request that it was filed.
    /// </summary>
    /// <remarks>
    /// The request is committed before this runs, and the anonymous passkey ceremony keeps the
    /// requester's credential only for a request that stands, so nothing here may fail the filing:
    /// it ignores the caller's cancellation, and a failure is logged. The owner still finds the
    /// request in the members list.
    /// </remarks>
    private async Task TellDecidersAsync(Guid tenantId, Guid subjectId, Guid requestId, string? message)
    {
        try
        {
            var subjectName = (await _subjects.GetManyAsync([subjectId], CancellationToken.None)).SingleOrDefault()?.Name ?? "Someone";

            var membersToNotify = (await _members.ListLivePeopleAsync(tenantId, _time.GetUtcNow().UtcDateTime, CancellationToken.None))
                .Where(m => m.Holds(Scope.MembersManage))
                .Select(m => m.SubjectId)
                .Distinct()
                .ToList();

            foreach (var recipientSubjectId in membersToNotify)
            {
                await _notificationService.CreateNotificationAsync(
                    recipientSubjectId.ToString(),
                    NotificationSubjectNaming.MembershipRequested,
                    $"Membership request from {subjectName}",
                    subtitle: message,
                    sourceId: requestId.ToString(),
                    cancellationToken: CancellationToken.None);
            }
        }
        catch (Exception ex) when (StoreFailure.Is(ex))
        {
            LogDecidersNotTold(ex, tenantId, requestId);
        }
        catch (OperationCanceledException ex)
        {
            LogDecidersNotTold(ex, tenantId, requestId);
        }
        catch (InvalidOperationException ex)
        {
            LogDecidersNotTold(ex, tenantId, requestId);
        }
    }

    private void LogDecidersNotTold(Exception ex, Guid tenantId, Guid requestId) =>
        _logger.LogWarning(ex,
            "MembershipRequestAudit: {Event} request_id={RequestId} tenant_id={TenantId}",
            "request_notification_failed", requestId, tenantId);

    private Task AuditAccessDecisionAsync(MembershipRequestRecord request, string approvalStatus) =>
        _audit.LogAsync(
            AuthAuditEventType.SubjectUpdated,
            request.SubjectId,
            success: true,
            detailsJson: JsonSerializer.Serialize(new { approvalStatus, membershipRequestId = request.Id }),
            tenantId: request.TenantId);

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
