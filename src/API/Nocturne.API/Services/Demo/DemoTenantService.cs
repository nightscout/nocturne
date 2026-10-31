using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Demo;

/// <summary>
/// Lifecycle operations for the demo tenant: provisioning its access grants and
/// resetting it back to a freshly provisioned state.
/// </summary>
/// <remarks>
/// The demo tenant carries two access paths. The Public system subject holds every scope in
/// <see cref="Scope.PublicShareScopes"/> with no history limit, so the tenant's
/// share link shows the full history of every shareable category. Separately, one non-system
/// member holds the tenant's own <c>demo-visitor</c> role
/// (<see cref="Scope.DemoVisitorPermissions"/>, not a seed role) so an anonymous
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

    private readonly INocturneDbContextFactory _factory;
    private readonly ITenantRepository _tenants;
    private readonly ITenantMembershipRepository _members;
    private readonly ISubjectRepository _subjects;
    private readonly IFirstPartyTokenRepository _sessions;
    private readonly ITenantService _tenantService;
    private readonly PublicAccessCacheService _publicAccessCache;
    private readonly ITenantCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<DemoTenantService> _logger;

    /// <param name="factory">For the units of work a reset and a provision run in.</param>
    /// <param name="sessions">The first-party sessions a retired demo visitor's rows are deleted from.</param>
    public DemoTenantService(
        INocturneDbContextFactory factory,
        ITenantRepository tenants,
        ITenantMembershipRepository members,
        ISubjectRepository subjects,
        IFirstPartyTokenRepository sessions,
        ITenantService tenantService,
        PublicAccessCacheService publicAccessCache,
        ITenantCache cache,
        TimeProvider time,
        ILogger<DemoTenantService> logger)
    {
        _factory = factory;
        _tenants = tenants;
        _members = members;
        _subjects = subjects;
        _sessions = sessions;
        _tenantService = tenantService;
        _publicAccessCache = publicAccessCache;
        _cache = cache;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Returns the demo tenant, or <see langword="null"/> when none is provisioned.
    /// </summary>
    public Task<TenantRecord?> FindDemoTenantAsync(CancellationToken ct = default) =>
        _tenants.FindDemoTenantAsync(ct);

    /// <summary>
    /// Resolves the subject id behind the demo tenant's member membership, or
    /// <see langword="null"/> when the tenant has no demo member.
    /// </summary>
    /// <remarks>
    /// The subject must itself be flagged a demo subject, not merely hold the
    /// membership under the demo username. Both callers act on the result in ways that must never
    /// reach a real account — one issues a session for it to any anonymous caller, the other
    /// deletes it — and the membership alone does not establish that:
    /// <see cref="EnsureDemoMemberAsync"/> adopts a pre-existing row under that username rather
    /// than asserting it created it. Filtering here means the session endpoint fails closed by
    /// returning 404 rather than depending on a later guard whose refusal a caller might drop.
    /// </remarks>
    public async Task<Guid?> FindDemoMemberSubjectIdAsync(Guid tenantId, CancellationToken ct = default)
    {
        var member = await _members.FindMemberByUsernameAsync(tenantId, DemoMemberUsername, ct);
        return member is { IsDemoSubject: true } ? member.SubjectId : null;
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
        var tenant = await _tenants.GetAsync(tenantId, ct);
        if (tenant is null)
            return;

        // One unit, pinned to the tenant: the role, the Public grant and the demo member, with its
        // subject, land together.
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(_factory, tenantId, ct);
        var tenants = new TenantRepository(unit);
        var members = new TenantMembershipRepository(unit);
        var subjects = new SubjectRepository(unit, _time);

        await unit.CommitAsync(async t =>
        {
            await tenants.UpdateAsync(tenantId, TenantDefaults(tenant), t);

            var demoRoleId = await EnsureDemoRoleAsync(members, tenantId, t);
            await unit.Context.SaveChangesAsync(t);

            await GrantPublicAccessAsync(members, tenantId, t);
            await EnsureDemoMemberAsync(members, subjects, tenantId, demoRoleId, t);

            await unit.Context.SaveChangesAsync(t);
        }, ct);

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
        var tenant = await _tenants.FindDemoTenantAsync(ct);
        if (tenant is null)
            return null;

        // Resolve the outgoing demo member before the wipe: the cascade removes its
        // membership, so afterwards there is nothing left to resolve its subject from.
        var outgoingMemberSubjectId = await FindDemoMemberSubjectIdAsync(tenant.Id, ct);

        await using (var unit = await DbUnitOfWork.OpenPlatformAsync(_factory, ct))
        {
            var tenants = new TenantRepository(unit);

            await unit.CommitAsync(async token =>
            {
                if (await tenants.GetAsync(tenant.Id, token) is not { } current)
                    return;
                await tenants.DeleteAsync(tenant.Id, token);
                await unit.Context.SaveChangesAsync(token);

                await tenants.CreateAsync(SnapshotTenant(current), token);
                await unit.Context.SaveChangesAsync(token);
            }, ct);
        }

        // Subjects and refresh tokens are subject-scoped, so the cascade does not reach
        // them; the outgoing demo member has to be retired explicitly.
        await RetireDemoMemberAsync(outgoingMemberSubjectId, ct);

        // The wipe empties the tables but not the tenant cache, so without this the demo keeps
        // serving the data it just deleted until each entry's TTL expires.
        await _cache.RemoveTenantAsync(tenant.Id, ct);

        // Re-seed roles and the Public membership, then
        // re-apply the demo tenant's own grants on top.
        await _tenantService.SeedAfterResetAsync(tenant.Id, ct);
        await ConfigureAccessAsync(tenant.Id, ct);

        _logger.LogInformation("Demo tenant {TenantId} reset: data and configuration cleared", tenant.Id);
        return tenant.Id;
    }

    /// <summary>Tenant state that survives a reset: identity, plus the demo defaults.</summary>
    private NewTenant SnapshotTenant(TenantRecord tenant) => new(
        tenant.Slug,
        tenant.DisplayName,
        Id: tenant.Id,
        IsActive: tenant.IsActive,
        IsDemo: true,
        AllowAccessRequests: false,
        AllowPublicDocs: true,
        OnboardingCompletedAt: _time.GetUtcNow().UtcDateTime,
        ShareTokenHash: tenant.ShareTokenHash,
        ShareTokenEncrypted: tenant.ShareTokenEncrypted,
        ShareTokenSetAt: tenant.ShareTokenSetAt,
        DemoConfig: new DemoTenantConfig(
            tenant.DemoConfig?.NextResetAt,
            _time.GetUtcNow().UtcDateTime,
            tenant.DemoConfig?.AccessMode ?? "open",
            tenant.DemoConfig?.BackfillDays ?? 90,
            tenant.DemoConfig?.IntervalMinutes ?? 5,
            tenant.DemoConfig?.ResetIntervalMinutes ?? 0));

    /// <summary>
    /// The tenant-row state a demo tenant is expected to hold. Applied at provisioning and
    /// re-applied by every reset, so a demo that predates one of these defaults picks it up.
    /// </summary>
    internal TenantEdit TenantDefaults(TenantRecord tenant) => new(
        // The web app's authenticated layout bounces a tenant whose onboarding is
        // incomplete to /setup, which would strand a signed-in demo visitor.
        OnboardingCompletedAt: tenant.OnboardingCompletedAt ?? _time.GetUtcNow().UtcDateTime,
        // A demo tenant has no owner to review access requests.
        AllowAccessRequests: false,
        // The demo's Scalar page prefills a working token, which needs the docs served on the
        // demo's own host.
        AllowPublicDocs: true);

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
    /// <c>audit.read</c>, which <see cref="Scope.DemoVisitorPermissions"/> excludes,
    /// so that exclusion is load-bearing and not merely tidy.
    /// </para>
    /// <para>
    /// Session rows written before addresses were scrubbed at the repository sink still carry a
    /// visitor address, and nothing backfills them: they are cleared by the first reset after
    /// deployment, along with the subject they belong to.
    /// </para>
    /// <para>
    /// Only ever called with a subject read from the demo tenant's own membership; the
    /// <see cref="ISubjectRepository.IsDemoSubjectAsync"/> check makes that explicit rather than
    /// implied, because this deletes a global row and must never reach a real account.
    /// </para>
    /// <para>
    /// A membership outside the demo tenant is deleted rather than treated as a reason to
    /// keep the subject: an account anyone can obtain a session for has no business holding
    /// one, and leaving it would keep publicly-obtainable credentials alive against that
    /// tenant across every reset.
    /// </para>
    /// <para>
    /// <c>subjects</c> and <c>tenant_members</c> are not tenant-scoped, so these reads see
    /// every tenant — which is what makes the cleanup complete.
    /// </para>
    /// </remarks>
    private async Task RetireDemoMemberAsync(Guid? subjectId, CancellationToken ct)
    {
        if (subjectId is null)
            return;

        if (await _subjects.IsDemoSubjectAsync(subjectId.Value, ct) is not true)
        {
            _logger.LogWarning(
                "Subject {SubjectId} held the demo membership but is not a demo subject — leaving it in place",
                subjectId);
            return;
        }

        await _sessions.DeleteAllForSubjectAsync(subjectId.Value, ct);
        await _members.RemoveMembershipsOfSubjectAsync(subjectId.Value, ct);
        await _subjects.DeleteAsync(subjectId.Value, ct);
    }

    /// <summary>
    /// Ensures the demo tenant's own role exists, carrying
    /// <see cref="Scope.DemoVisitorPermissions"/>, and rewrites its permissions
    /// to that list on every call so the role cannot drift.
    /// </summary>
    /// <remarks>
    /// Deliberately not the seed <c>admin</c> role: that includes <c>members.manage</c> and
    /// <c>roles.manage</c>, and role and direct permissions are unioned into a member's
    /// effective set, so holding either is the ability to grant oneself <c>*</c>. Anyone can
    /// get a session for the demo member, so it must not hold an escalation primitive.
    /// </remarks>
    private async Task<Guid> EnsureDemoRoleAsync(
        ITenantMembershipRepository members, Guid tenantId, CancellationToken ct)
    {
        var roleId = await members.FindRoleIdBySlugAsync(tenantId, DemoRoleSlug, ct);
        if (roleId is null)
        {
            var created = await members.CreateRoleAsync(
                new NewTenantRole(tenantId, "Demo Visitor", DemoRoleSlug, null, [.. Scope.DemoVisitorPermissions], IsSystem: true), ct);
            return created.Id;
        }

        await members.UpdateRoleAsync(
            tenantId, roleId.Value,
            new TenantRoleEdit("Demo Visitor", DemoRoleSlug, null, [.. Scope.DemoVisitorPermissions]),
            _time.GetUtcNow().UtcDateTime, ct);
        return roleId.Value;
    }

    /// <summary>
    /// Grants the Public system subject every shareable read scope and lifts its 24-hour
    /// history limit, widening the tenant's public share view to the full history of every
    /// shareable category.
    /// </summary>
    /// <remarks>
    /// Written as direct permissions bounded by
    /// <see cref="Scope.PublicShareScopes"/>, not as the demo-visitor role. The
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
        ITenantMembershipRepository members, Guid tenantId, CancellationToken ct)
    {
        var publicMember = await members.FindPublicMemberAsync(tenantId, ct);

        if (publicMember is null)
        {
            _logger.LogWarning(
                "Public membership missing on demo tenant {TenantId} — its share link will expose nothing", tenantId);
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await members.SetLimitTo24HoursAsync(tenantId, publicMember.Id, false, now, ct);
        await members.SetDirectPermissionsAsync(tenantId, publicMember.Id, [.. Scope.PublicShareScopes], now, ct);

        if (publicMember.Roles.Count > 0)
        {
            _logger.LogInformation(
                "Removing {Count} role assignment(s) from the Public membership on demo tenant {TenantId}: " +
                "its grant is the share vocabulary alone",
                publicMember.Roles.Count, tenantId);
            await members.ReplaceRolesAsync(
                tenantId, publicMember.Id, publicMember.Roles.Select(r => r.RoleId).ToList(), [], now, ct);
        }
    }

    /// <summary>
    /// Ensures the demo membership exists on the demo-visitor role, creating its subject on
    /// first provision. The subject is created fresh and carries no global username, so
    /// it can never collide with — or resolve to — an operator's or invitee's account.
    /// </summary>
    private async Task EnsureDemoMemberAsync(
        ITenantMembershipRepository members, ISubjectRepository subjects, Guid tenantId, Guid demoRoleId, CancellationToken ct)
    {
        var member = await members.FindMemberByUsernameAsync(tenantId, DemoMemberUsername, ct);

        if (member is null)
        {
            // Anyone can obtain a session for this account, so it is flagged a demo subject and
            // refused wherever "authenticated" is read as "a person who signed up".
            var subjectId = await subjects.CreateAsync(
                new NewSubject(DemoMemberName, IsDemoSubject: true), ct);

            await members.AddMemberAsync(
                new NewTenantMember(
                    tenantId, subjectId, [demoRoleId], Label: DemoMemberName, Username: DemoMemberUsername, LimitTo24Hours: false),
                ct);
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await members.SetLimitTo24HoursAsync(tenantId, member.Id, false, now, ct);
        if (member.Roles.All(r => r.RoleId != demoRoleId))
            await members.ReplaceRolesAsync(tenantId, member.Id, [], [demoRoleId], now, ct);
    }
}
