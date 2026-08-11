using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Manages tenant membership invite links: creates JWT-signed invite tokens,
/// validates them on claim, and enforces single-use expiry via the database.
/// </summary>
/// <seealso cref="IMemberInviteService"/>
public class MemberInviteService : IMemberInviteService
{
    /// <summary>Longest lifetime an invite token may be minted with.</summary>
    private const int MaxExpiresInDays = 90;

    private readonly NocturneDbContext _dbContext;
    private readonly IJwtService _jwtService;
    private readonly ITenantService _tenantService;
    private readonly ITenantRoleService _tenantRoleService;
    private readonly BaseDomainOptions _baseDomain;
    private readonly ILogger<MemberInviteService> _logger;

    public MemberInviteService(
        NocturneDbContext dbContext,
        IJwtService jwtService,
        ITenantService tenantService,
        ITenantRoleService tenantRoleService,
        IOptions<BaseDomainOptions> baseDomainOptions,
        ILogger<MemberInviteService> logger)
    {
        _dbContext = dbContext;
        _jwtService = jwtService;
        _tenantService = tenantService;
        _tenantRoleService = tenantRoleService;
        _baseDomain = baseDomainOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MemberInviteResult> CreateInviteAsync(
        Guid tenantId,
        Guid createdBySubjectId,
        IEnumerable<string> granterPermissions,
        List<Guid> roleIds,
        List<string>? directPermissions = null,
        string? label = null,
        int expiresInDays = 7,
        int? maxUses = null,
        bool limitTo24Hours = false,
        string? baseUrl = null)
    {
        if (roleIds.Count == 0 && (directPermissions == null || directPermissions.Count == 0))
            throw new ArgumentException("At least one role or direct permission is required.");

        // The token is a bearer credential for tenant membership, so its lifetime is bounded
        // rather than taken from the caller.
        if (expiresInDays is < 1 or > MaxExpiresInDays)
            throw new ArgumentException($"Expiry must be between 1 and {MaxExpiresInDays} days.");

        var granter = granterPermissions as IReadOnlyCollection<string> ?? granterPermissions.ToList();

        var directViolation = TenantPermissions.ValidateGrant(directPermissions, granter);
        if (directViolation != null)
            throw new ArgumentException(directViolation.Description);

        var roleGrant = await _tenantRoleService.ValidateRoleGrantAsync(tenantId, roleIds, granter);
        if (!roleGrant.Ok)
            throw new ArgumentException(roleGrant.ErrorDescription);

        // Generate token
        var token = _jwtService.GenerateRefreshToken();
        var tokenHash = _jwtService.HashRefreshToken(token);

        var entity = new MemberInviteEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            CreatedBySubjectId = createdBySubjectId,
            TokenHash = tokenHash,
            RoleIds = roleIds,
            DirectPermissions = directPermissions,
            Label = label,
            LimitTo24Hours = limitTo24Hours,
            ExpiresAt = DateTime.UtcNow.AddDays(expiresInDays),
            MaxUses = maxUses,
            UseCount = 0,
            CreatedAt = DateTime.UtcNow,
        };

        _dbContext.MemberInvites.Add(entity);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "MemberInviteAudit: {Event} invite_id={InviteId} tenant_id={TenantId} role_count={RoleCount} expires_at={ExpiresAt}",
            "invite_created", entity.Id, tenantId, roleIds.Count, entity.ExpiresAt);

        // The join page is served per tenant, so the invite has to point at the tenant's own host.
        // The configured base URL is the instance apex, which in a multi-tenant deployment serves a
        // different site entirely — only the caller knows the host the invite was minted on.
        var origin = (baseUrl ?? _baseDomain.PublicOrigin)?.TrimEnd('/') ?? "";
        var inviteUrl =
            $"{origin}{IMemberInviteService.JoinPath}?{IMemberInviteService.TokenQueryParameter}={token}";

        return new MemberInviteResult(
            entity.Id,
            token,
            inviteUrl,
            entity.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<MemberInviteInfo?> GetInviteByTokenAsync(string token, Guid tenantId)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var tokenHash = _jwtService.HashRefreshToken(token);

        var entity = await _dbContext.MemberInvites
            .Include(i => i.Tenant)
            .Include(i => i.CreatedBy)
            .Where(i => i.TokenHash == tokenHash && i.TenantId == tenantId)
            .FirstOrDefaultAsync();

        if (entity == null)
            return null;

        return MapToInfo(entity);
    }

