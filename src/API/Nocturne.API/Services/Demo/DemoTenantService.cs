using Microsoft.EntityFrameworkCore;
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
/// The demo tenant carries two access paths. The Public system subject holds the
/// Admin role so the demo data service can post entries and treatments without
/// credentials, and a single non-system <see cref="DemoMemberUsername"/> subject
/// holds the Admin role so an anonymous visitor can be signed in as a real member
/// (see <c>DemoSessionController</c>) and reach the write and settings surfaces the
/// read-only public share host cannot serve.
/// </remarks>
public sealed class DemoTenantService
{
    /// <summary>
    /// Username of the demo tenant's human-facing member. Every visitor is signed
    /// in as this one subject, so concurrent visitors share its view and its edits.
    /// </summary>
    public const string DemoMemberUsername = "demo";

    /// <summary>Display name of the demo member, shown in the app's account menu.</summary>
    public const string DemoMemberName = "Demo Visitor";

    private readonly IDbContextFactory<NocturneDbContext> _factory;
    private readonly ITenantService _tenantService;
    private readonly ILogger<DemoTenantService> _logger;

    public DemoTenantService(
        IDbContextFactory<NocturneDbContext> factory,
        ITenantService tenantService,
        ILogger<DemoTenantService> logger)
    {
        _factory = factory;
        _tenantService = tenantService;
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
    /// Resolves the subject id of the demo tenant's member, or <see langword="null"/>
    /// when the tenant is not a demo or has no demo member.
    /// </summary>
    public async Task<Guid?> FindDemoMemberSubjectIdAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.TenantId = tenantId;

        return await db.TenantMembers
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                && m.RevokedAt == null
                && !m.Subject!.IsSystemSubject
                && m.Subject.Username == DemoMemberUsername)
            .Select(m => (Guid?)m.SubjectId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Applies the demo tenant's access configuration: marks onboarding complete so
    /// the app serves the dashboard instead of the setup wizard, grants the Public
    /// subject the Admin role for unauthenticated ingest, and ensures the demo member
    /// subject exists with the Admin role. Idempotent — safe to call on every
    /// provision and after every reset.
    /// </summary>
    public async Task ConfigureAccessAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.TenantId = tenantId;

        var tenant = await db.Set<TenantEntity>().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
            return;

        // The web app's authenticated layout bounces a tenant whose onboarding is
        // incomplete to /setup, which would strand a signed-in demo visitor.
        tenant.OnboardingCompletedAt ??= DateTime.UtcNow;
        // A demo tenant has no owner to review access requests.
        tenant.AllowAccessRequests = false;

        var adminRole = await db.TenantRoles
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Slug == TenantPermissions.SeedRoles.Admin, ct);

        if (adminRole is null)
        {
            _logger.LogWarning(
                "Admin role missing on demo tenant {TenantId} — skipping demo access grants", tenantId);
            await db.SaveChangesAsync(ct);
            return;
        }

