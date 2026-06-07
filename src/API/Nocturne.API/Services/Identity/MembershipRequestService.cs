using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Manages tenant membership requests: creation, listing pending requests,
/// and approval/denial with automatic notification delivery.
/// </summary>
public class MembershipRequestService : IMembershipRequestService
{
    private readonly NocturneDbContext _dbContext;
    private readonly ITenantService _tenantService;
    private readonly IInAppNotificationService _notificationService;
    private readonly ILogger<MembershipRequestService> _logger;

    public MembershipRequestService(
        NocturneDbContext dbContext,
        ITenantService tenantService,
        IInAppNotificationService notificationService,
        ILogger<MembershipRequestService> logger)
    {
        _dbContext = dbContext;
        _tenantService = tenantService;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CreateMembershipRequestResult> CreateRequestAsync(
        Guid tenantId, Guid subjectId, string? message, CancellationToken ct = default)
    {
        _dbContext.TenantId = tenantId;

        var tenant = await _dbContext.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant == null)
            return new CreateMembershipRequestResult(false, "Tenant not found.");
        if (!tenant.AllowAccessRequests)
            return new CreateMembershipRequestResult(false, "This profile is not accepting membership requests.");

        var existingPending = await _dbContext.MembershipRequests
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

        _dbContext.MembershipRequests.Add(entity);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "MembershipRequestAudit: {Event} request_id={RequestId} tenant_id={TenantId} subject_id={SubjectId}",
            "request_created", entity.Id, tenantId, subjectId);

        // Look up the requester's name for the notification
        var subject = await _dbContext.Subjects.FindAsync([subjectId], ct);
        var subjectName = subject?.Name ?? "Someone";

        // Find all tenant members whose roles include "members.manage" or "*"
        var membersToNotify = await _dbContext.TenantMembers
            .Where(m => m.TenantId == tenantId)
            .Where(m => m.MemberRoles.Any(mr =>
                mr.TenantRole.Permissions.Any(p => p == "members.manage" || p == "*")))
            .Select(m => m.SubjectId)
            .Distinct()
            .ToListAsync(ct);

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
        _dbContext.TenantId = tenantId;

        var entity = await _dbContext.MembershipRequests
            .Where(r => r.SubjectId == subjectId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (entity == null)
            return null;

        var subject = await _dbContext.Subjects.FindAsync([subjectId], ct);

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
        _dbContext.TenantId = tenantId;

        var requests = await _dbContext.MembershipRequests
            .Where(r => r.Status == "pending")
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        if (requests.Count == 0)
            return [];

        var subjectIds = requests.Select(r => r.SubjectId).Distinct().ToList();
        var subjects = await _dbContext.Subjects
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
        Guid decidedBySubjectId, CancellationToken ct = default)
    {
        _dbContext.TenantId = tenantId;

        var request = await _dbContext.MembershipRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (request == null)
            return new DecideMembershipRequestResult(false, "Request not found.");

        if (request.Status != "pending")
            return new DecideMembershipRequestResult(false, "Request is no longer pending.");

        request.Status = "approved";
        request.DecidedBySubjectId = decidedBySubjectId;
        request.DecidedAt = DateTime.UtcNow;
        request.RoleIds = roleIds;
        await _dbContext.SaveChangesAsync(ct);

        await _tenantService.AddMemberAsync(tenantId, request.SubjectId, roleIds, ct: ct);

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
        _dbContext.TenantId = tenantId;

        var request = await _dbContext.MembershipRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (request == null)
            return new DecideMembershipRequestResult(false, "Request not found.");

        if (request.Status != "pending")
            return new DecideMembershipRequestResult(false, "Request is no longer pending.");

        request.Status = "denied";
        request.DecidedBySubjectId = decidedBySubjectId;
        request.DecidedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);

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
        var tenant = await _dbContext.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");
        return tenant.AllowAccessRequests;
    }

    /// <inheritdoc />
    public async Task<bool> SetAllowRequestsAsync(Guid tenantId, bool allowRequests, CancellationToken ct = default)
    {
        var tenant = await _dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found");

        tenant.AllowAccessRequests = allowRequests;
        await _dbContext.SaveChangesAsync(ct);
        return tenant.AllowAccessRequests;
    }
}