    /// <inheritdoc />
    public async Task<AcceptMemberInviteResult> AcceptInviteAsync(
        string token, Guid acceptingSubjectId, Guid tenantId)
    {
        if (string.IsNullOrEmpty(token))
            return new AcceptMemberInviteResult(false, "invalid_token", "Invite token is required.");

        var tokenHash = _jwtService.HashRefreshToken(token);

        // Bounded by the tenant the request resolved to, not by the one the token names. The token
        // is the only thing authorizing the join, so a token minted for another tenant must read as
        // unknown here rather than as an invite that happens to point elsewhere.
        var entity = await _dbContext.MemberInvites
            .Include(i => i.Tenant)
            .Where(i => i.TokenHash == tokenHash && i.TenantId == tenantId)
            .FirstOrDefaultAsync();

        if (entity == null)
            return new AcceptMemberInviteResult(false, "invalid_token", "Invite not found or has been revoked.");

        if (entity.IsExpired)
            return new AcceptMemberInviteResult(false, "expired", "This invite has expired.");

        if (entity.IsRevoked)
            return new AcceptMemberInviteResult(false, "revoked", "This invite has been revoked.");

        if (entity.IsExhausted)
            return new AcceptMemberInviteResult(false, "exhausted", "This invite has reached its maximum uses.");

        // Check if already an active member of this tenant
        var existingMember = await _dbContext.TenantMembers
            .Where(m => m.TenantId == entity.TenantId
                        && m.SubjectId == acceptingSubjectId)
            .FirstOrDefaultAsync();

        if (existingMember != null)
            return new AcceptMemberInviteResult(false, "already_member", "You are already a member of this tenant.");

        // Filter out deleted roles from the invite
        var validRoleIds = entity.RoleIds.Count > 0
            ? await _dbContext.TenantRoles
                .Where(r => r.TenantId == entity.TenantId && entity.RoleIds.Contains(r.Id))
                .Select(r => r.Id)
                .ToListAsync()
            : [];

        if (validRoleIds.Count == 0 && (entity.DirectPermissions == null || entity.DirectPermissions.Count == 0))
            return new AcceptMemberInviteResult(false, "no_permissions", "All roles from this invite have been deleted and no direct permissions are assigned.");

        // Claim the use before writing the membership. The IsExhausted check above is a fast path
        // on a value read earlier in the request; two concurrent accepts of a single-use invite —
        // the default the UI mints — would both pass it and both join. Only a row that is still
        // under its cap matches here, so the loser claims nothing and is refused.
        //
        // The claim commits on its own and the membership is written on a separate context, so a
        // failure in between burns the use without joining anyone: a single-use invite then reads
        // as exhausted and the owner has to mint another. That is the direction to fail in — the
        // alternative admits the double join this guards against.
        var claimed = await _dbContext.MemberInvites
            .Where(i => i.Id == entity.Id && (i.MaxUses == null || i.UseCount < i.MaxUses))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UseCount, i => i.UseCount + 1));

        if (claimed == 0)
            return new AcceptMemberInviteResult(false, "exhausted", "This invite has reached its maximum uses.");

        // The guarded update bypasses the change tracker, so the entity loaded above still carries
        // the pre-claim count. Reload it rather than leave a stale row for anything downstream.
        await _dbContext.Entry(entity).ReloadAsync();

        // Create the tenant membership via the tenant service
        await _tenantService.AddMemberAsync(
            entity.TenantId,
            acceptingSubjectId,
            validRoleIds,
            entity.DirectPermissions,
            entity.Label,
            entity.LimitTo24Hours);

        // Get the member ID for the result
        var member = await _dbContext.TenantMembers
            .Where(m => m.TenantId == entity.TenantId && m.SubjectId == acceptingSubjectId)
            .FirstAsync();

        // Update the invite link to the member
        member.CreatedFromInviteId = entity.Id;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "MemberInviteAudit: {Event} invite_id={InviteId} tenant_id={TenantId} subject_id={SubjectId} member_id={MemberId}",
            "invite_accepted", entity.Id, entity.TenantId, acceptingSubjectId, member.Id);

        return new AcceptMemberInviteResult(true, MembershipId: member.Id);
    }

    /// <inheritdoc />
    public async Task<List<MemberInviteInfo>> GetInvitesForTenantAsync(Guid tenantId)
    {
        var entities = await _dbContext.MemberInvites
            .Include(i => i.Tenant)
            .Include(i => i.CreatedBy)
            .Include(i => i.CreatedMembers)
                .ThenInclude(m => m.Subject)
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();

        return entities.Select(MapToInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> RevokeInviteAsync(Guid inviteId, Guid tenantId)
    {
        var entity = await _dbContext.MemberInvites
            .Where(i => i.Id == inviteId && i.TenantId == tenantId)
            .FirstOrDefaultAsync();

        if (entity == null)
            return false;

        if (entity.RevokedAt.HasValue)
            return true; // Already revoked

        entity.RevokedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "MemberInviteAudit: {Event} invite_id={InviteId} tenant_id={TenantId}",
            "invite_revoked", inviteId, tenantId);

        return true;
    }

    private static MemberInviteInfo MapToInfo(MemberInviteEntity entity)
    {
        return new MemberInviteInfo(
            entity.Id,
            entity.TenantId,
            entity.Tenant?.DisplayName ?? "",
            entity.CreatedBy?.Name ?? "",
            entity.RoleIds,
            entity.DirectPermissions,
            entity.Label,
            entity.LimitTo24Hours,
            entity.ExpiresAt,
            entity.MaxUses,
            entity.UseCount,
            entity.IsValid,
            entity.IsExpired,
            entity.IsRevoked,
            entity.CreatedAt,
            entity.CreatedMembers
                .Where(m => m.RevokedAt == null)
                .Select(m => new InviteUsageInfo(
                    m.SubjectId,
                    m.Subject?.Name,
                    m.SysCreatedAt))
                .ToList());
    }
}
