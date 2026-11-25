using Nocturne.API.Multitenancy;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.SetupHub;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Creates, resolves, updates and deletes tenants, and holds the rules of their membership:
/// slug validation, default role seeding, the Public membership every tenant carries, and the
/// last-owner protection on removal.
/// </summary>
/// <remarks>
/// Slugs must match a <c>[a-z0-9][a-z0-9\-]{1,61}[a-z0-9]</c> pattern and cannot be reserved words.
/// Tenant lookups by slug are cached with a short TTL to reduce database round-trips on every request.
/// </remarks>
/// <seealso cref="ITenantService"/>
/// <seealso cref="ITenantRoleService"/>
/// <seealso cref="AuthorizationSeedService"/>
public partial class TenantService : ITenantService
{
    private readonly INocturneDbContextFactory _factory;
    private readonly ITenantRepository _tenants;
    private readonly ITenantMembershipRepository _members;
    private readonly ISubjectRepository _subjects;
    private readonly IMemoryCache _cache;
    private readonly OperatorConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITenantRoleService _roleService;
    private readonly GrantRevocationService _grantRevocation;
    private readonly TeamStaffAnnouncer _teamStaff;
    private readonly TimeProvider _time;
    private readonly ILogger<TenantService> _logger;
    private readonly IAccountLinkRoute _linkRoute;

    private static readonly HashSet<string> ReservedSlugs =
    [
        // Infrastructure
        "api", "app", "www", "cdn", "assets", "static", "gateway",
        "staging", "prod", "dev", "demo", "preview", "beta",
        // Email
        "mail", "smtp", "webmail", "autoconfig", "autodiscover", "postmaster",
        // Auth/Security
        "auth", "login", "signup", "sso", "oauth", "identity",
        // Admin
        "admin", "dashboard", "console", "manage", "internal", "platform", "system",
        // Public
        "docs", "help", "support", "status", "blog", "legal",
        // DNS
        "ns", "ns1", "ns2", "ftp", "localhost", "dns",
        // Abuse vectors
        "account", "billing", "security", "password", "verify",
        // Platform
        "monitoring", "alerts", "bridge", "connect", "share",
        // Reserved words
        "null", "undefined", "default", "test", "example",
        // Healthcare
        "nightscout", "cgm", "patient", "clinic", "provider",
    ];

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{1,61}[a-z0-9]$")]
    private static partial Regex SlugPattern();

    /// <param name="factory">For the units of work that provision a tenant or remove a member in one transaction.</param>
    /// <param name="tenants">Reads and writes tenant records.</param>
    /// <param name="members">Reads and writes memberships and looks up the roles they carry.</param>
    /// <param name="subjects">Finds the Public system subject a new tenant's public membership is for.</param>
    /// <param name="cache">In-memory cache for caching resolved tenant contexts by slug.</param>
    /// <param name="config">Operator configuration (self-service creation, webhooks, etc.).</param>
    /// <param name="httpClientFactory">HTTP client factory for external tenant validation calls if needed.</param>
    /// <param name="roleService">Role service for seeding default roles on new tenant creation.</param>
    /// <param name="grantRevocation">Revokes a removed member's grants and evicts their sessions.</param>
    /// <param name="timeProvider">Clock for the handover, passkey and OIDC identity timestamps.</param>
    /// <param name="logger">Logs a tenant left without its public membership.</param>
    /// <param name="teamStaff">Recounts the team staff who wait at a site once a member becomes or stops being its owner.</param>
    public TenantService(
        INocturneDbContextFactory factory,
        ITenantRepository tenants,
        ITenantMembershipRepository members,
        ISubjectRepository subjects,
        IMemoryCache cache,
        IOptions<OperatorConfiguration> config,
        IHttpClientFactory httpClientFactory,
        ITenantRoleService roleService,
        GrantRevocationService grantRevocation,
        TimeProvider timeProvider,
        ILogger<TenantService> logger,
        IAccountLinkRoute linkRoute,
        TeamStaffAnnouncer teamStaff)
    {
        _factory = factory;
        _tenants = tenants;
        _members = members;
        _subjects = subjects;
        _cache = cache;
        _config = config.Value;
        _httpClientFactory = httpClientFactory;
        _roleService = roleService;
        _grantRevocation = grantRevocation;
        _time = timeProvider;
        _logger = logger;
        _linkRoute = linkRoute;
        _teamStaff = teamStaff;
    }

