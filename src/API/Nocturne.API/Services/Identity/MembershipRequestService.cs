using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Manages tenant membership requests: creation, listing pending requests,
/// and approval/denial with automatic notification delivery.
/// </summary>
public class MembershipRequestService : IMembershipRequestService
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly ITenantService _tenantService;
    private readonly ITenantRoleService _tenantRoleService;
    private readonly IInAppNotificationService _notificationService;
    private readonly ILogger<MembershipRequestService> _logger;

    public MembershipRequestService(
        INocturneDbContextFactory contexts,
        ITenantService tenantService,
        ITenantRoleService tenantRoleService,
        IInAppNotificationService notificationService,
        ILogger<MembershipRequestService> logger)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _tenantService = tenantService;
        _tenantRoleService = tenantRoleService;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CreateMembershipRequestResult> CreateRequestAsync(
        Guid tenantId, Guid subjectId, string? message, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var tenant = await db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant == null)
            return new CreateMembershipRequestResult(false, "Tenant not found.");
        if (!tenant.AllowAccessRequests)
            return new CreateMembershipRequestResult(false, "This profile is not accepting membership requests.");

        var existingPending = await db.MembershipRequests
            .AnyAsync(r => r.SubjectId == subjectId && r.Status == "pending", ct);

        if (existingPending)
            return new CreateMembershipRequestResult(false, "A pending request already exists.");

        var entity = new Infrastructure.Data.Entities.MembershipRequestEntity
        {
            TenantId = tenantId,
            SubjectId = subjectId,
            Message = message,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
        };

        db.MembershipRequests.Add(entity);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "MembershipRequestAudit: {Event} request_id={RequestId} tenant_id={TenantId} subject_id={SubjectId}",
            "request_created", entity.Id, tenantId, subjectId);

        // Look up the requester's name for the notification
        var subject = await db.Subjects.FindAsync([subjectId], ct);
        var subjectName = subject?.Name ?? "Someone";

        var candidates = await db.TenantMembers
            .AsNoTracking()
            .Include(m => m.MemberRoles)
                .ThenInclude(mr => mr.TenantRole)
            .PeopleOf(tenantId)
            .ToListAsync(ct);

        var membersToNotify = candidates
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
                sourceId: entity.Id.ToString(),
                cancellationToken: ct);
        }

        return new CreateMembershipRequestResult(true, null);
    }

    /// <inheritdoc />
    public async Task<MembershipRequestDto?> GetMyRequestAsync(
        Guid tenantId, Guid subjectId, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await db.MembershipRequests
            .Where(r => r.SubjectId == subjectId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (entity == null)
            return null;

        var subject = await db.Subjects.FindAsync([subjectId], ct);

        return new MembershipRequestDto(
            entity.Id,
            entity.SubjectId,
            subject?.Name,
            subject?.AvatarUrl,
            entity.Message,
            entity.Status,
            entity.CreatedAt);
    }

    /// <inheritdoc />
    public async Task<List<MembershipRequestDto>> GetPendingRequestsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var requests = await db.MembershipRequests
            .Where(r => r.Status == "pending")
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        if (requests.Count == 0)
            return [];

        var subjectIds = requests.Select(r => r.SubjectId).Distinct().ToList();
        var subjects = await db.Subjects
            .Where(s => subjectIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        return requests.Select(r =>
        {
            subjects.TryGetValue(r.SubjectId, out var subject);
            return new MembershipRequestDto(
                r.Id,
                r.SubjectId,
                subject?.Name,
                subject?.AvatarUrl,
                r.Message,
                r.Status,
                r.CreatedAt);
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<DecideMembershipRequestResult> ApproveRequestAsync(
        Guid requestId, Guid tenantId, List<Guid> roleIds,
        Guid decidedBySubjectId, IReadOnlyCollection<string> granterScopes,
        bool limitTo24Hours = false,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var roleGrant = await _tenantRoleService.ValidateRoleGrantAsync(
            tenantId, roleIds, granterScopes, ct);
        if (!roleGrant.Ok)
            return new DecideMembershipRequestResult(false, roleGrant.ErrorDescription);

        if (limitTo24Hours
            && MemberScopeResolver.IsExemptFromHistoryClamp(
                await _tenantRoleService.GetRolePermissionsAsync(tenantId, roleIds, ct)))
            return new DecideMembershipRequestResult(false, MemberScopeResolver.ExemptFromHistoryClampDetail);

        var request = await db.MembershipRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (request == null)
            return new DecideMembershipRequestResult(false, "Request not found.");

        if (request.Status != "pending")
            return new DecideMembershipRequestResult(false, "Request is no longer pending.");

        request.Status = "approved";
        request.DecidedBySubjectId = decidedBySubjectId;
        request.DecidedAt = DateTime.UtcNow;
        request.RoleIds = roleIds;
        await db.SaveChangesAsync(ct);

        await _tenantService.AddMemberAsync(
            tenantId, request.SubjectId, roleIds, limitTo24Hours: limitTo24Hours, ct: ct);

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
        await using var db = await _contexts.CreateAsync(ct);
        var request = await db.MembershipRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (request == null)
            return new DecideMembershipRequestResult(false, "Request not found.");

        if (request.Status != "pending")
            return new DecideMembershipRequestResult(false, "Request is no longer pending.");

        request.Status = "denied";
        request.DecidedBySubjectId = decidedBySubjectId;
        request.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

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
    public async Task<bool> GetAllowRequestsAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var tenant = await db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
        return tenant.AllowAccessRequests;
    }

    /// <inheritdoc />
    public async Task<bool> SetAllowRequestsAsync(Guid tenantId, bool allowRequests, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var tenant = await db.Tenants
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");

        tenant.AllowAccessRequests = allowRequests;
        await db.SaveChangesAsync(ct);
        return tenant.AllowAccessRequests;
    }
}
