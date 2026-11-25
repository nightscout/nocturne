using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// A membership with two reasons, one of which lapses, on PostgreSQL under the runtime role: no
/// <c>LiveAt</c> reader sees the row once the reason has lapsed until it is recomputed; each read that
/// recomputes on read returns the narrowed membership at once; the sweep and the startup check
/// recompute due rows; and every writer of a membership waits on its lock.
/// </summary>
[Trait("Category", "Integration")]
public class MembershipReasonLapsePostgresTests(MembershipReasonLapsePostgresTests.Database database)
    : IClassFixture<MembershipReasonLapsePostgresTests.Database>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime T0 = new(2026, 11, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CampEnds = new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime After = new(2026, 12, 2, 0, 0, 0, DateTimeKind.Utc);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("membership_lapse");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private static NocturneDbContext Context(string connectionString, Guid tenant) =>
        TestContexts.Context(connectionString, tenant);

    private sealed record Site(
        string App, string Migrator, Guid Tenant, SuppliedContextFactory Factory, TenantMembershipRepository Members, Guid Owner,
        Guid ViewerRole, Guid CaretakerRole);

    private sealed record Lapsing(Guid Subject, Guid Member);

    private async Task<Site> SiteAsync(string slug, (string App, string Migrator)? on = null)
    {
        var (app, migrator) = on ?? (database.AppConnectionString, database.MigratorConnectionString);
        var tenant = Guid.NewGuid();
        await NpgsqlTestCommands.ExecuteAsync(migrator, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', '{slug}-{tenant:N}', 'membership-lapse', true, now(), now());
            """);

        var factory = new SuppliedContextFactory(() => Context(app, tenant));
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        var owner = await NewSubjectAsync(factory, $"{slug}-owner");
        await members.SeedRolesAsync(tenant, Ct);
        var ownerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, Ct))!.Value;
        var viewerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Viewer, Ct))!.Value;
        var caretakerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, Ct))!.Value;
        (await members.AddMemberAsync(new NewTenantMember(tenant, owner, [ownerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        return new Site(app, migrator, tenant, factory, members, owner, viewerRole, caretakerRole);
    }

    private static Task<Guid> NewSubjectAsync(SuppliedContextFactory factory, string name) =>
        new SubjectRepository(factory, AccountLinkRoutes.Off).CreateAsync(new NewSubject(name), Ct);

    /// <summary>
    /// A viewer whose direct reason never ends, with a second reason that gives caretaker until
    /// <see cref="CampEnds"/>, projected at <see cref="T0"/>.
    /// </summary>
    private static async Task<Lapsing> LapsingAsync(Site site, string name)
    {
        var subject = await NewSubjectAsync(site.Factory, name);
        (await site.Members.AddMemberAsync(new NewTenantMember(site.Tenant, subject, [site.ViewerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        var member = (await site.Members.FindMemberAsync(site.Tenant, subject, Ct))!.Id;
        await AddReasonAsync(site, member, site.CaretakerRole, CampEnds);
        (await site.Members.SetDirectPermissionsAsync(site.Tenant, member, null, T0, Ct)).Should().BeTrue();
        (await LapseAsync(site, member)).Should().Be(CampEnds);
        return new Lapsing(subject, member);
    }

    private static Task AddReasonAsync(Site site, Guid member, Guid role, DateTime? endsAt)
    {
        var reason = Guid.CreateVersion7();
        var ends = endsAt is { } end ? $"'{end:O}'" : "NULL";
        var (teamAccessSql, teamAccess) = TeamReasonSql.NewAccess(site.Tenant, "Camp");
        return NpgsqlTestCommands.ExecuteAsync(site.Migrator, $"""
            {teamAccessSql}
            INSERT INTO tenant_member_reasons (id, tenant_member_id, kind, source_id, limit_to_24_hours, access_ends_at)
            VALUES ('{reason}', '{member}', 'team', '{teamAccess}', false, {ends});
            INSERT INTO tenant_member_reason_roles (id, tenant_member_reason_id, tenant_role_id)
            VALUES ('{Guid.CreateVersion7()}', '{reason}', '{role}');
            {TeamReasonSql.StaffOfMember(teamAccess, member, role, false, endsAt)}
            """);
    }

    private static async Task<DateTime?> LapseAsync(Site site, Guid member)
    {
        await using var db = Context(site.App, site.Tenant);
        return await db.TenantMembers.AsNoTracking().Where(m => m.Id == member).Select(m => m.NextReasonLapseAt).SingleAsync();
    }

    private static async Task<DateTime> UpdatedAtAsync(Site site, Guid member)
    {
        await using var db = Context(site.App, site.Tenant);
        return await db.TenantMembers.AsNoTracking().Where(m => m.Id == member).Select(m => m.SysUpdatedAt).SingleAsync();
    }

    private static async Task<List<Guid>> RolesAsync(Site site, Guid member) =>
        (await site.Members.GetMemberAsync(site.Tenant, member, Ct))!.Roles.Select(r => r.RoleId).ToList();

    [Fact]
    public async Task A_due_row_is_not_live_for_any_live_reader_until_it_is_recomputed()
    {
        var site = await SiteAsync("live");
        var lapsing = await LapsingAsync(site, "live-member");

        async Task<bool[]> SeenAtAsync(DateTime now) =>
        [
            (await site.Members.ListLivePeopleAsync(site.Tenant, now, Ct)).Any(m => m.Id == lapsing.Member),
            (await site.Members.ListTenantsOfSubjectAsync(lapsing.Subject, activeOnly: true, now, Ct)).Any(t => t.Id == site.Tenant),
            (await site.Members.ListMembershipsOfSubjectAsync(lapsing.Subject, now, Ct)).Any(m => m.Tenant.Id == site.Tenant),
        ];

        (await SeenAtAsync(T0)).Should().AllBeEquivalentTo(true);
        (await SeenAtAsync(After)).Should().AllBeEquivalentTo(false);

        await MembershipReasons.RecomputeAsync(site.Factory, site.Tenant, lapsing.Subject, After, Ct);

        (await SeenAtAsync(After)).Should().AllBeEquivalentTo(true);
        (await RolesAsync(site, lapsing.Member)).Should().Equal(site.ViewerRole);
        (await LapseAsync(site, lapsing.Member)).Should().BeNull();
    }

    public enum RecomputingRead
    {
        RequestMembership,
        DirectGrantMembership,
        MemberAccess,
        IsMember,
        RoleNames,
        UsernameSignIn,
        EmailSignIn,
    }

    [Theory]
    [InlineData(RecomputingRead.RequestMembership)]
    [InlineData(RecomputingRead.DirectGrantMembership)]
    [InlineData(RecomputingRead.MemberAccess)]
    [InlineData(RecomputingRead.IsMember)]
    [InlineData(RecomputingRead.RoleNames)]
    [InlineData(RecomputingRead.UsernameSignIn)]
    [InlineData(RecomputingRead.EmailSignIn)]
    public async Task Each_read_that_recomputes_returns_the_narrowed_membership_on_its_first_call_after_the_lapse(RecomputingRead path)
    {
        var site = await SiteAsync($"read-{(int)path}");
        var lapsing = await LapsingAsync(site, $"read-{path}-member");
        var hash = $"lapse-{Guid.NewGuid():N}";
        await new OAuthGrantRepository(site.Factory).CreateAsync(
            new NewOAuthGrant(null, lapsing.Subject, OAuthGrantTypes.Direct, [Scope.GlucoseRead], "lapse", hash, null, null, false), Ct);
        var grants = new OAuthGrantRepository(site.Factory);
        var subjects = new SubjectRepository(site.Factory, AccountLinkRoutes.Off);
        var username = $"lapse-{lapsing.Subject:N}";
        var email = $"{username}@example.com";
        await NpgsqlTestCommands.ExecuteAsync(site.Migrator, $"""
            UPDATE subjects SET username = '{username}', email = '{email}', email_confirmed_at = now()
             WHERE id = '{lapsing.Subject}';
            """);
        var member = new HashSet<string> { "member" };

        async Task<IReadOnlySet<string>?> ReadAsync(DateTime now) => path switch
        {
            RecomputingRead.RequestMembership =>
                (await site.Members.ReadSubjectMembershipAsync(lapsing.Subject, site.Tenant, now, Ct))?.Membership?.Access.EffectivePermissions,
            RecomputingRead.DirectGrantMembership =>
                (await grants.FindLiveDirectGrantAsync(site.Tenant, new DirectGrantLookup.ByTokenHash([hash]), now, Ct))?.Membership?.Access.EffectivePermissions,
            RecomputingRead.MemberAccess =>
                (await site.Members.GetMemberAccessAsync(site.Tenant, lapsing.Subject, now, Ct))?.EffectivePermissions,
            RecomputingRead.IsMember =>
                await site.Members.IsMemberAsync(site.Tenant, lapsing.Subject, now, Ct) ? member : null,
            RecomputingRead.UsernameSignIn =>
                (await subjects.FindMemberByUsernameAsync(site.Tenant, username, activeOnly: true, now, Ct))?.Id == lapsing.Subject ? member : null,
            RecomputingRead.EmailSignIn =>
                (await subjects.FindMembersByConfirmedEmailAsync(site.Tenant, email, now, Ct)).Any(s => s.Id == lapsing.Subject) ? member : null,
            _ => (await site.Members.GetMemberRoleNamesAsync(site.Tenant, lapsing.Subject, now, Ct)).ToHashSet(),
        };

        async Task<IReadOnlySet<string>> ExpectedAsync(params Guid[] roleIds)
        {
            if (path is RecomputingRead.IsMember or RecomputingRead.UsernameSignIn or RecomputingRead.EmailSignIn)
                return member;
            var roles = (await site.Members.ListRolesAsync(site.Tenant, Ct)).Where(r => roleIds.Contains(r.Id)).ToList();
            return path == RecomputingRead.RoleNames
                ? roles.Select(r => r.Name).ToHashSet()
                : MemberPermissions.Effective(roles.Select(r => r.Permissions), null).ToHashSet();
        }

        (await ReadAsync(T0)).Should().BeEquivalentTo(await ExpectedAsync(site.ViewerRole, site.CaretakerRole));
        (await LapseAsync(site, lapsing.Member)).Should().Be(CampEnds, "a read before the lapse recomputes nothing");

        (await ReadAsync(After)).Should().BeEquivalentTo(await ExpectedAsync(site.ViewerRole));

        (await LapseAsync(site, lapsing.Member)).Should().BeNull();
        (await RolesAsync(site, lapsing.Member)).Should().Equal(site.ViewerRole);
    }

    [Fact]
    public async Task The_sweep_recomputes_a_due_row_and_carries_on_past_a_row_that_fails()
    {
        var fresh = await SharedPostgres.CreateMigratedDatabaseAsync("membership_lapse_sweep");
        var site = await SiteAsync("sweep", (fresh.AppConnectionString, fresh.MigratorConnectionString));
        var bad = await LapsingAsync(site, "sweep-bad");
        var good = await LapsingAsync(site, "sweep-good");
        await NpgsqlTestCommands.ExecuteAsync(site.Migrator, $$"""
            UPDATE tenant_member_reasons SET direct_permissions = '{"not":"a list"}'::jsonb
             WHERE tenant_member_id = '{{bad.Member}}' AND kind = 'direct';
            """);

        var sweep = await new MembershipReasonLapses(site.Factory, NullLogger<MembershipReasonLapses>.Instance)
            .NarrowLapsedAsync(After, Ct);

        sweep.Should().Be(new MembershipLapseSweep(Recomputed: 1, Failed: 1));
        (await LapseAsync(site, good.Member)).Should().BeNull();
        (await RolesAsync(site, good.Member)).Should().Equal(site.ViewerRole);
        (await LapseAsync(site, bad.Member)).Should().Be(CampEnds);
    }

    [Fact]
    public async Task Recompute_is_idempotent()
    {
        var site = await SiteAsync("idempotent");
        var lapsing = await LapsingAsync(site, "idempotent-member");

        await MembershipReasons.RecomputeAsync(site.Factory, site.Tenant, lapsing.Subject, After, Ct);
        var updatedAt = await UpdatedAtAsync(site, lapsing.Member);
        await MembershipReasons.RecomputeAsync(site.Factory, site.Tenant, lapsing.Subject, After, Ct);

        (await UpdatedAtAsync(site, lapsing.Member)).Should().Be(updatedAt);
        (await RolesAsync(site, lapsing.Member)).Should().Equal(site.ViewerRole);
        (await LapseAsync(site, lapsing.Member)).Should().BeNull();
    }

    [Fact]
    public async Task A_recompute_and_a_write_at_once_lose_neither()
    {
        var site = await SiteAsync("race");
        var lapsing = await LapsingAsync(site, "race-member");

        await using var holder = Context(site.App, site.Tenant);
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.LockMembershipAsync(site.Tenant, lapsing.Subject, Ct);

        var recompute = MembershipReasons.RecomputeAsync(site.Factory, site.Tenant, lapsing.Subject, After, Ct);
        var write = site.Members.SetDirectPermissionsAsync(site.Tenant, lapsing.Member, ["glucose.read"], After, Ct);
        await PostgresLockWaits.UntilWaitingAsync(site.App, 2, recompute, write);
        await held.RollbackAsync();
        await Task.WhenAll(recompute, write).WaitAsync(Eventually.DefaultPatience);

        var row = (await site.Members.GetMemberAsync(site.Tenant, lapsing.Member, Ct))!;
        row.DirectPermissions.Should().Equal("glucose.read");
        row.Roles.Select(r => r.RoleId).Should().Equal(site.ViewerRole);
        (await LapseAsync(site, lapsing.Member)).Should().BeNull();
        await using var db = Context(site.App, site.Tenant);
        (await MembershipReasons.MatchesAsync(db, lapsing.Member, After, Ct)).Should().BeTrue();
    }

    public enum Writer
    {
        Add,
        SetDirectPermissions,
        SetLimitTo24Hours,
        SetAccessEndUnlessOwner,
        ReplaceRoles,
        ReplaceRolesUnlessLastOwner,
        RemoveMemberUnlessLastOwner,
        DeleteRole,
        RemoveMembershipsOfSubject,
        Recompute,
        DeleteSubject,
        RemoveMembersOfTenant,
    }

    [Theory]
    [InlineData(Writer.Add)]
    [InlineData(Writer.SetDirectPermissions)]
    [InlineData(Writer.SetLimitTo24Hours)]
    [InlineData(Writer.SetAccessEndUnlessOwner)]
    [InlineData(Writer.ReplaceRoles)]
    [InlineData(Writer.ReplaceRolesUnlessLastOwner)]
    [InlineData(Writer.RemoveMemberUnlessLastOwner)]
    [InlineData(Writer.DeleteRole)]
    [InlineData(Writer.RemoveMembershipsOfSubject)]
    [InlineData(Writer.Recompute)]
    [InlineData(Writer.DeleteSubject)]
    [InlineData(Writer.RemoveMembersOfTenant)]
    public async Task Every_writer_of_a_membership_waits_on_its_lock(Writer writer)
    {
        var site = await SiteAsync($"lock-{(int)writer}");
        var subject = await NewSubjectAsync(site.Factory, $"lock-{writer}-member");
        var member = Guid.Empty;
        if (writer != Writer.Add)
        {
            (await site.Members.AddMemberAsync(new NewTenantMember(site.Tenant, subject, [site.ViewerRole, site.CaretakerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct))
                .Should().BeTrue();
            member = (await site.Members.FindMemberAsync(site.Tenant, subject, Ct))!.Id;
        }

        await using var holder = Context(site.App, site.Tenant);
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.LockMembershipAsync(site.Tenant, subject, Ct);

        await using var own = Context(site.App, site.Tenant);
        Task write = writer switch
        {
            Writer.Add => site.Members.AddMemberAsync(new NewTenantMember(site.Tenant, subject, [site.ViewerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct),
            Writer.SetDirectPermissions => site.Members.SetDirectPermissionsAsync(site.Tenant, member, ["glucose.read"], T0, Ct),
            Writer.SetLimitTo24Hours => site.Members.SetLimitTo24HoursAsync(site.Tenant, member, true, T0, Ct),
            Writer.SetAccessEndUnlessOwner => OwnerGuardedWrites.SetAccessEndUnlessOwnerAsync(site.Factory, AccountLinkRoutes.Off, site.Tenant, member, After, T0, Ct),
            Writer.ReplaceRoles => site.Members.ReplaceRolesAsync(site.Tenant, member, [site.CaretakerRole], [], T0, Ct),
            Writer.ReplaceRolesUnlessLastOwner => OwnerGuardedWrites.ReplaceRolesUnlessLastOwnerAsync(
                site.Factory, AccountLinkRoutes.Off, site.Tenant, member, [site.CaretakerRole], [], T0, Ct),
            Writer.RemoveMemberUnlessLastOwner => OwnerGuardedWrites.RemoveMemberUnlessLastOwnerAsync(
                site.Factory, AccountLinkRoutes.Off, site.Tenant, member, MembershipEndHow.Removed, T0, Ct),
            Writer.DeleteRole => site.Members.DeleteRoleAsync(site.Tenant, site.CaretakerRole, T0, Ct),
            Writer.RemoveMembershipsOfSubject => site.Members.RemoveMembershipsOfSubjectAsync(subject, Ct),
            Writer.Recompute => MembershipReasons.RecomputeAsync(site.Factory, site.Tenant, subject, T0, Ct),
            Writer.DeleteSubject => new SubjectRepository(site.Factory, AccountLinkRoutes.Off).DeleteAsync(subject, Ct),
            _ => DevSnapshotMemberships.RemoveMembersOfTenantAsync(own, site.Tenant, Ct),
        };

        await PostgresLockWaits.UntilBlockedAsync(site.App, write);
        await held.RollbackAsync();
        await write.WaitAsync(Eventually.DefaultPatience);
    }

    [Fact]
    public async Task The_startup_check_recomputes_a_due_row_before_it_compares()
    {
        var fresh = await SharedPostgres.CreateMigratedDatabaseAsync("membership_lapse_startup");
        var site = await SiteAsync("startup", (fresh.AppConnectionString, fresh.MigratorConnectionString));
        var lapsing = await LapsingAsync(site, "startup-member");

        var logger = new RecordingLogger();
        await DatabaseInitializationExtensions.ReconcileMembershipReasonsAsync(site.Migrator, After, TeamWrites.Apply(After), logger, Ct);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains("Recomputed 1 memberships"));
        logger.Entries.Should().HaveCount(1);
        (await RolesAsync(site, lapsing.Member)).Should().Equal(site.ViewerRole);
        (await LapseAsync(site, lapsing.Member)).Should().BeNull();
    }

    [Fact]
    public async Task The_startup_check_projects_a_member_with_a_reason_of_another_kind_from_its_reasons()
    {
        var fresh = await SharedPostgres.CreateMigratedDatabaseAsync("membership_lapse_reasons_win");
        var site = await SiteAsync("reasons-win", (fresh.AppConnectionString, fresh.MigratorConnectionString));
        var subject = await NewSubjectAsync(site.Factory, "reasons-win-member");
        (await site.Members.AddMemberAsync(new NewTenantMember(site.Tenant, subject, [site.ViewerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        var member = (await site.Members.FindMemberAsync(site.Tenant, subject, Ct))!.Id;
        await AddReasonAsync(site, member, site.CaretakerRole, endsAt: null);

        var logger = new RecordingLogger();
        await DatabaseInitializationExtensions.ReconcileMembershipReasonsAsync(site.Migrator, T0, TeamWrites.Apply(T0), logger, Ct);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains(member.ToString()));
        (await RolesAsync(site, member)).Should().BeEquivalentTo([site.ViewerRole, site.CaretakerRole]);
        await using var db = Context(site.App, site.Tenant);
        var direct = await db.TenantMemberReasons.AsNoTracking().Include(r => r.ReasonRoles)
            .SingleAsync(r => r.TenantMemberId == member && r.Kind == MembershipReasonKind.Direct);
        direct.ReasonRoles.Select(rr => rr.TenantRoleId).Should().Equal(site.ViewerRole);
        direct.DirectPermissions.Should().BeNull();
        (await MembershipReasons.MatchesAsync(db, member, T0, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_recompute_that_fails_reads_as_no_membership_and_leaves_the_next_member_alone()
    {
        var site = await SiteAsync("fails");
        var bad = await LapsingAsync(site, "fails-bad");
        var good = await LapsingAsync(site, "fails-good");
        await NpgsqlTestCommands.ExecuteAsync(site.Migrator, $$"""
            UPDATE tenant_member_reasons SET direct_permissions = '{"not":"a list"}'::jsonb
             WHERE tenant_member_id = '{{bad.Member}}' AND kind = 'direct';
            """);

        (await site.Members.GetMemberAccessAsync(site.Tenant, bad.Subject, After, Ct)).Should().BeNull();
        (await site.Members.ReadSubjectMembershipAsync(bad.Subject, site.Tenant, After, Ct))!.Membership.Should().BeNull();
        (await LapseAsync(site, bad.Member)).Should().Be(CampEnds);

        var viewer = (await site.Members.ListRolesAsync(site.Tenant, Ct)).Single(r => r.Id == site.ViewerRole).Permissions;
        (await site.Members.GetMemberAccessAsync(site.Tenant, good.Subject, After, Ct))!.EffectivePermissions
            .Should().BeEquivalentTo(viewer);
        (await LapseAsync(site, good.Member)).Should().BeNull();
    }

    [Fact]
    public async Task A_read_over_a_unit_of_work_reads_a_due_row_as_absent_and_recomputes_nothing()
    {
        var site = await SiteAsync("unit");
        var lapsing = await LapsingAsync(site, "unit-member");
        await using var unit = DbUnitOfWork.Own(Context(site.App, site.Tenant));
        var members = new TenantMembershipRepository(unit, AccountLinkRoutes.Off);

        (await members.GetMemberAccessAsync(site.Tenant, lapsing.Subject, T0, Ct)).Should().NotBeNull();
        (await members.GetMemberAccessAsync(site.Tenant, lapsing.Subject, After, Ct)).Should().BeNull();
        (await members.IsMemberAsync(site.Tenant, lapsing.Subject, After, Ct)).Should().BeFalse();

        (await LapseAsync(site, lapsing.Member)).Should().Be(CampEnds);
    }

    [Fact]
    public async Task A_row_is_due_from_the_instant_its_reason_lapses()
    {
        var site = await SiteAsync("boundary");
        var lapsing = await LapsingAsync(site, "boundary-member");
        var justBefore = CampEnds.AddMicroseconds(-1);

        (await site.Members.ListLivePeopleAsync(site.Tenant, justBefore, Ct)).Should().Contain(m => m.Id == lapsing.Member);
        (await site.Members.ListLivePeopleAsync(site.Tenant, CampEnds, Ct)).Should().NotContain(m => m.Id == lapsing.Member);

        (await site.Members.GetMemberRoleNamesAsync(site.Tenant, lapsing.Subject, justBefore, Ct)).Should().HaveCount(2);
        (await LapseAsync(site, lapsing.Member)).Should().Be(CampEnds);

        var viewer = (await site.Members.ListRolesAsync(site.Tenant, Ct)).Single(r => r.Id == site.ViewerRole).Name;
        (await site.Members.GetMemberRoleNamesAsync(site.Tenant, lapsing.Subject, CampEnds, Ct)).Should().Equal(viewer);
        (await LapseAsync(site, lapsing.Member)).Should().BeNull();
    }

    [Fact]
    public async Task A_resync_clears_a_next_lapse_the_one_reason_cannot_give()
    {
        var fresh = await SharedPostgres.CreateMigratedDatabaseAsync("membership_lapse_resync");
        var site = await SiteAsync("resync", (fresh.AppConnectionString, fresh.MigratorConnectionString));
        var written = await DirectOnlyAsync(site, "resync-written");
        var started = await DirectOnlyAsync(site, "resync-started");
        await NpgsqlTestCommands.ExecuteAsync(site.Migrator, $"""
            UPDATE tenant_members SET next_reason_lapse_at = '{CampEnds:O}' WHERE id IN ('{written}', '{started}');
            """);

        (await site.Members.SetLimitTo24HoursAsync(site.Tenant, written, false, T0, Ct)).Should().BeTrue();
        (await LapseAsync(site, written)).Should().BeNull();

        var logger = new RecordingLogger();
        await DatabaseInitializationExtensions.ReconcileMembershipReasonsAsync(site.Migrator, T0, TeamWrites.Apply(T0), logger, Ct);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains(started.ToString()));
        (await LapseAsync(site, started)).Should().BeNull();

        await using var db = Context(site.App, site.Tenant);
        (await MembershipReasons.MatchesAsync(db, written, T0, Ct)).Should().BeTrue();
        (await MembershipReasons.MatchesAsync(db, started, T0, Ct)).Should().BeTrue();
        (await RolesAsync(site, started)).Should().Equal(site.ViewerRole);
    }

    [Fact]
    public async Task Setup_adoption_waits_on_the_lock_of_the_membership_it_moves_to()
    {
        var site = await SiteAsync("adopt");
        var subjects = new SubjectRepository(site.Factory, AccountLinkRoutes.Off);
        var created = await subjects.CreateSetupOwnerAsync(site.Tenant, new NewSubject("Owner", Username: $"owner-{Guid.NewGuid():N}"), Ct);
        var ownerRole = (await site.Members.FindRoleIdBySlugAsync(site.Tenant, RoleSeeds.Owner, Ct))!.Value;
        (await site.Members.AddMemberAsync(new NewTenantMember(site.Tenant, created, [ownerRole]) { How = MembershipJoinHow.Setup, By = null }, Ct))
            .Should().BeTrue();
        var holder = await subjects.CreateAsync(new NewSubject("Holder"), Ct);
        var provider = Guid.NewGuid();
        await using (var db = Context(site.App, Guid.Empty))
        {
            db.OidcProviders.Add(new OidcProviderEntity
            {
                Id = provider, Name = "Keycloak", IssuerUrl = "https://issuer.example", ClientId = "nocturne", IsEnabled = true,
            });
            await db.SaveChangesAsync();
        }
        var identity = await subjects.AddOidcIdentityAsync(
            new NewSubjectOidcIdentity(holder, provider, $"ext-{Guid.NewGuid():N}", "https://issuer.example", null, T0), Ct);
        var slug = (await new TenantRepository(site.Factory).GetAsync(site.Tenant, Ct))!.Slug;

        await using var holding = Context(site.App, site.Tenant);
        await using var held = await holding.Database.BeginTransactionAsync();
        await holding.LockMembershipAsync(site.Tenant, holder, Ct);

        var adoption = subjects.AdoptForSetupAsync(created, holder, identity, slug, Ct);
        await PostgresLockWaits.UntilBlockedAsync(site.App, adoption);
        await held.RollbackAsync();

        (await adoption.WaitAsync(Eventually.DefaultPatience)).Should().BeTrue();
        (await site.Members.FindMemberAsync(site.Tenant, holder, Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task The_device_subject_is_made_holding_a_membership_lock()
    {
        var site = await SiteAsync("device");
        await using var db = Context(site.App, site.Tenant);
        await using var tx = await db.Database.BeginTransactionAsync();

        await db.DeviceSubjectOf(site.Tenant, Ct);

        (await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pg_locks
             WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND objsubid = 2
               AND classid = {(long)MembershipLock.Class}
            """).SingleAsync()).Should().Be(1);
        await tx.RollbackAsync();
    }

    /// <summary>A viewer whose one reason is direct.</summary>
    private static async Task<Guid> DirectOnlyAsync(Site site, string name)
    {
        var subject = await NewSubjectAsync(site.Factory, name);
        (await site.Members.AddMemberAsync(new NewTenantMember(site.Tenant, subject, [site.ViewerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        return (await site.Members.FindMemberAsync(site.Tenant, subject, Ct))!.Id;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