    public async Task<TenantCreatedDto> CreateAsync(
        string slug, string displayName, Guid creatorSubjectId, CancellationToken ct = default)
    {
        var created = await CreateWithoutOwnerAsync(slug, displayName, ct);

        var ownerRoleId = await _members.FindRoleIdBySlugAsync(created.Id, RoleSeeds.Owner, ct)
            ?? throw new InvalidOperationException($"Tenant {created.Id} has no owner role after seeding.");
        if (!await _members.AddMemberAsync(
                new NewTenantMember(created.Id, creatorSubjectId, [ownerRoleId]) { How = MembershipJoinHow.Setup, By = null }, ct))
            throw new InvalidOperationException($"Subject {creatorSubjectId} is already a member of the new tenant {created.Id}.");

        return created;
    }

    public async Task<TenantCreatedDto> CreateWithOwnerAsync(
        string slug, string displayName, Guid ownerSubjectId, string? ownerUsername = null, CancellationToken ct = default)
    {
        await using var unit = await DbUnitOfWork.OpenPlatformAsync(_factory, ct);
        var tenants = new TenantRepository(unit);
        var members = new TenantMembershipRepository(unit, _linkRoute);
        var tenantId = await unit.CommitAsync(async token =>
        {
            await unit.LockSubjectRowSharedAsync(ownerSubjectId, token);
            var id = await CreateSeededTenantAsync(unit, tenants, members, slug, displayName, token);

            var ownerRoleId = await members.FindRoleIdBySlugAsync(id, RoleSeeds.Owner, token)
                ?? throw new InvalidOperationException($"Tenant {id} has no owner role after seeding.");
            await members.AddMemberAsync(
                new NewTenantMember(
                    id, ownerSubjectId, [ownerRoleId], Username: ownerUsername, MadeBy: MembershipMadeBy.Member)
                    { How = MembershipJoinHow.Setup, By = null },
                token);
            await unit.Context.SaveChangesAsync(token);

            return id;
        }, ct: ct);

        var tenant = (await _tenants.GetAsync(tenantId, ct))!;
        TenantCacheEviction.Evict(_cache, tenant.Slug, tenant.ShareTokenHash);
        return new TenantCreatedDto(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, tenant.SysCreatedAt);
    }

    public async Task<TenantCreatedDto> CreateWithoutOwnerAsync(
        string slug, string displayName, CancellationToken ct = default)
    {
        var id = await _tenants.CreateAsync(new NewTenant(slug.ToLowerInvariant(), displayName), ct);
        var tenant = (await _tenants.GetAsync(id, ct))!;

        await _roleService.SeedRolesForTenantAsync(tenant.Id, ct);
        await CreatePublicSubjectMembershipAsync(_members, tenant.Id, ct);

        TenantCacheEviction.Evict(_cache, tenant.Slug, tenant.ShareTokenHash);
        return new TenantCreatedDto(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, tenant.SysCreatedAt);
    }

    /// <summary>
    /// Re-seeds roles and public membership for an existing
    /// tenant after its data has been purged. Used by the dev-only reset flow.
    /// </summary>
    public async Task SeedAfterResetAsync(Guid tenantId, CancellationToken ct = default)
    {
        await _roleService.SeedRolesForTenantAsync(tenantId, ct);
        await CreatePublicSubjectMembershipAsync(_members, tenantId, ct);
    }

    public async Task<List<TenantDto>> GetAllAsync(CancellationToken ct = default) =>
        (await _tenants.ListAsync(activeOnly: false, ct)).Select(ToDto).ToList();

    public async Task<TenantDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var tenant = await _tenants.GetAsync(id, ct);
        if (tenant == null) return null;

