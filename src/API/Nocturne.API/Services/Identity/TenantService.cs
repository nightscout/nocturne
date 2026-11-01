using Nocturne.API.Multitenancy;
using System.Net.Http.Json;
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
    private readonly TimeProvider _time;
    private readonly ILogger<TenantService> _logger;

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
    /// <param name="cache">In-memory cache for caching resolved tenant contexts by slug.</param>
    /// <param name="config">Operator configuration (self-service creation, webhooks, etc.).</param>
    /// <param name="httpClientFactory">HTTP client factory for external tenant validation calls if needed.</param>
    /// <param name="roleService">Role service for seeding default roles on new tenant creation.</param>
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
        ILogger<TenantService> logger)
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
    }

    public async Task<TenantCreatedDto> CreateAsync(
        string slug, string displayName, Guid creatorSubjectId, CancellationToken ct = default)
    {
        var created = await CreateWithoutOwnerAsync(slug, displayName, ct);

        var ownerRoleId = await _members.FindRoleIdBySlugAsync(created.Id, RoleSeeds.Owner, ct)
            ?? throw new InvalidOperationException($"Tenant {created.Id} has no owner role after seeding.");
        if (!await AddMemberAsync(created.Id, creatorSubjectId, [ownerRoleId], ct: ct))
            throw new InvalidOperationException($"Subject {creatorSubjectId} is already a member of the new tenant {created.Id}.");

        return created;
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
                    m.IsPlatformAdmin))
                .ToList());
    }

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

    public Task<bool> AddMemberAsync(
        Guid tenantId, Guid subjectId, List<Guid> roleIds, List<string>? directPermissions = null,
        string? label = null, bool limitTo24Hours = false, CancellationToken ct = default) =>
        _members.AddMemberAsync(
            new NewTenantMember(tenantId, subjectId, roleIds, directPermissions, label, LimitTo24Hours: limitTo24Hours), ct);

    public async Task<MemberRemovalResult> RemoveMemberAsync(
        Guid tenantId, Guid subjectId, CancellationToken ct = default)
    {
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(_factory, tenantId, ct);
        var context = unit.Context;
        var members = new TenantMembershipRepository(unit);
        var member = await members.FindMemberAsync(tenantId, subjectId, ct);

        // Already absent is the caller's desired end state, not a refusal.
        if (member == null)
            return new MemberRemovalResult(true);

        // The Public subject serves the anonymous share viewer; its membership is the share's
        // storage, managed through the public access card rather than the member list.
        if (member.IsSystemSubject)
            return new MemberRemovalResult(false, "Cannot remove system subject memberships");

        var (outcome, grants) = await unit.CommitAsync<(OwnerGuardedChange, IReadOnlyList<OAuthGrantRecord>)>(
            async token =>
            {
                var removal = await members.RemoveMemberUnlessLastOwnerAsync(tenantId, member.Id, token);
                if (removal != OwnerGuardedChange.Changed)
                    return (removal, []);

                // Chat identity directory rows are global and carry no membership join, so a link left
                // behind here would keep answering bot commands for this tenant. It goes in the same
                // SaveChanges as the membership so the two cannot come apart, and is scoped to this
                // tenant: the same subject's links to tenants they still belong to must survive.
                var chatLinks = await context.ChatIdentityDirectory
                    .Where(d => d.TenantId == tenantId && d.NocturneUserId == subjectId)
                    .ToListAsync(token);

                // Grants carry no membership join either; left live, every one would work again the
                // moment the subject is re-added. They are staged on the unit with the removal, so one
                // commit lands both.
                var active = await new OAuthGrantRepository(unit).GetActiveForSubjectAsync(subjectId, token);

                context.ChatIdentityDirectory.RemoveRange(chatLinks);
                await _grantRevocation.StageAsync(unit, active, token);
                await context.SaveChangesAsync(token);
                return (removal, active);
            },
            ct: ct);

        if (outcome == OwnerGuardedChange.LastOwner)
            return new MemberRemovalResult(false, TenantOwnerFilter.LastOwnerRefusal);

        _grantRevocation.EvictSessions(grants);
        return new MemberRemovalResult(true);
    }

    public async Task<List<TenantDto>> GetTenantsForSubjectAsync(
        Guid subjectId, CancellationToken ct = default) =>
        (await _members.ListTenantsOfSubjectAsync(subjectId, activeOnly: false, ct)).Select(ToDto).ToList();

    public async Task<List<TenantDto>> GetReachableTenantsForSubjectAsync(
        Guid subjectId, CancellationToken ct = default) =>
        (await _members.ListTenantsOfSubjectAsync(subjectId, activeOnly: true, ct)).Select(ToDto).ToList();

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
            try
            {
                var client = _httpClientFactory.CreateClient("slug-validation");
                var response = await client.PostAsJsonAsync(
                    _config.SlugValidationWebhookUrl,
                    new { slug = normalized },
                    ct);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<SlugValidationResult>(ct);
                    if (result is { IsValid: false })
                        return result;
                }
            }
            catch
            {
                // Webhook failure should not block validation — fall through to success
            }
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
        var members = new TenantMembershipRepository(unit);
        var providers = new OidcProviderRepository(unit);
        var subjects = new SubjectRepository(unit);
        var passkeys = new PasskeyCredentialRepository(unit);
        var provisioned = await unit.CommitAsync(async token =>
        {
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

            // Resolve the owner subject. An OIDC account maps to one subject reused across
            // tenants, so an already-linked identity names the owner even when the checkout email
            // differs. Nothing else does: the owner email is never matched to an existing subject,
            // since a subject's email proves nothing about who holds the new credential.
            var normalizedIssuer = oidcIdentity?.Issuer.TrimEnd('/');
            var existingIdentity = oidcIdentity is null
                ? null
                : await subjects.FindByOidcIdentityAsync(oidcIdentity.OidcSubjectId, normalizedIssuer!, token);

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
            await members.AddMemberAsync(new NewTenantMember(tenantId, ownerSubjectId, [ownerRoleId]), token);
            await context.SaveChangesAsync(token);

            return new ProvisionResult(tenantId, ownerSubjectId, slug.ToLowerInvariant());
        }, ct: ct);

        TenantCacheEviction.Evict(_cache, provisioned.Slug);
        return provisioned;
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
                new NewTenantMember(tenantId, publicSubject.Id, [], Label: "Public Access", LimitTo24Hours: true), ct);
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

    private static TenantSettingsDto ToSettingsDto(TenantRecord t) =>
        new(t.AllowPublicDocs, t.Slug, t.DisplayName, t.SysCreatedAt);
}