        await GrantPublicAccessAsync(db, tenantId, adminRole.Id, ct);
        await EnsureDemoMemberAsync(db, tenantId, adminRole.Id, ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Resets the demo tenant to a freshly provisioned state, discarding both the
    /// generated data and every configuration change a visitor made — settings,
    /// roles, members, connectors, alert rules, trackers, audit history.
    /// </summary>
    /// <remarks>
    /// The wipe deletes the <c>tenants</c> row and re-inserts it with the same id,
    /// slug and share token, letting the database's cascade from <c>tenants</c> clear
    /// every tenant-scoped table. That keeps the reset exhaustive without a
    /// hand-maintained table list to drift, and preserving the tenant's identity
    /// means cached tenant contexts and share links stay valid across the reset.
    /// Demo-specific operational state (reset schedule, generation intervals) is
    /// carried across; everything else returns to its provisioning default.
    /// </remarks>
    /// <returns>The reset demo tenant's id, or <see langword="null"/> when no demo tenant exists.</returns>
    public async Task<Guid?> ResetAsync(CancellationToken ct = default)
    {
        Guid tenantId;

        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            var tenant = await db.Set<TenantEntity>()
                .Include(t => t.DemoConfig)
                .FirstOrDefaultAsync(t => t.IsDemo, ct);

            if (tenant is null)
                return null;

            tenantId = tenant.Id;

            // Snapshot the identity and operational state to carry across the wipe.
            var preserved = new TenantEntity
            {
                Id = tenant.Id,
                Slug = tenant.Slug,
                DisplayName = tenant.DisplayName,
                IsActive = tenant.IsActive,
                IsDemo = true,
                ShareToken = tenant.ShareToken,
                ShareTokenSetAt = tenant.ShareTokenSetAt,
                OnboardingCompletedAt = DateTime.UtcNow,
                AllowAccessRequests = false,
                SysCreatedAt = tenant.SysCreatedAt,
                SysUpdatedAt = DateTime.UtcNow,
            };

            var config = tenant.DemoConfig;
            var preservedConfig = new TenantDemoConfigEntity
            {
                TenantId = tenant.Id,
                NextResetAt = config?.NextResetAt,
                LastResetAt = DateTime.UtcNow,
                AccessMode = config?.AccessMode ?? "open",
                BackfillDays = config?.BackfillDays ?? 90,
                IntervalMinutes = config?.IntervalMinutes ?? 5,
                ResetIntervalMinutes = config?.ResetIntervalMinutes ?? 0,
            };

            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.Set<TenantEntity>().Remove(tenant);
                await db.SaveChangesAsync(ct);

                db.Set<TenantEntity>().Add(preserved);
                db.Set<TenantDemoConfigEntity>().Add(preservedConfig);
                await db.SaveChangesAsync(ct);

                await transaction.CommitAsync(ct);
            });
        }

        // Re-seed roles, the Public membership and the bundled OAuth clients, then
        // re-apply the demo tenant's own grants on top.
        await _tenantService.SeedAfterResetAsync(tenantId, ct);
        await ConfigureAccessAsync(tenantId, ct);

        _logger.LogInformation("Demo tenant {TenantId} reset: data and configuration cleared", tenantId);
        return tenantId;
    }

    /// <summary>
    /// Assigns the Admin role to the Public system subject's membership and lifts its
    /// 24-hour history limit, so the demo service can write without credentials and
    /// the public share link exposes the full history.
    /// </summary>
    private async Task GrantPublicAccessAsync(
        NocturneDbContext db, Guid tenantId, Guid adminRoleId, CancellationToken ct)
    {
        var publicMember = await db.TenantMembers
            .Include(m => m.Subject)
            .FirstOrDefaultAsync(
                m => m.TenantId == tenantId && m.Subject!.IsSystemSubject && m.Subject.Name == "Public", ct);

        if (publicMember is null)
        {
            _logger.LogWarning(
                "Public membership missing on demo tenant {TenantId} — demo ingest will be unauthorized", tenantId);
            return;
        }

        publicMember.LimitTo24Hours = false;
        await AssignRoleAsync(db, publicMember.Id, adminRoleId, ct);
    }

    /// <summary>
    /// Ensures the demo member subject exists, is active, and is an Admin member of
    /// the demo tenant. The subject is global and reused across resets, so a session
    /// minted before a reset keeps resolving to the same account afterwards.
    /// </summary>
    private async Task EnsureDemoMemberAsync(
        NocturneDbContext db, Guid tenantId, Guid adminRoleId, CancellationToken ct)
    {
        var subject = await db.Subjects
            .FirstOrDefaultAsync(s => !s.IsSystemSubject && s.Username == DemoMemberUsername, ct);

        if (subject is null)
        {
            subject = new SubjectEntity
            {
                Id = Guid.CreateVersion7(),
                Name = DemoMemberName,
                Username = DemoMemberUsername,
                IsActive = true,
                ApprovalStatus = "Approved",
            };
            db.Subjects.Add(subject);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            subject.IsActive = true;
            subject.ApprovalStatus = "Approved";
        }

        var member = await db.TenantMembers
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.SubjectId == subject.Id, ct);

        if (member is null)
        {
            member = new TenantMemberEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                SubjectId = subject.Id,
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

        await AssignRoleAsync(db, member.Id, adminRoleId, ct);
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