        // The member list this returns is an authorization primitive
        // (TenantController.IsCallerTenantOwnerAsync reads it), so the repository pins the read to
        // the tenant asked about rather than whatever tenant the caller's context carries.
        var members = await _members.ListMembersAsync(id, ct);
        var now = _time.GetUtcNow().UtcDateTime;

        return new TenantDetailDto(
            tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, tenant.SysCreatedAt,
            members
                .Select(m => new TenantMemberDto(
                    m.Id,
                    m.SubjectId,
                    m.SubjectName,
                    m.IsSystemSubject,
                    m.Roles.Select(r => new TenantMemberRoleDto(r.RoleId, r.Name, r.Slug, [.. r.Permissions])).ToList(),
                    m.DirectPermissions?.ToList(),
                    m.Label,
                    m.LimitTo24Hours,
                    m.LastUsedAt,
                    m.SysCreatedAt,
                    m.IsPlatformAdmin,
                    m.AccessEndsAt,
                    MembershipLiveness.HasEnded(m.AccessEndsAt, now),
                    _linkRoute.IsConfigured && !m.IsSystemSubject && !m.IsDemoSubject ? m.HasConfirmedEmail : null,
                    m.DirectReason is not null,
                    ReasonsOf(m, now),
                    DirectLiftsTeamLimit(m, now)))
                .ToList());
    }

    /// <summary>The reasons <paramref name="member"/> holds, leaving out a team reason whose access is not of the member's tenant.</summary>
    private static List<TenantMemberReasonDto> ReasonsOf(TenantMemberRecord member, DateTime now) =>
        member.HeldReasons
            .Where(r => r.Kind == MembershipReasonKind.Direct || r.Team is not null)
            .Select(r => new TenantMemberReasonDto(
                r.Kind == MembershipReasonKind.Direct ? MemberReasonKind.Direct : MemberReasonKind.Team,
                r.Team?.Id,
                r.Team?.Name,
                r.Roles.Select(role => new TenantMemberRoleDto(role.RoleId, role.Name, role.Slug, [.. role.Permissions])).ToList(),
                r.DirectPermissions?.ToList(),
                r.LimitTo24Hours,
                r.AccessEndsAt,
                MembershipLiveness.HasEnded(r.AccessEndsAt, now)))
            .ToList();

    /// <summary>
    /// Whether the member's direct reason, in force, gives full history while a team reason in force
    /// limits them to the last 24 hours.
    /// </summary>
    private static bool DirectLiftsTeamLimit(TenantMemberRecord member, DateTime now) =>
        member.DirectReason is { LimitTo24Hours: false } direct
        && !MembershipLiveness.HasEnded(direct.AccessEndsAt, now)
        && member.HeldReasons.Any(r =>
            r.Kind == MembershipReasonKind.Team && r.LimitTo24Hours && !MembershipLiveness.HasEnded(r.AccessEndsAt, now));

    public async Task<TenantDto> UpdateAsync(
        Guid id, string displayName, bool isActive, bool? allowAccessRequests = null, CancellationToken ct = default)
    {
        var tenant = await RequireAsync(id, ct);
        await _tenants.UpdateAsync(id, new TenantEdit(DisplayName: displayName, IsActive: isActive, AllowAccessRequests: allowAccessRequests), ct);

        TenantCacheEviction.Evict(_cache, tenant.Slug, tenant.ShareTokenHash);

        return ToDto((await _tenants.GetAsync(id, ct))!);
    }

    public async Task<TenantSettingsDto> GetSettingsAsync(Guid id, CancellationToken ct = default) =>
        ToSettingsDto(await RequireAsync(id, ct));

    public async Task<TenantSettingsDto> SetAllowPublicDocsAsync(
        Guid id, bool allowPublicDocs, CancellationToken ct = default)
    {
        var tenant = await RequireAsync(id, ct);
        await _tenants.UpdateAsync(id, new TenantEdit(AllowPublicDocs: allowPublicDocs), ct);

        TenantCacheEviction.Evict(_cache, tenant.Slug, tenant.ShareTokenHash);

        return ToSettingsDto(tenant with { AllowPublicDocs = allowPublicDocs });
    }

    public async Task<TenantSettingsDto> SetDisplayNameAsync(
        Guid id, string displayName, CancellationToken ct = default)
    {
        var tenant = await RequireAsync(id, ct);
        await _tenants.UpdateAsync(id, new TenantEdit(DisplayName: displayName), ct);

        TenantCacheEviction.Evict(_cache, tenant.Slug, tenant.ShareTokenHash);

        return ToSettingsDto(tenant with { DisplayName = displayName });
    }

    public async Task<SlugValidationResult> ChangeSlugAsync(
        Guid id, string newSlug, CancellationToken ct = default)
    {
        var tenant = await RequireAsync(id, ct);
        var normalized = newSlug.ToLowerInvariant().Trim();

        if (normalized == tenant.Slug)
            return new SlugValidationResult(false, "This is already the tenant's subdomain");

        if (NextSlugChangeAt(tenant) is not null)
            return new SlugValidationResult(false, "You can change the address one time each hour. Try again later.");

        var validation = await ValidateSlugAsync(normalized, ct);
        if (!validation.IsValid)
            return validation;

        try
        {
            await _tenants.UpdateAsync(
                id, new TenantEdit(Slug: normalized, SlugChangedAt: _time.GetUtcNow().UtcDateTime), ct);
        }
        catch (DbUpdateException)
        {
            if (!await _tenants.SlugExistsAsync(normalized, CancellationToken.None))
                throw;

            return new SlugValidationResult(false, "This name is already taken");
        }

        TenantCacheEviction.Evict(_cache, tenant.Slug, tenant.ShareTokenHash);
        TenantCacheEviction.Evict(_cache, normalized);

        _logger.LogInformation(
            "Tenant {TenantId} moved from subdomain {OldSlug} to {NewSlug}", id, tenant.Slug, normalized);

        return new SlugValidationResult(true);
    }

    public async Task<PatientRelationship?> GetPatientRelationshipAsync(Guid id, CancellationToken ct = default) =>
        (await RequireAsync(id, ct)).PatientRelationship;

    public async Task SetPatientRelationshipAsync(
        Guid id, PatientRelationship relationship, Guid? callerId, CancellationToken ct = default)
    {
        var tenant = await RequireAsync(id, ct);

        DateTime? handedOverAt = null;
        if (tenant.PatientRelationship == PatientRelationship.Helper
            && relationship != PatientRelationship.Helper
            && tenant.HandedOverAt is null
            && callerId is { } caller
            && await Handover.HelperAsync(_members, id, ct) is { } helper
            && helper.SubjectId != caller)
            handedOverAt = _time.GetUtcNow().UtcDateTime;

        await _tenants.UpdateAsync(id, new TenantEdit(PatientRelationship: relationship, HandedOverAt: handedOverAt), ct);
    }

    public async Task<string?> GetDefaultGlucoseUnitsAsync(Guid id, CancellationToken ct = default) =>
        (await _tenants.GetAsync(id, ct))?.DefaultGlucoseUnits;

    public async Task SetDefaultGlucoseUnitsAsync(Guid id, string units, CancellationToken ct = default)
    {
        await RequireAsync(id, ct);
        await _tenants.UpdateAsync(id, new TenantEdit(DefaultGlucoseUnits: units), ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var tenant = await RequireAsync(id, ct);
        await _tenants.DeleteAsync(id, ct);

        TenantCacheEviction.Evict(_cache, tenant.Slug, tenant.ShareTokenHash);
    }

    public async Task<bool> AddMemberAsync(
        Guid tenantId, Guid subjectId, List<Guid> roleIds, List<string>? directPermissions = null,
        string? label = null, bool limitTo24Hours = false, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(_factory, tenantId, ct);
        var members = new TenantMembershipRepository(unit, _linkRoute);
        var (added, lost) = await unit.CommitAsync<(bool, IReadOnlyList<OAuthGrantRecord>)>(
            async token =>
            {
                await TenantMembershipRepository.LockOwnerChangeAsync(unit, tenantId, subjectId, token);
                if (!await members.AddDirectMemberAsync(
                        new NewTenantMember(tenantId, subjectId, roleIds, directPermissions, label, LimitTo24Hours: limitTo24Hours)
                        { How = MembershipJoinHow.Direct, By = null },
                        now,
                        token))
                    return (false, []);

                await unit.Context.SaveChangesAsync(token);
                return (true, await TeamReasonWrites.SyncOwnerChangeAsync(unit, _grantRevocation, tenantId, subjectId, now, token));
            },
            ct: ct);

        _grantRevocation.EvictSessions(lost);
        if (added)
            await _teamStaff.RecountOwnerChangeAsync(tenantId, subjectId);
        return added;
    }

    public async Task<MemberRemovalResult> RemoveMemberAsync(
        Guid tenantId, Guid subjectId, CancellationToken ct = default)
    {
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(_factory, tenantId, ct);
        var members = new TenantMembershipRepository(unit, _linkRoute);
        var member = await members.FindMemberAsync(tenantId, subjectId, ct);

        // Already absent is the caller's desired end state, not a refusal.
        if (member == null)
            return new MemberRemovalResult(true);

        // The Public subject serves the anonymous share viewer; its membership is the share's
        // storage, managed through the public access card rather than the member list.
        if (member.IsSystemSubject)
            return new MemberRemovalResult(false, "Cannot remove system subject memberships");

        var now = _time.GetUtcNow().UtcDateTime;
        var (outcome, grants) = await unit.CommitAsync<(OwnerGuardedChange, IReadOnlyList<OAuthGrantRecord>)>(
            async token =>
            {
                await TenantMembershipRepository.LockOwnerChangeAsync(unit, tenantId, subjectId, token);
                var (change, lost) = await MembershipRemoval.StageAsync(
                    unit, members, _grantRevocation, tenantId, member, MembershipEndHow.Removed, now, token);
                return change == OwnerGuardedChange.Changed
                    ? (change, [.. lost, .. await TeamReasonWrites.SyncOwnerChangeAsync(unit, _grantRevocation, tenantId, subjectId, now, token)])
                    : (change, lost);
            },
            ct: ct);

        if (outcome == OwnerGuardedChange.LastOwner)
            return new MemberRemovalResult(false, TenantOwnerFilter.LastOwnerRefusal);
        if (outcome == OwnerGuardedChange.FromTeam)
            return new MemberRemovalResult(false, MemberAccessFromTeam.Detail) { AccessFromTeam = true };

        _grantRevocation.EvictSessions(grants);
        await _teamStaff.RecountOwnerChangeAsync(tenantId, subjectId);
        return new MemberRemovalResult(true);
    }

    public async Task<OwnerGuardedChange> SetMemberAccessEndAsync(
        Guid tenantId, Guid memberId, DateTime? accessEndsAt, DateTime now, CancellationToken ct = default)
    {
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(_factory, tenantId, ct);
        var members = new TenantMembershipRepository(unit, _linkRoute);
        Guid? changedSubject = null;
        var (outcome, capped) = await unit.CommitAsync<(OwnerGuardedChange, IReadOnlyList<OAuthGrantRecord>)>(
            async token =>
            {
                IReadOnlyList<OAuthGrantRecord> lowered = [];
                if (await members.GetMemberAsync(tenantId, memberId, token) is not { } target)
                    return (OwnerGuardedChange.NotFound, lowered);

                changedSubject = target.SubjectId;

                await TenantMembershipRepository.LockOwnerChangeAsync(unit, tenantId, target.SubjectId, token);
                var change = await members.SetAccessEndUnlessOwnerAsync(tenantId, memberId, accessEndsAt, now, token);
                if (change != OwnerGuardedChange.Changed)
                    return (change, lowered);

                await unit.Context.SaveChangesAsync(token);
                if (accessEndsAt is not null
                    && await members.GetMemberAsync(tenantId, memberId, token) is { AccessEndsAt: { } end })
                {
                    lowered = await MemberCreations.CapAsync(unit, target.SubjectId, end, now, token);
                    await unit.Context.SaveChangesAsync(token);
                }
                return (change, [.. lowered, .. await TeamReasonWrites.SyncOwnerChangeAsync(unit, _grantRevocation, tenantId, target.SubjectId, now, token)]);
            },
            ct: ct);

        _grantRevocation.EvictSessions(capped);
        if (outcome == OwnerGuardedChange.Changed && changedSubject is { } ended)
            await _teamStaff.RecountOwnerChangeAsync(tenantId, ended);
        return outcome;
    }

    public async Task<OwnerGuardedChange> ReplaceMemberRolesAsync(
        Guid tenantId, Guid memberId, IReadOnlyCollection<Guid> removedRoleIds, IReadOnlyCollection<Guid> addedRoleIds,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(_factory, tenantId, ct);
        var members = new TenantMembershipRepository(unit, _linkRoute);
        Guid? changedSubject = null;
        var (outcome, lost) = await unit.CommitAsync<(OwnerGuardedChange, IReadOnlyList<OAuthGrantRecord>)>(
            async token =>
            {
                if (await members.GetMemberAsync(tenantId, memberId, token) is not { } target)
                    return (OwnerGuardedChange.NotFound, []);

                changedSubject = target.SubjectId;

                await TenantMembershipRepository.LockOwnerChangeAsync(unit, tenantId, target.SubjectId, token);
                var change = await members.ReplaceRolesUnlessLastOwnerAsync(tenantId, memberId, removedRoleIds, addedRoleIds, now, token);
                if (change != OwnerGuardedChange.Changed)
                    return (change, []);

                await unit.Context.SaveChangesAsync(token);
                return (change, await TeamReasonWrites.SyncOwnerChangeAsync(unit, _grantRevocation, tenantId, target.SubjectId, now, token));
            },
            ct: ct);

        _grantRevocation.EvictSessions(lost);
        if (outcome == OwnerGuardedChange.Changed && changedSubject is { } replaced)
            await _teamStaff.RecountOwnerChangeAsync(tenantId, replaced);
        return outcome;
    }

    public async Task<List<TenantDto>> GetTenantsForSubjectAsync(
        Guid subjectId, CancellationToken ct = default) =>
        (await _members.ListTenantsOfSubjectAsync(subjectId, activeOnly: false, _time.GetUtcNow().UtcDateTime, ct)).Select(ToDto).ToList();

    public async Task<List<TenantDto>> GetReachableTenantsForSubjectAsync(
        Guid subjectId, CancellationToken ct = default) =>
        (await _members.ListTenantsOfSubjectAsync(subjectId, activeOnly: true, _time.GetUtcNow().UtcDateTime, ct)).Select(ToDto).ToList();

    public Task<SlugValidationResult> ValidateSlugAsync(string slug, CancellationToken ct = default) =>
        ValidateSlugAsync(slug, allowReserved: false, ct);

    public async Task<SlugValidationResult> ValidateSlugAsync(
        string slug, bool allowReserved, CancellationToken ct = default)
    {
        var normalized = slug.ToLowerInvariant().Trim();

        if (!SlugPattern().IsMatch(normalized))
            return new SlugValidationResult(false, "Slug must be 3-63 characters, alphanumeric and hyphens only, no leading/trailing hyphens");

        if (!allowReserved && ReservedSlugs.Contains(normalized))
            return new SlugValidationResult(false, "This name is reserved");

        if (await _tenants.SlugExistsAsync(normalized, ct))
            return new SlugValidationResult(false, "This name is already taken");

        if (!string.IsNullOrEmpty(_config.SlugValidationWebhookUrl))
        {
            var answer = await OperatorWebhook.PostAsync<SlugValidationResult>(
                _httpClientFactory.CreateClient(OperatorWebhook.SlugValidationClient),
                _config.SlugValidationWebhookUrl,
                new { slug = normalized },
                ct);

            if (answer.Value is { IsValid: false } rejection)
                return rejection;
        }

        return new SlugValidationResult(true);
    }

    public async Task<ProvisionResult> ProvisionWithOwnerAsync(
        string slug, string displayName, string ownerUsername, string ownerEmail,
        ProvisionCredentialData? credential, ProvisionOidcIdentityData? oidcIdentity,
        CancellationToken ct = default)
    {
        // One unit: the owner, its credential and its membership land with the tenant or not at all.
        await using var unit = await DbUnitOfWork.OpenPlatformAsync(_factory, ct);
        var context = unit.Context;
        var tenants = new TenantRepository(unit);
        var members = new TenantMembershipRepository(unit, _linkRoute);
        var providers = new OidcProviderRepository(unit);
        var subjects = new SubjectRepository(unit, _linkRoute);
        var passkeys = new PasskeyCredentialRepository(unit);
        var provisioned = await unit.CommitAsync(async token =>
        {
            // Resolve the owner subject. An OIDC account maps to one subject reused across
            // tenants, so an already-linked identity names the owner even when the checkout email
            // differs. Nothing else does: the owner email is never matched to an existing subject,
            // since a subject's email proves nothing about who holds the new credential.
            var normalizedIssuer = oidcIdentity?.Issuer.TrimEnd('/');
            var existingIdentity = oidcIdentity is null
                ? null
                : await subjects.FindByOidcIdentityAsync(oidcIdentity.OidcSubjectId, normalizedIssuer!, token);
            if (existingIdentity is not null)
                await unit.LockSubjectRowSharedAsync(existingIdentity.Subject.Id, token);

            var tenantId = await CreateSeededTenantAsync(unit, tenants, members, slug, displayName, token);

            Guid ownerSubjectId;
            if (existingIdentity is null)
            {
                ownerSubjectId = credential?.SubjectId ?? oidcIdentity?.SubjectId ?? Guid.CreateVersion7();
                if (await subjects.GetAsync(ownerSubjectId, token) is not null)
                {
                    throw new ProvisionConflictException(
                        $"Subject {ownerSubjectId} already exists; a provisioned owner is always a new subject.");
                }

                await subjects.CreateAsync(
                    new NewSubject(
                        ownerUsername,
                        Username: ownerUsername.ToLowerInvariant(),
                        Email: ownerEmail,
                        Id: ownerSubjectId),
                    token);
                await context.SaveChangesAsync(token);
            }
            else
            {
                ownerSubjectId = existingIdentity.Subject.Id;
            }

            if (credential is not null)
            {
                await passkeys.CreateAsync(
                    new NewPasskeyCredential(
                        ownerSubjectId,
                        Convert.FromBase64String(credential.CredentialId),
                        Convert.FromBase64String(credential.PublicKey),
                        credential.SignCount,
                        credential.Transports,
                        Label: null,
                        credential.AaGuid,
                        _time.GetUtcNow().UtcDateTime),
                    token);
            }
            else if (oidcIdentity is not null)
            {
                if (existingIdentity is null)
                {
                    // A config-managed provider has a deterministic id but may have no row yet;
                    // the identity's foreign key needs one. Its client id is populated by the
                    // config sync on the next startup.
                    var providerId = (await providers.FindByIssuerAsync(normalizedIssuer!, token))?.Id;
                    if (providerId is null)
                    {
                        providerId = await providers.CreateAsync(
                            new NewOidcProvider(
                                oidcIdentity.Provider,
                                ProviderType: "oidc",
                                normalizedIssuer!,
                                ClientId: string.Empty,
                                Id: OidcProviderService.CreateDeterministicGuid(normalizedIssuer!)),
                            token);
                        await context.SaveChangesAsync(token);
                    }

                    await subjects.AddOidcIdentityAsync(
                        new NewSubjectOidcIdentity(
                            ownerSubjectId,
                            providerId.Value,
                            oidcIdentity.OidcSubjectId,
                            normalizedIssuer!,
                            oidcIdentity.Email,
                            _time.GetUtcNow().UtcDateTime),
                        token);
                }
                else
                {
                    // The identity already links to the owner subject resolved above; record the reuse.
                    await subjects.TouchOidcIdentityAsync(
                        existingIdentity.Identity.Id, _time.GetUtcNow().UtcDateTime, email: null, token);
                }
            }
            await context.SaveChangesAsync(token);

            var ownerRoleId = await members.FindRoleIdBySlugAsync(tenantId, RoleSeeds.Owner, token)
                ?? throw new InvalidOperationException($"Tenant {tenantId} has no owner role after seeding.");
            await members.AddMemberAsync(
                new NewTenantMember(tenantId, ownerSubjectId, [ownerRoleId]) { How = MembershipJoinHow.Setup, By = null }, token);
            await context.SaveChangesAsync(token);

            return new ProvisionResult(tenantId, ownerSubjectId, slug.ToLowerInvariant());
        }, ct: ct);

        TenantCacheEviction.Evict(_cache, provisioned.Slug);
        return provisioned;
    }

    /// <summary>
    /// Creates the tenant row, its seeded roles and its Public membership on
    /// <paramref name="unit"/>, inside the caller's transaction, and returns the tenant's id.
    /// </summary>
    private async Task<Guid> CreateSeededTenantAsync(
        DbUnitOfWork unit, TenantRepository tenants, TenantMembershipRepository members,
        string slug, string displayName, CancellationToken token)
    {
        var context = unit.Context;
        var tenantId = await tenants.CreateAsync(new NewTenant(slug.ToLowerInvariant(), displayName), token);
        await context.SaveChangesAsync(token);

        // The tenant's id exists only once the row is saved, and the connection is already
        // open, so the TenantConnectionInterceptor will not fire again: the GUC is set here
        // for the tenant-scoped writes that follow.
        await context.PinTenantAsync(tenantId, token);

        await members.SeedRolesAsync(tenantId, token);
        await context.SaveChangesAsync(token);

        await CreatePublicSubjectMembershipAsync(members, tenantId, token);
        await context.SaveChangesAsync(token);

        return tenantId;
    }

    /// <summary>
    /// The Public membership every tenant carries, with no roles, so the tenant is private until
    /// its owner opens a share.
    /// </summary>
    private async Task CreatePublicSubjectMembershipAsync(
        ITenantMembershipRepository members, Guid tenantId, CancellationToken ct)
    {
        var publicSubject = await _subjects.FindSystemSubjectAsync(PublicSubjectFilter.PublicSubjectName, ct);

        if (publicSubject != null)
        {
            await members.AddMemberAsync(
                new NewTenantMember(
                    tenantId, publicSubject.Id, [], Label: "Public Access", LimitTo24Hours: true,
                    MadeBy: MembershipMadeBy.System) { How = MembershipJoinHow.System, By = null },
                ct);
        }
        else
        {
            _logger.LogWarning("Public system subject not found — skipping public access membership for tenant {TenantId}", tenantId);
        }
    }

    private async Task<TenantRecord> RequireAsync(Guid id, CancellationToken ct) =>
        await _tenants.GetAsync(id, ct) ?? throw new KeyNotFoundException($"Tenant {id} not found");

    private static TenantDto ToDto(TenantRecord t) =>
        new(t.Id, t.Slug, t.DisplayName, t.IsActive, t.SysCreatedAt);

    private TenantSettingsDto ToSettingsDto(TenantRecord t) =>
        new(t.AllowPublicDocs, t.Slug, t.DisplayName, t.SysCreatedAt, NextSlugChangeAt(t));

    /// <summary>
    /// When <paramref name="tenant"/>'s subdomain may next change, or null when it may change now.
    /// </summary>
    private DateTime? NextSlugChangeAt(TenantRecord tenant) =>
        tenant.SlugChangedAt is { } changedAt
        && changedAt + ITenantService.SlugChangeInterval is var next
        && next > _time.GetUtcNow().UtcDateTime
            ? next
            : null;
}
