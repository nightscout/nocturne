using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Auth;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Keys;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Demo;

/// <summary>
/// Lifecycle operations for the demo tenant: provisioning its access grants and
/// resetting it back to a freshly provisioned state.
/// </summary>
/// <remarks>
/// The demo tenant carries two access paths. The Public system subject holds every scope in
/// <see cref="TenantPermissions.PublicShareScopes"/> with no history limit, so the tenant's
/// share link shows the full history of every shareable category. Separately, one non-system
/// member holds the tenant's own <c>demo-visitor</c> role
/// (<see cref="TenantPermissions.DemoVisitorPermissions"/>, not a seed role) so an anonymous
/// visitor can be signed in as a real member (see <c>DemoSessionController</c>) and reach the
/// write and settings surfaces the read-only share host cannot serve.
/// <para>
/// The demo member is identified by its <em>membership</em> row
/// (<c>tenant_members.username</c>, unique per tenant), never by the subject's global
/// username: <c>subjects.username</c> carries no unique index and any operator or
/// invitee can choose one, so a global lookup could bind the demo to an unrelated
/// account and hand its session to anonymous callers.
/// </para>
/// </remarks>
public sealed class DemoTenantService
{
    /// <summary>
    /// Value of <c>tenant_members.username</c> for the demo tenant's human-facing
    /// member. Unique per tenant, so it identifies exactly one membership. Every
    /// visitor is signed in as this one member, so concurrent visitors share its
    /// view and its edits.
    /// </summary>
    public const string DemoMemberUsername = "demo";

    /// <summary>Display name of the demo member, shown in the app's account menu.</summary>
    public const string DemoMemberName = "Demo Visitor";

    /// <summary>
    /// Slug of the demo tenant's own role. Not one of the seed roles — see
    /// <see cref="EnsureDemoRoleAsync"/>.
    /// </summary>
    public const string DemoRoleSlug = "demo-visitor";

    private readonly IDbContextFactory<NocturneDbContext> _factory;
    private readonly ITenantService _tenantService;
    private readonly PublicAccessCacheService _publicAccessCache;
    private readonly ICacheService _cache;
    private readonly ILogger<DemoTenantService> _logger;

    public DemoTenantService(
        IDbContextFactory<NocturneDbContext> factory,
        ITenantService tenantService,
        PublicAccessCacheService publicAccessCache,
        ICacheService cache,
        ILogger<DemoTenantService> logger)
    {
        _factory = factory;
        _tenantService = tenantService;
        _publicAccessCache = publicAccessCache;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Returns the demo tenant, or <see langword="null"/> when none is provisioned.
    /// </summary>
    public async Task<TenantEntity?> FindDemoTenantAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Set<TenantEntity>()
            .AsNoTracking()
            .Include(t => t.DemoConfig)
            .FirstOrDefaultAsync(t => t.IsDemo, ct);
    }

    /// <summary>
    /// Resolves the subject id behind the demo tenant's member membership, or
    /// <see langword="null"/> when the tenant has no demo member.
    /// </summary>
    /// <remarks>
    /// The subject must itself carry <see cref="SubjectEntity.IsDemoSubject"/>, not merely hold the
    /// membership under the demo username. Both callers act on the result in ways that must never
    /// reach a real account — one issues a session for it to any anonymous caller, the other
    /// deletes it — and the membership alone does not establish that:
    /// <see cref="EnsureDemoMemberAsync"/> adopts a pre-existing row under that username rather
    /// than asserting it created it. Filtering here means the session endpoint fails closed by
    /// returning 404 rather than depending on a later guard whose refusal a caller might drop.
    /// </remarks>
    public async Task<Guid?> FindDemoMemberSubjectIdAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.TenantId = tenantId;

