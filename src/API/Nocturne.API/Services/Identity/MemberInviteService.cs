using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

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

    private static readonly AcceptMemberInviteResult Exhausted =
        new(false, "exhausted", "This invite has reached its maximum uses.");

    private static readonly AcceptMemberInviteResult AlreadyMember =
        new(false, "already_member", "You are already a member of this tenant.");

    private readonly ITenantMembershipRepository _members;
    private readonly IMemberInviteRepository _invites;
    private readonly ISubjectRepository _subjects;
    private readonly INocturneDbContextFactory _contexts;
    private readonly IJwtService _jwtService;
    private readonly ITenantRoleService _tenantRoleService;
    private readonly BaseDomainOptions _baseDomain;
    private readonly TimeProvider _time;
    private readonly ILogger<MemberInviteService> _logger;

    /// <param name="members">For the roles and memberships an invite names.</param>
    /// <param name="invites">The stored invites.</param>
    /// <param name="subjects">For the name of the member who minted an invite.</param>
    /// <param name="contexts">For the unit of work an accepted invite commits its use and its membership on.</param>
    public MemberInviteService(
        ITenantMembershipRepository members,
        IMemberInviteRepository invites,
        ISubjectRepository subjects,
        INocturneDbContextFactory contexts,
        IJwtService jwtService,
        ITenantRoleService tenantRoleService,
        IOptions<BaseDomainOptions> baseDomainOptions,
        TimeProvider timeProvider,
        ILogger<MemberInviteService> logger)
    {
        _members = members ?? throw new ArgumentNullException(nameof(members));
        _invites = invites ?? throw new ArgumentNullException(nameof(invites));
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _jwtService = jwtService;
        _tenantRoleService = tenantRoleService;
        _baseDomain = baseDomainOptions.Value;
        _time = timeProvider;
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

        var directViolation = Scope.ValidateGrant(directPermissions, granter);
        if (directViolation != null)
            throw new ArgumentException(directViolation.Description);

        var roleGrant = await _tenantRoleService.ValidateRoleGrantAsync(tenantId, roleIds, granter);
        if (!roleGrant.Ok)
            throw new ArgumentException(roleGrant.ErrorDescription);

        // Generate token
        var token = _jwtService.GenerateRefreshToken();
        var tokenHash = HashUtils.Sha256Hex(token);

        var now = _time.GetUtcNow().UtcDateTime;
        var expiresAt = now.AddDays(expiresInDays);
        var inviteId = await _invites.CreateAsync(
            new NewMemberInvite(
                tenantId,
                createdBySubjectId,
                tokenHash,
                roleIds,
                directPermissions,
                label,
                limitTo24Hours,
                expiresAt,
                maxUses,
                now),
            CancellationToken.None);

        _logger.LogInformation(
            "MemberInviteAudit: {Event} invite_id={InviteId} tenant_id={TenantId} role_count={RoleCount} expires_at={ExpiresAt}",
            "invite_created", inviteId, tenantId, roleIds.Count, expiresAt);

        // The join page is served per tenant, so the invite has to point at the tenant's own host.
        // The configured base URL is the instance apex, which in a multi-tenant deployment serves a
        // different site entirely — only the caller knows the host the invite was minted on.
        var origin = (baseUrl ?? _baseDomain.PublicOrigin)?.TrimEnd('/') ?? "";
        var inviteUrl =
            $"{origin}{IMemberInviteService.JoinPath}?{IMemberInviteService.TokenQueryParameter}={token}";

        var createdBy = (await _subjects.GetManyAsync([createdBySubjectId], CancellationToken.None)).SingleOrDefault();

        return new MemberInviteResult(
            inviteId,
            token,
            inviteUrl,
            expiresAt,
            createdBy?.Name ?? "");
    }

    /// <inheritdoc />
    public async Task<MemberInviteInfo?> GetInviteByTokenAsync(string token, Guid tenantId)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var tokenHash = HashUtils.Sha256Hex(token);

        var invite = await _invites.FindByTokenHashAsync(tokenHash, tenantId, CancellationToken.None);

        if (invite == null)
            return null;

        return MapToInfo(invite, _time.GetUtcNow().UtcDateTime, await LoadRolesAsync(tenantId, invite.RoleIds));
    }

    /// <inheritdoc />
    public async Task<AcceptMemberInviteResult> AcceptInviteAsync(
        string token, Guid acceptingSubjectId, Guid tenantId)
    {
        if (string.IsNullOrEmpty(token))
            return new AcceptMemberInviteResult(false, "invalid_token", "Invite token is required.");

        var tokenHash = HashUtils.Sha256Hex(token);

        // Bounded by the tenant the request resolved to, not by the one the token names. The token
        // is the only thing authorizing the join, so a token minted for another tenant must read as
        // unknown here rather than as an invite that happens to point elsewhere.
        var invite = await _invites.FindByTokenHashAsync(tokenHash, tenantId, CancellationToken.None);

        if (invite == null)
            return new AcceptMemberInviteResult(false, "invalid_token", "Invite not found or has been revoked.");

        if (invite.IsExpiredAt(_time.GetUtcNow().UtcDateTime))
            return new AcceptMemberInviteResult(false, "expired", "This invite has expired.");

        if (invite.IsRevoked)
            return new AcceptMemberInviteResult(false, "revoked", "This invite has been revoked.");

        if (invite.IsExhausted)
            return Exhausted;

        if (await _members.IsMemberAsync(invite.TenantId, acceptingSubjectId, CancellationToken.None))
            return AlreadyMember;

        // Filter out deleted roles from the invite
        var validRoleIds = invite.RoleIds.Count > 0
            ? (await _members.ListRolesAsync(invite.TenantId, CancellationToken.None))
                .Where(r => invite.RoleIds.Contains(r.Id))
                .Select(r => r.Id)
                .ToList()
            : [];

        if (validRoleIds.Count == 0 && (invite.DirectPermissions == null || invite.DirectPermissions.Count == 0))
            return new AcceptMemberInviteResult(false, "no_permissions", "All roles from this invite have been deleted and no direct permissions are assigned.");

        // The IsExhausted check above is a fast path on a value read earlier in the request; two
        // concurrent accepts of a single-use invite would both pass it and both join. Only a row
        // still under its cap matches the claim, so the loser claims nothing and is refused. The
        // claim shares the membership's transaction, so a membership that fails to save gives the use back.
        AcceptMemberInviteResult? refusal;
        await using (var unit = await DbUnitOfWork.OpenForTenantAsync(_contexts, invite.TenantId))
        {
            var invites = new MemberInviteRepository(unit);
            var members = new TenantMembershipRepository(unit);
            try
            {
                refusal = await unit.CommitAsync<AcceptMemberInviteResult?>(async token =>
                {
                    if (!await members.AddMemberAsync(
                            new NewTenantMember(
                                invite.TenantId,
                                acceptingSubjectId,
                                validRoleIds,
                                invite.DirectPermissions?.ToList(),
                                invite.Label,
                                LimitTo24Hours: invite.LimitTo24Hours,
                                CreatedFromInviteId: invite.Id),
                            token))
                        return AlreadyMember;

                    if (!await invites.TryClaimUseAsync(invite.Id, token))
                        return Exhausted;

                    await unit.Context.SaveChangesAsync(token);
                    return null;
                });
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                return AlreadyMember;
            }
        }

        if (refusal is not null)
            return refusal;

        var member = await _members.FindMemberAsync(invite.TenantId, acceptingSubjectId, CancellationToken.None)
            ?? throw new InvalidOperationException($"Subject {acceptingSubjectId} did not become a member of tenant {invite.TenantId}.");

        _logger.LogInformation(
            "MemberInviteAudit: {Event} invite_id={InviteId} tenant_id={TenantId} subject_id={SubjectId} member_id={MemberId}",
            "invite_accepted", invite.Id, invite.TenantId, acceptingSubjectId, member.Id);

        return new AcceptMemberInviteResult(true, MembershipId: member.Id);
    }

    /// <inheritdoc />
    public async Task<List<MemberInviteInfo>> GetInvitesForTenantAsync(Guid tenantId)
    {
        var invites = await _invites.GetForTenantAsync(tenantId, CancellationToken.None);

        var roles = await LoadRolesAsync(tenantId, invites.SelectMany(i => i.RoleIds).Distinct().ToList());
        var now = _time.GetUtcNow().UtcDateTime;
        return invites.Select(i => MapToInfo(i, now, roles)).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> RevokeInviteAsync(Guid inviteId, Guid tenantId)
    {
        var revocation = await _invites.RevokeAsync(
            inviteId, tenantId, _time.GetUtcNow().UtcDateTime, CancellationToken.None);

        if (revocation == MemberInviteRevocation.NotFound)
            return false;

        if (revocation == MemberInviteRevocation.Revoked)
        {
            _logger.LogInformation(
                "MemberInviteAudit: {Event} invite_id={InviteId} tenant_id={TenantId}",
                "invite_revoked", inviteId, tenantId);
        }

        return true;
    }

    private async Task<Dictionary<Guid, TenantRoleRecord>> LoadRolesAsync(Guid tenantId, IReadOnlyList<Guid> roleIds)
    {
        if (roleIds.Count == 0)
            return [];

        return (await _members.ListRolesAsync(tenantId, CancellationToken.None))
            .Where(r => roleIds.Contains(r.Id))
            .ToDictionary(r => r.Id);
    }

    private static MemberInviteInfo MapToInfo(
        MemberInviteRecord invite, DateTime now, IReadOnlyDictionary<Guid, TenantRoleRecord> roles)
    {
        // A deleted role grants nothing on acceptance, so it is not described either.
        var grantedRoles = invite.RoleIds
            .Where(roles.ContainsKey)
            .Select(id => roles[id])
            .ToList();

        var permissions = grantedRoles
            .SelectMany(r => r.Permissions)
            .Union(invite.DirectPermissions ?? [])
            .ToList();

        return new MemberInviteInfo(
            invite.Id,
            invite.TenantId,
            invite.TenantDisplayName ?? "",
            invite.CreatedByName ?? "",
            [.. invite.RoleIds],
            invite.DirectPermissions?.ToList(),
            invite.Label,
            invite.LimitTo24Hours,
            invite.ExpiresAt,
            invite.MaxUses,
            invite.UseCount,
            invite.IsValidAt(now),
            invite.IsExpiredAt(now),
            invite.IsRevoked,
            invite.CreatedAt,
            invite.Uses
                .Select(u => new InviteUsageInfo(u.SubjectId, u.SubjectName, u.JoinedAt))
                .ToList(),
            grantedRoles.Select(r => r.Name).ToList(),
            permissions);
    }
}
