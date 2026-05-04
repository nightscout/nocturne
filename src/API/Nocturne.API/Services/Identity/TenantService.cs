using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Auth;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Service for creating, resolving, updating, and deleting tenants.
/// Manages <see cref="TenantEntity"/> lifecycle including slug validation, API secret hashing,
/// <see cref="ITenantRoleService"/> default role seeding, and per-tenant <see cref="IMemoryCache"/> caching.
/// </summary>
/// <remarks>
/// Slugs must match a <c>[a-z0-9][a-z0-9\-]{1,62}[a-z0-9]</c> pattern and cannot be reserved words.
/// Tenant lookups by slug are cached with a short TTL to reduce database round-trips on every request.
/// </remarks>
/// <seealso cref="ITenantService"/>
/// <seealso cref="ITenantRoleService"/>
/// <seealso cref="AuthorizationSeedService"/>
public partial class TenantService : ITenantService
{
    private readonly IDbContextFactory<NocturneDbContext> _factory;
    private readonly IMemoryCache _cache;
    private readonly OperatorConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITenantRoleService _roleService;
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
        "monitoring", "alerts", "bridge", "connect",
        // Reserved words
        "null", "undefined", "default", "test", "example",
        // Healthcare
        "nightscout", "cgm", "patient", "clinic", "provider",
    ];

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{1,62}[a-z0-9]$")]
    private static partial Regex SlugPattern();

    /// <summary>
    /// Initializes a new instance of <see cref="TenantService"/>.
    /// </summary>
    /// <param name="factory">Factory for creating short-lived <see cref="NocturneDbContext"/> instances.</param>
    /// <param name="cache">In-memory cache for caching resolved tenant contexts by slug.</param>
    /// <param name="config">Operator configuration (self-service creation, webhooks, etc.).</param>
    /// <param name="httpClientFactory">HTTP client factory for external tenant validation calls if needed.</param>
    /// <param name="roleService">Role service for seeding default roles on new tenant creation.</param>
    /// <param name="logger">The logger instance.</param>
    public TenantService(
        IDbContextFactory<NocturneDbContext> factory,
        IMemoryCache cache,
        IOptions<OperatorConfiguration> config,
        IHttpClientFactory httpClientFactory,
        ITenantRoleService roleService,
        ILogger<TenantService> logger)
    {
        _factory = factory;
        _cache = cache;
        _config = config.Value;
        _httpClientFactory = httpClientFactory;
        _roleService = roleService;
        _logger = logger;
    }

    public async Task<TenantCreatedDto> CreateAsync(
        string slug, string displayName, Guid creatorSubjectId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);

        var tenant = new TenantEntity
        {
            Slug = slug.ToLowerInvariant(),
            DisplayName = displayName,
            IsActive = true,
        };

        context.Tenants.Add(tenant);
        await context.SaveChangesAsync(ct);

        // Set the RLS tenant context so subsequent writes to tenant-scoped
        // tables (roles, members, OAuth clients) are permitted. The factory-
        // created context has no TenantId, so the connection interceptor
        // won't set the GUC automatically.
        await SetTenantGuc(context, tenant.Id);

        // Seed default roles for this tenant
        await _roleService.SeedRolesForTenantAsync(tenant.Id, ct);

        // Create Public subject membership (no roles = unconfigured sentinel)
        await CreatePublicSubjectMembershipAsync(context, tenant.Id, ct);

        // Seed bundled known OAuth clients (Trio, xDrip+, etc.)
        await SeedKnownOAuthClientsAsync(context, tenant.Id, ct);

        // Assign creator as owner
        var ownerRole = await context.TenantRoles
            .FirstAsync(r => r.TenantId == tenant.Id && r.Slug == "owner", ct);
        await AddMemberAsync(tenant.Id, creatorSubjectId, [ownerRole.Id], ct: ct);

        _cache.Remove("tenant:__sole__");
        return ToCreatedDto(tenant);
    }

    public async Task<TenantCreatedDto> CreateWithoutOwnerAsync(
        string slug, string displayName, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);

        var tenant = new TenantEntity
        {
            Slug = slug.ToLowerInvariant(),
            DisplayName = displayName,
            IsActive = true,
        };

        context.Tenants.Add(tenant);
        await context.SaveChangesAsync(ct);

        // Set the RLS tenant context so subsequent writes to tenant-scoped
        // tables (roles, members, OAuth clients) are permitted.
        await SetTenantGuc(context, tenant.Id);

        // Seed default roles for this tenant (but don't assign an owner)
        await _roleService.SeedRolesForTenantAsync(tenant.Id, ct);

        // Create Public subject membership (no roles = unconfigured sentinel)
        await CreatePublicSubjectMembershipAsync(context, tenant.Id, ct);

        // Seed bundled known OAuth clients (Trio, xDrip+, etc.)
        await SeedKnownOAuthClientsAsync(context, tenant.Id, ct);

        _cache.Remove("tenant:__sole__");
        return ToCreatedDto(tenant);
    }

    /// <summary>
    /// Re-seeds roles, public membership, and OAuth clients for an existing
    /// tenant after its data has been purged. Used by the dev-only reset flow.
    /// </summary>
    public async Task SeedAfterResetAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        await SetTenantGuc(context, tenantId);

        await _roleService.SeedRolesForTenantAsync(tenantId, ct);
        await CreatePublicSubjectMembershipAsync(context, tenantId, ct);
        await SeedKnownOAuthClientsAsync(context, tenantId, ct);
    }

    public async Task<List<TenantDto>> GetAllAsync(CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        return await context.Tenants.AsNoTracking()
            .Select(t => new TenantDto(t.Id, t.Slug, t.DisplayName, t.IsActive, t.SysCreatedAt))
            .ToListAsync(ct);
    }

    public async Task<TenantDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        var tenant = await context.Tenants.AsNoTracking()
            .Include(t => t.Members)
                .ThenInclude(m => m.Subject)
            .Include(t => t.Members)
                .ThenInclude(m => m.MemberRoles)
                    .ThenInclude(mr => mr.TenantRole)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        if (tenant == null) return null;

        return new TenantDetailDto(
            tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, tenant.SysCreatedAt,
            tenant.Members
                .Where(m => m.RevokedAt == null)
                .Select(m => new TenantMemberDto(
                    m.Id,
                    m.SubjectId,
                    m.Subject?.Name,
                    m.MemberRoles.Select(mr => new TenantMemberRoleDto(
                        mr.TenantRoleId, mr.TenantRole.Name, mr.TenantRole.Slug)).ToList(),
                    m.DirectPermissions,
                    m.Label,
                    m.LimitTo24Hours,
                    m.LastUsedAt,
                    m.SysCreatedAt))
                .ToList());
    }

    public async Task<TenantDto> UpdateAsync(
        Guid id, string displayName, bool isActive, bool? allowAccessRequests = null, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        var tenant = await context.Tenants.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Tenant {id} not found");

        tenant.DisplayName = displayName;
        tenant.IsActive = isActive;
        if (allowAccessRequests.HasValue)
            tenant.AllowAccessRequests = allowAccessRequests.Value;
        await context.SaveChangesAsync(ct);

        // Invalidate cached tenant context
        _cache.Remove($"tenant:{tenant.Slug}");
        _cache.Remove("tenant:__sole__");

        return ToDto(tenant);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        var tenant = await context.Tenants.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Tenant {id} not found");

        context.Tenants.Remove(tenant);
        await context.SaveChangesAsync(ct);

        // Invalidate cached tenant context
        _cache.Remove($"tenant:{tenant.Slug}");
        _cache.Remove("tenant:__sole__");
    }

    public async Task AddMemberAsync(
        Guid tenantId, Guid subjectId, List<Guid> roleIds, List<string>? directPermissions = null,
        string? label = null, bool limitTo24Hours = false, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);

        // Check if already a member
        var exists = await context.TenantMembers
            .AnyAsync(tm => tm.TenantId == tenantId && tm.SubjectId == subjectId, ct);

        if (exists)
            return;

        var member = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubjectId = subjectId,
            DirectPermissions = directPermissions,
            Label = label,
            LimitTo24Hours = limitTo24Hours,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        };

        context.TenantMembers.Add(member);

        // Create role assignments
        var now = DateTime.UtcNow;
        foreach (var roleId in roleIds)
        {
            context.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantMemberId = member.Id,
                TenantRoleId = roleId,
                SysCreatedAt = now,
            });
        }

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Race condition: another request already inserted. This is fine.
        }
    }

    public async Task RemoveMemberAsync(
        Guid tenantId, Guid subjectId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        var member = await context.TenantMembers
            .FirstOrDefaultAsync(tm => tm.TenantId == tenantId && tm.SubjectId == subjectId, ct);

        if (member != null)
        {
            context.TenantMembers.Remove(member);
            await context.SaveChangesAsync(ct);
        }
    }

    public async Task<List<TenantDto>> GetTenantsForSubjectAsync(
        Guid subjectId, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateDbContextAsync(ct);
        return await context.TenantMembers.AsNoTracking()
            .Where(tm => tm.SubjectId == subjectId)
            .Include(tm => tm.Tenant)
            .Select(tm => new TenantDto(
                tm.Tenant!.Id, tm.Tenant.Slug, tm.Tenant.DisplayName,
                tm.Tenant.IsActive, tm.Tenant.SysCreatedAt))
            .ToListAsync(ct);
    }

    public async Task<SlugValidationResult> ValidateSlugAsync(string slug, CancellationToken ct = default)
    {
        var normalized = slug.ToLowerInvariant().Trim();

        if (!SlugPattern().IsMatch(normalized))
            return new SlugValidationResult(false, "Slug must be 3-64 characters, alphanumeric and hyphens only, no leading/trailing hyphens");

        if (ReservedSlugs.Contains(normalized))
            return new SlugValidationResult(false, "This name is reserved");

        await using var context = await _factory.CreateDbContextAsync(ct);
        var exists = await context.Tenants.AsNoTracking()
            .AnyAsync(t => t.Slug == normalized, ct);

        if (exists)
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
        await using var context = await _factory.CreateDbContextAsync(ct);
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            try
            {
                // 1. Create tenant
                var tenant = new TenantEntity
                {
                    Slug = slug.ToLowerInvariant(),
                    DisplayName = displayName,
                    IsActive = true,
                };

                context.Tenants.Add(tenant);
                await context.SaveChangesAsync(ct);

                // Set RLS tenant context for the remainder of this transaction.
                // The connection is already open, so the TenantConnectionInterceptor
                // won't fire again — we must set the GUC manually.
                await context.Database.ExecuteSqlRawAsync(
                    "SELECT set_config('app.current_tenant_id', {0}, false)",
                    tenant.Id.ToString());

                // Seed default roles for this tenant (inline to share transaction context)
                var now = DateTime.UtcNow;
                foreach (var (roleSlug, permissions) in TenantPermissions.SeedRolePermissions)
                {
                    var name = TenantPermissions.SeedRoleNames[roleSlug];
                    context.TenantRoles.Add(new TenantRoleEntity
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenant.Id,
                        Name = name,
                        Slug = roleSlug,
                        Description = null,
                        Permissions = new List<string>(permissions),
                        IsSystem = true,
                        SysCreatedAt = now,
                        SysUpdatedAt = now,
                    });
                }
                await context.SaveChangesAsync(ct);

                // Create Public subject membership (no roles = unconfigured sentinel)
                var publicSubject = await context.Subjects
                    .FirstOrDefaultAsync(s => s.IsSystemSubject && s.Name == "Public", ct);

                if (publicSubject != null)
                {
                    context.TenantMembers.Add(new TenantMemberEntity
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenant.Id,
                        SubjectId = publicSubject.Id,
                        LimitTo24Hours = true,
                        Label = "Public Access",
                        SysCreatedAt = now,
                        SysUpdatedAt = now,
                    });
                    await context.SaveChangesAsync(ct);
                }
                else
                {
                    _logger.LogWarning("Public system subject not found — skipping public access membership for tenant {TenantId}", tenant.Id);
                }

                // Seed bundled known OAuth clients (Trio, xDrip+, etc.)
                await SeedKnownOAuthClientsAsync(context, tenant.Id, ct);

                // 2. Find or create subject by email
                var subjectId = credential?.SubjectId ?? oidcIdentity?.SubjectId ?? Guid.CreateVersion7();
                var subject = await context.Subjects.FirstOrDefaultAsync(s => s.Email == ownerEmail, ct);
                if (subject == null)
                {
                    subject = new SubjectEntity
                    {
                        Id = subjectId,
                        Name = ownerUsername,
                        Username = ownerUsername.ToLowerInvariant(),
                        Email = ownerEmail,
                        IsActive = true,
                        ApprovalStatus = "Approved",
                    };
                    context.Subjects.Add(subject);
                    await context.SaveChangesAsync(ct);
                }

                // 3. Create credential (passkey or OIDC identity)
                if (credential is not null)
                {
                    context.PasskeyCredentials.Add(new PasskeyCredentialEntity
                    {
                        Id = Guid.CreateVersion7(),
                        SubjectId = subject.Id,
                        CredentialId = Convert.FromBase64String(credential.CredentialId),
                        PublicKey = Convert.FromBase64String(credential.PublicKey),
                        SignCount = credential.SignCount,
                        Transports = credential.Transports,
                        AaGuid = credential.AaGuid,
                    });
                }
                else if (oidcIdentity is not null)
                {
                    // Normalize issuer URL to match OidcProviderService storage format
                    var normalizedIssuer = oidcIdentity.Issuer.TrimEnd('/');

                    // Ensure the OIDC provider row exists (config-managed providers
                    // use deterministic GUIDs but may not have DB rows yet).
                    var provider = await context.OidcProviders
                        .FirstOrDefaultAsync(p => p.IssuerUrl == normalizedIssuer, ct);

                    if (provider == null)
                    {
                        provider = new OidcProviderEntity
                        {
                            Id = OidcProviderService.CreateDeterministicGuid(normalizedIssuer),
                            Name = oidcIdentity.Provider,
                            IssuerUrl = normalizedIssuer,
                            ClientId = string.Empty, // Populated by config on next startup
                            IsEnabled = true,
                        };
                        context.OidcProviders.Add(provider);
                        await context.SaveChangesAsync(ct);
                    }

                    // Re-use existing OIDC identity if one already exists for this
                    // (oidc_subject_id, issuer) pair — the subject may have signed up
                    // for a previous tenant with the same OAuth account.
                    var existingIdentity = await context.SubjectOidcIdentities
                        .FirstOrDefaultAsync(x =>
                            x.OidcSubjectId == oidcIdentity.OidcSubjectId
                            && x.Issuer == normalizedIssuer, ct);

                    if (existingIdentity is null)
                    {
                        context.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
                        {
                            Id = Guid.CreateVersion7(),
                            SubjectId = subject.Id,
                            ProviderId = provider.Id,
                            OidcSubjectId = oidcIdentity.OidcSubjectId,
                            Issuer = normalizedIssuer,
                            Email = oidcIdentity.Email,
                            LinkedAt = DateTime.UtcNow,
                        });
                    }
                    else
                    {
                        existingIdentity.LastUsedAt = DateTime.UtcNow;
                    }
                }
                await context.SaveChangesAsync(ct);

                // 4. Add subject as tenant owner (inline to share transaction context)
                var ownerRole = await context.TenantRoles
                    .FirstAsync(r => r.TenantId == tenant.Id && r.Slug == "owner", ct);
                var member = new TenantMemberEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant.Id,
                    SubjectId = subject.Id,
                    SysCreatedAt = now,
                    SysUpdatedAt = now,
                };
                context.TenantMembers.Add(member);
                context.TenantMemberRoles.Add(new TenantMemberRoleEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantMemberId = member.Id,
                    TenantRoleId = ownerRole.Id,
                    SysCreatedAt = now,
                });
                await context.SaveChangesAsync(ct);

                // 5. Commit transaction
                await transaction.CommitAsync(ct);

                return new ProvisionResult(tenant.Id, subject.Id, tenant.Slug);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
    }

    private async Task CreatePublicSubjectMembershipAsync(
        NocturneDbContext context, Guid tenantId, CancellationToken ct = default)
    {
        var publicSubject = await context.Subjects
            .FirstOrDefaultAsync(s => s.IsSystemSubject && s.Name == "Public", ct);

        if (publicSubject != null)
        {
            context.TenantMembers.Add(new TenantMemberEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                SubjectId = publicSubject.Id,
                LimitTo24Hours = true,
                Label = "Public Access",
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync(ct);
        }
        else
        {
            _logger.LogWarning("Public system subject not found — skipping public access membership for tenant {TenantId}", tenantId);
        }
    }


    /// <summary>
    /// Sets the RLS tenant context on a factory-created DbContext. Sets both
    /// the context's TenantId (so the connection interceptor fires on new
    /// connections) and the PostgreSQL GUC on the current connection.
    /// </summary>
    private static async Task SetTenantGuc(NocturneDbContext context, Guid tenantId)
    {
        context.TenantId = tenantId;
        await context.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant_id', {0}, false)",
            tenantId.ToString());
    }

    private static TenantDto ToDto(TenantEntity t) =>
        new(t.Id, t.Slug, t.DisplayName, t.IsActive, t.SysCreatedAt);

    private static TenantCreatedDto ToCreatedDto(TenantEntity t) =>
        new(t.Id, t.Slug, t.DisplayName, t.IsActive, t.SysCreatedAt);

    /// <summary>
    /// Seed the bundled known-app directory into a tenant's oauth_clients.
    /// Idempotent: existing rows for the same software_id are left untouched.
    /// </summary>
    private static async Task SeedKnownOAuthClientsAsync(
        NocturneDbContext context, Guid tenantId, CancellationToken ct)
    {
        var existingSoftwareIds = await context.OAuthClients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.SoftwareId != null)
            .Select(c => c.SoftwareId!)
            .ToListAsync(ct);
        var existingSet = new HashSet<string>(existingSoftwareIds, StringComparer.Ordinal);

        var added = 0;
        foreach (var entry in KnownOAuthClients.Entries.Where(e => !existingSet.Contains(e.SoftwareId)))
        {
            context.OAuthClients.Add(new OAuthClientEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ClientId = Guid.CreateVersion7().ToString(),
                SoftwareId = entry.SoftwareId,
                ClientName = entry.DisplayName,
                ClientUri = entry.Homepage,
                LogoUri = entry.LogoUri,
                DisplayName = entry.DisplayName,
                IsKnown = true,
                RedirectUris = JsonSerializer.Serialize(entry.RedirectUris),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            added++;
        }

        if (added > 0)
        {
            await context.SaveChangesAsync(ct);
        }
    }
}