        return await db.TenantMembers
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                && m.Username == DemoMemberUsername
                && m.RevokedAt == null
                && m.Subject!.IsDemoSubject)
            .Select(m => (Guid?)m.SubjectId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Applies the demo tenant's access configuration: marks onboarding complete so the app
    /// serves the dashboard instead of the setup wizard, opens the Public subject's share
    /// grant to every shareable category, and puts the demo member on the tenant's
    /// <c>demo-visitor</c> role. Idempotent — called on every provision and after every
    /// reset, so a reset that failed part-way is repaired by the demo service's next
    /// provision.
    /// </summary>
    public async Task ConfigureAccessAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.TenantId = tenantId;

        var tenant = await db.Set<TenantEntity>().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
            return;

        ApplyTenantDefaults(tenant);

        var demoRole = await EnsureDemoRoleAsync(db, tenantId, ct);

        await GrantPublicAccessAsync(db, tenantId, ct);
        await EnsureDemoMemberAsync(db, tenantId, demoRole.Id, ct);

        await db.SaveChangesAsync(ct);

        // The Public membership's permissions and history limit were just rewritten, and
        // misses are cached too — evict so the share host sees the new grant at once.
        _publicAccessCache.Evict(tenantId);
    }

    /// <summary>
    /// Resets the demo tenant to a freshly provisioned state, discarding both the
    /// generated data and every configuration change a visitor made — settings,
    /// roles, members, connectors, alert rules, trackers, audit history — and
    /// revoking the sessions visitors were holding.
    /// </summary>
    /// <remarks>
    /// The wipe deletes the <c>tenants</c> row and re-inserts it with the same id,
    /// slug and share token, letting the database's cascade from <c>tenants</c> clear
    /// every tenant-scoped table. That keeps the reset exhaustive without a
    /// hand-maintained table list to drift (guarded by
    /// <c>TenantDeleteCascadeTests</c>), and preserving the tenant's identity means
    /// cached tenant contexts and share links stay valid across the reset. Demo
    /// operational state (reset schedule, generation intervals) is carried across;
    /// everything else returns to its provisioning default.
    /// <para>
    /// Re-seeding runs after the wipe commits, so a failure between the two leaves the
    /// tenant without roles or members. That state is repaired by
    /// <see cref="ConfigureAccessAsync"/> on the demo service's next provision or
    /// reset, and this method throws rather than reporting success.
    /// </para>
    /// </remarks>
    /// <returns>The reset demo tenant's id, or <see langword="null"/> when no demo tenant exists.</returns>
    public async Task<Guid?> ResetAsync(CancellationToken ct = default)
    {
        var tenantId = await FindDemoTenantIdAsync(ct);
        if (tenantId is null)
            return null;

        // Resolve the outgoing demo member before the wipe: the cascade removes its
        // membership, so afterwards there is nothing left to resolve its subject from.
        var outgoingMemberSubjectId = await FindDemoMemberSubjectIdAsync(tenantId.Value, ct);

        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            var strategy = db.Database.CreateExecutionStrategy();

            // Each attempt re-reads the tenant on its own context: a retry must not
            // reuse entities the previous attempt already detached or began tracking.
            await strategy.ExecuteAsync(async () =>
            {
                await using var attempt = await _factory.CreateDbContextAsync(ct);
                await using var transaction = await attempt.Database.BeginTransactionAsync(ct);

                var tenant = await attempt.Set<TenantEntity>()
                    .Include(t => t.DemoConfig)
                    .FirstOrDefaultAsync(t => t.Id == tenantId.Value, ct);

                if (tenant is null)
                    return;

                var preserved = SnapshotTenant(tenant);
                var preservedConfig = SnapshotDemoConfig(tenant);

                attempt.Set<TenantEntity>().Remove(tenant);
                await attempt.SaveChangesAsync(ct);

                attempt.Set<TenantEntity>().Add(preserved);
                attempt.Set<TenantDemoConfigEntity>().Add(preservedConfig);
                await attempt.SaveChangesAsync(ct);

                await transaction.CommitAsync(ct);
            });
        }

        // Subjects and refresh tokens are subject-scoped, so the cascade does not reach
        // them; the outgoing demo member has to be retired explicitly.
        await RetireDemoMemberAsync(outgoingMemberSubjectId, ct);

        // Read paths cache recent entries and treatments in process, keyed by tenant id.
        // The wipe empties the tables but not those caches, so without this the demo keeps
        // serving the data it just deleted until each entry's TTL expires.
        await InvalidateReadCachesAsync(tenantId.Value, ct);

        // Re-seed roles, the Public membership and the bundled OAuth clients, then
        // re-apply the demo tenant's own grants on top.
        await _tenantService.SeedAfterResetAsync(tenantId.Value, ct);
        await ConfigureAccessAsync(tenantId.Value, ct);

        _logger.LogInformation("Demo tenant {TenantId} reset: data and configuration cleared", tenantId);
        return tenantId;
    }

    /// <summary>
    /// Drops the cached recent-entry and recent-treatment reads for the tenant. The cache is
    /// keyed by tenant id, so this touches nothing belonging to another tenant.
    /// </summary>
    private async Task InvalidateReadCachesAsync(Guid tenantId, CancellationToken ct)
    {
        var id = tenantId.ToString();
        await _cache.RemoveByPatternAsync(CacheKeyBuilder.BuildRecentEntriesPattern(id), ct);
        await _cache.RemoveByPatternAsync(CacheKeyBuilder.BuildRecentTreatmentsPattern(id), ct);
        await _cache.RemoveByPatternAsync(CacheKeyBuilder.BuildProfileTimestampPattern(id), ct);
    }

    private async Task<Guid?> FindDemoTenantIdAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Set<TenantEntity>()
            .AsNoTracking()
            .Where(t => t.IsDemo)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Tenant state that survives a reset: identity, plus the demo defaults.</summary>
    private static TenantEntity SnapshotTenant(TenantEntity tenant)
    {
        var preserved = new TenantEntity
        {
            Id = tenant.Id,
            Slug = tenant.Slug,
            DisplayName = tenant.DisplayName,
            IsActive = tenant.IsActive,
            IsDemo = true,
            ShareToken = tenant.ShareToken,
            ShareTokenSetAt = tenant.ShareTokenSetAt,
            SysCreatedAt = tenant.SysCreatedAt,
            SysUpdatedAt = DateTime.UtcNow,
        };

        ApplyTenantDefaults(preserved);
        return preserved;
    }

    private static TenantDemoConfigEntity SnapshotDemoConfig(TenantEntity tenant)
    {
        var config = tenant.DemoConfig;
        return new TenantDemoConfigEntity
        {
            TenantId = tenant.Id,
            NextResetAt = config?.NextResetAt,
            LastResetAt = DateTime.UtcNow,
            AccessMode = config?.AccessMode ?? "open",
            BackfillDays = config?.BackfillDays ?? 90,
            IntervalMinutes = config?.IntervalMinutes ?? 5,
            ResetIntervalMinutes = config?.ResetIntervalMinutes ?? 0,
        };
    }

    private static void ApplyTenantDefaults(TenantEntity tenant)
    {
        // The web app's authenticated layout bounces a tenant whose onboarding is
        // incomplete to /setup, which would strand a signed-in demo visitor.
        tenant.OnboardingCompletedAt ??= DateTime.UtcNow;
        // A demo tenant has no owner to review access requests.
        tenant.AllowAccessRequests = false;
    }

    /// <summary>
    /// Retires the demo member the reset has just unseated: deletes its refresh tokens, so no
    /// visitor session outlives the reset, then deletes the subject itself along with any
    /// membership it picked up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session rows are the part this controls. It is not the case that the demo retains no
    /// visitor addresses at all: <c>AuditContextMiddleware</c> stamps
    /// <c>Connection.RemoteIpAddress</c> onto every <c>mutation_audit_log</c> row, so a visitor's
    /// address is recorded for each write they make and survives until the next reset clears the
    /// table by cascade. Nobody can read it in the meantime — <c>AuditController</c> gates on
    /// <c>audit.read</c>, which <see cref="TenantPermissions.DemoVisitorPermissions"/> excludes,
    /// so that exclusion is load-bearing and not merely tidy.
    /// </para>
    /// <para>
    /// Session rows written before addresses were scrubbed at the repository sink still carry a
    /// visitor address, and nothing backfills them: they are cleared by the first reset after
    /// deployment, along with the subject they belong to.
    /// </para>
    /// <para>
    /// Only ever called with a subject read from the demo tenant's own membership; the
    /// <see cref="SubjectEntity.IsDemoSubject"/> check makes that explicit rather than
    /// implied, because this deletes a global row and must never reach a real account.
    /// </para>
    /// <para>
    /// A membership outside the demo tenant is deleted rather than treated as a reason to
    /// keep the subject: an account anyone can obtain a session for has no business holding
    /// one, and leaving it would keep publicly-obtainable credentials alive against that
    /// tenant across every reset.
    /// </para>
    /// <para>
    /// <c>subjects</c> and <c>tenant_members</c> are not tenant-scoped, so these queries see
    /// every tenant — which is what makes the cleanup complete.
    /// </para>
    /// </remarks>
    private async Task RetireDemoMemberAsync(Guid? subjectId, CancellationToken ct)
    {
        if (subjectId is null)
            return;

        await using var db = await _factory.CreateDbContextAsync(ct);

        var isDemoSubject = await db.Subjects
            .AsNoTracking()
            .Where(s => s.Id == subjectId.Value)
            .Select(s => s.IsDemoSubject)
            .FirstOrDefaultAsync(ct);

        if (!isDemoSubject)
        {
            _logger.LogWarning(
                "Subject {SubjectId} held the demo membership but is not a demo subject — leaving it in place",
                subjectId);
            return;
        }

        await db.RefreshTokens
            .Where(t => t.SubjectId == subjectId.Value)
            .ExecuteDeleteAsync(ct);

        await db.TenantMembers
            .Where(m => m.SubjectId == subjectId.Value)
            .ExecuteDeleteAsync(ct);

        await db.Subjects.Where(s => s.Id == subjectId.Value).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Ensures the demo tenant's own role exists, carrying
    /// <see cref="TenantPermissions.DemoVisitorPermissions"/>, and rewrites its permissions
    /// to that list on every call so the role cannot drift.
    /// </summary>
    /// <remarks>
    /// Deliberately not the seed <c>admin</c> role: that includes <c>members.manage</c> and
    /// <c>roles.manage</c>, and role and direct permissions are unioned into a member's
    /// effective set, so holding either is the ability to grant oneself <c>*</c>. Anyone can
    /// get a session for the demo member, so it must not hold an escalation primitive.
    /// </remarks>
    private static async Task<TenantRoleEntity> EnsureDemoRoleAsync(
        NocturneDbContext db, Guid tenantId, CancellationToken ct)
    {
        var role = await db.TenantRoles
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Slug == DemoRoleSlug, ct);

        if (role is null)
        {
            role = new TenantRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Name = "Demo Visitor",
                Slug = DemoRoleSlug,
                Permissions = new List<string>(TenantPermissions.DemoVisitorPermissions),
                IsSystem = true,
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            };
            db.TenantRoles.Add(role);
            await db.SaveChangesAsync(ct);
            return role;
        }

        role.Permissions = new List<string>(TenantPermissions.DemoVisitorPermissions);
        role.SysUpdatedAt = DateTime.UtcNow;
        return role;
    }

    /// <summary>
    /// Grants the Public system subject every shareable read scope and lifts its 24-hour
    /// history limit, widening the tenant's public share view to the full history of every
    /// shareable category.
    /// </summary>
    /// <remarks>
    /// Written as direct permissions bounded by
    /// <see cref="TenantPermissions.PublicShareScopes"/>, not as the demo-visitor role. The
    /// Public subject serves the anonymous share viewer, and the other two writers of this
    /// membership — <c>MemberInviteController.SetMemberPermissions</c> and
    /// <c>ShareLinkService.SetScopesAsync</c> — both refuse anything outside that vocabulary.
    /// A third writer granting the write and administration atoms the demo member holds would
    /// leave the narrower two decorative, and would rest the whole property on
    /// <c>AuthenticationMiddleware</c> re-narrowing the grant on every share request.
    /// <para>
    /// Role assignments are cleared rather than left alone. Effective permissions are the union
    /// of role and direct permissions, so a role row left in place would widen the grant past
    /// that vocabulary and make setting <c>DirectPermissions</c> pointless. This is not
    /// hypothetical on an already-provisioned tenant: the code this replaced assigned the seed
    /// <c>admin</c> role to this exact membership, so the production demo tenant carries one,
    /// and provisioning is the only thing that will remove it.
    /// </para>
    /// </remarks>
    private async Task GrantPublicAccessAsync(
        NocturneDbContext db, Guid tenantId, CancellationToken ct)
    {
        var publicMember = await db.TenantMembers
            .Include(m => m.Subject)
            .Include(m => m.MemberRoles)
            .FirstOrDefaultAsync(
                m => m.TenantId == tenantId && m.Subject!.IsSystemSubject && m.Subject.Name == "Public", ct);

        if (publicMember is null)
        {
            _logger.LogWarning(
                "Public membership missing on demo tenant {TenantId} — its share link will expose nothing", tenantId);
            return;
        }

        publicMember.LimitTo24Hours = false;
        publicMember.DirectPermissions = [.. TenantPermissions.PublicShareScopes];

        if (publicMember.MemberRoles.Count > 0)
        {
            _logger.LogInformation(
                "Removing {Count} role assignment(s) from the Public membership on demo tenant {TenantId}: " +
                "its grant is the share vocabulary alone",
                publicMember.MemberRoles.Count, tenantId);
            db.TenantMemberRoles.RemoveRange(publicMember.MemberRoles);
        }
    }

    /// <summary>
    /// Ensures the demo membership exists on the demo-visitor role, creating its subject on
    /// first provision. The subject is created fresh and carries no global username, so
    /// it can never collide with — or resolve to — an operator's or invitee's account.
    /// </summary>
    private async Task EnsureDemoMemberAsync(
        NocturneDbContext db, Guid tenantId, Guid demoRoleId, CancellationToken ct)
    {
        var member = await db.TenantMembers
            .FirstOrDefaultAsync(
                m => m.TenantId == tenantId && m.Username == DemoMemberUsername, ct);

        if (member is null)
        {
            var subject = new SubjectEntity
            {
                Id = Guid.CreateVersion7(),
                Name = DemoMemberName,
                IsActive = true,
                ApprovalStatus = "Approved",
                // Anyone can obtain a session for this account, so it must be refused
                // wherever "authenticated" is read as "a person who signed up".
                IsDemoSubject = true,
            };
            db.Subjects.Add(subject);
            await db.SaveChangesAsync(ct);

            member = new TenantMemberEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                SubjectId = subject.Id,
                Username = DemoMemberUsername,
                LimitTo24Hours = false,
                Label = DemoMemberName,
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            };
            db.TenantMembers.Add(member);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            member.RevokedAt = null;
            member.LimitTo24Hours = false;
        }

        await AssignRoleAsync(db, member.Id, demoRoleId, ct);
    }

    private static async Task AssignRoleAsync(
        NocturneDbContext db, Guid memberId, Guid roleId, CancellationToken ct)
    {
        var assigned = await db.TenantMemberRoles
            .AnyAsync(mr => mr.TenantMemberId == memberId && mr.TenantRoleId == roleId, ct);

        if (assigned)
            return;

        db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = memberId,
            TenantRoleId = roleId,
            SysCreatedAt = DateTime.UtcNow,
        });
    }
}
