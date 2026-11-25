using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Auth;
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
/// <see cref="MembershipReasons"/> on PostgreSQL under the runtime role: after every write that
/// changes a member, the row is the sum of its reasons by <see cref="MembershipReasons.Matches"/>,
/// the function the startup check uses; a row with no reason is adopted on its first change with
/// the audit the change alone writes; and the startup check adopts, re-syncs from the row, and
/// leaves alone what it should.
/// </summary>
[Trait("Category", "Integration")]
public class MembershipReasonsPostgresTests(MembershipReasonsPostgresTests.Database database)
    : IClassFixture<MembershipReasonsPostgresTests.Database>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string[] AccessEvents =
    [
        AuthAuditEventType.RoleAssigned, AuthAuditEventType.RoleRemoved,
        AuthAuditEventType.HistoryLimitChanged, AuthAuditEventType.AccessEndChanged,
    ];

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("membership_reasons");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private static NocturneDbContext Context(string connectionString, Guid tenant) =>
        TestContexts.Context(connectionString, tenant);

    private sealed record Seeded(
        Guid Tenant, SuppliedContextFactory Factory, TenantMembershipRepository Members, Guid Owner, Guid OwnerRole, Guid ViewerRole,
        Guid CaretakerRole);

    private async Task<Seeded> SeedTenantAsync(string slug, string? connectionString = null)
    {
        var app = connectionString ?? database.AppConnectionString;
        var tenant = Guid.NewGuid();
        await NpgsqlTestCommands.ExecuteAsync(Migrator(app), $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', '{slug}-{tenant:N}', 'membership-reasons', true, now(), now());
            """);

        var factory = new SuppliedContextFactory(() => Context(app, tenant));
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        var owner = await new SubjectRepository(factory, AccountLinkRoutes.Off).CreateAsync(new NewSubject($"{slug}-owner"), Ct);
        await members.SeedRolesAsync(tenant, Ct);
        var ownerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, Ct))!.Value;
        var viewerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Viewer, Ct))!.Value;
        var caretakerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, Ct))!.Value;
        (await members.AddMemberAsync(new NewTenantMember(tenant, owner, [ownerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct)).Should().BeTrue();
        return new Seeded(tenant, factory, members, owner, ownerRole, viewerRole, caretakerRole);
    }

    private async Task<Guid> NewSubjectAsync(Seeded seeded, string name) =>
        await new SubjectRepository(seeded.Factory, AccountLinkRoutes.Off).CreateAsync(new NewSubject(name), Ct);

    private async Task<Guid> MemberIdAsync(Seeded seeded, Guid subject) =>
        (await seeded.Members.FindMemberAsync(seeded.Tenant, subject, Ct))!.Id;

    /// <summary>Every member of the tenant matches its reasons and holds exactly one, the direct reason.</summary>
    private static async Task AssertEveryMemberMatchesAsync(Seeded seeded)
    {
        await using var db = await seeded.Factory.CreateForTenantAsync(seeded.Tenant);
        var memberIds = await db.TenantMembers.Where(m => m.TenantId == seeded.Tenant).Select(m => m.Id).ToListAsync();
        memberIds.Should().NotBeEmpty();
        foreach (var memberId in memberIds)
        {
            (await MembershipReasons.MatchesAsync(db, memberId, Now, Ct)).Should().BeTrue($"member {memberId} is the sum of its reasons");
            (await db.TenantMemberReasons.Where(r => r.TenantMemberId == memberId).Select(r => r.Kind).ToListAsync())
                .Should().Equal(MembershipReasonKind.Direct);
        }
    }

    private string Migrator(string app) =>
        app == database.AppConnectionString ? database.MigratorConnectionString : _migrators[app];

    private readonly Dictionary<string, string> _migrators = [];

    [Fact]
    public async Task EveryRepositoryWrite_LeavesEachRowTheSumOfItsReasons()
    {
        var s = await SeedTenantAsync("writes");
        var invite = Guid.CreateVersion7();
        await using (var conn = await NpgsqlTestCommands.OpenAsync(database.MigratorConnectionString))
        {
            await conn.PinTenantAsync(s.Tenant);
            await conn.ExecuteAsync($"""
            INSERT INTO member_invites (id, tenant_id, created_by_subject_id, token_hash, limit_to_24_hours, expires_at, use_count, created_at)
            VALUES ('{invite}', '{s.Tenant}', '{s.Owner}', '{invite:N}', false, now() + interval '1 day', 0, now());
            """);
        }

        var subject = await NewSubjectAsync(s, "writes-member");

        (await s.Members.AddMemberAsync(new NewTenantMember(
            s.Tenant, subject, [s.ViewerRole, s.CaretakerRole], ["glucose.read", "treatments.read"], LimitTo24Hours: true,
            CreatedFromInviteId: invite, AccessEndsAt: Now.AddDays(30)) { How = MembershipJoinHow.Direct, By = null }, Ct)).Should().BeTrue();
        await AssertEveryMemberMatchesAsync(s);
        var member = await MemberIdAsync(s, subject);
        (await s.Members.GetMemberAsync(s.Tenant, member, Ct))!.CreatedFromInviteId.Should().Be(invite);

        await s.Members.SetDirectPermissionsAsync(s.Tenant, member, [], Now, Ct);
        await AssertEveryMemberMatchesAsync(s);
        (await s.Members.GetMemberAsync(s.Tenant, member, Ct))!.DirectPermissions.Should().NotBeNull().And.BeEmpty();

        await s.Members.SetDirectPermissionsAsync(s.Tenant, member, null, Now, Ct);
        await AssertEveryMemberMatchesAsync(s);

        await s.Members.SetLimitTo24HoursAsync(s.Tenant, member, false, Now, Ct);
        await AssertEveryMemberMatchesAsync(s);

        (await OwnerGuardedWrites.SetAccessEndUnlessOwnerAsync(s.Factory, AccountLinkRoutes.Off, s.Tenant, member, null, Now, Ct)).Should().Be(OwnerGuardedChange.Changed);
        await AssertEveryMemberMatchesAsync(s);

        (await s.Members.ReplaceRolesAsync(s.Tenant, member, [s.ViewerRole], [s.OwnerRole], Now, Ct)).Should().BeTrue();
        await AssertEveryMemberMatchesAsync(s);

        (await OwnerGuardedWrites.ReplaceRolesUnlessLastOwnerAsync(s.Factory, AccountLinkRoutes.Off, s.Tenant, member, [s.OwnerRole], [s.ViewerRole], Now, Ct))
            .Should().Be(OwnerGuardedChange.Changed);
        await AssertEveryMemberMatchesAsync(s);
        (await s.Members.GetMemberAsync(s.Tenant, member, Ct))!.Roles.Select(r => r.RoleId)
            .Should().BeEquivalentTo([s.CaretakerRole, s.ViewerRole]);

        (await s.Members.DeleteRoleAsync(s.Tenant, s.CaretakerRole, Now, Ct)).Should().Be(RoleChangeOutcome.Changed);
        await AssertEveryMemberMatchesAsync(s);
        (await NpgsqlTestCommands.ScalarAsync<long>(database.MigratorConnectionString,
            $"SELECT count(*) FROM tenant_member_reason_roles WHERE tenant_role_id = '{s.CaretakerRole}'")).Should().Be(0);

        (await OwnerGuardedWrites.RemoveMemberUnlessLastOwnerAsync(s.Factory, AccountLinkRoutes.Off, s.Tenant, member, MembershipEndHow.Removed, Now, Ct)).Outcome.Should().Be(OwnerGuardedChange.Changed);
        await AssertEveryMemberMatchesAsync(s);
        (await NpgsqlTestCommands.ScalarAsync<long>(database.MigratorConnectionString,
            $"SELECT count(*) FROM tenant_member_reasons WHERE tenant_member_id = '{member}'")).Should().Be(0);
    }

    [Fact]
    public async Task RemovingASubjectsMemberships_RemovesTheirReasons()
    {
        var a = await SeedTenantAsync("subject-a");
        var b = await SeedTenantAsync("subject-b");
        var subject = await NewSubjectAsync(a, "everywhere");
        await a.Members.AddMemberAsync(new NewTenantMember(a.Tenant, subject, [a.ViewerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct);
        await b.Members.AddMemberAsync(new NewTenantMember(b.Tenant, subject, [b.ViewerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct);
        var memberIds = new[] { await MemberIdAsync(a, subject), await MemberIdAsync(b, subject) };

        (await a.Members.RemoveMembershipsOfSubjectAsync(subject, Ct)).Removed.Should().Be(2);

        await AssertEveryMemberMatchesAsync(a);
        await AssertEveryMemberMatchesAsync(b);
        (await NpgsqlTestCommands.ScalarAsync<long>(database.MigratorConnectionString,
            $"SELECT count(*) FROM tenant_member_reasons WHERE tenant_member_id IN ('{memberIds[0]}', '{memberIds[1]}')"))
            .Should().Be(0);
    }

    [Fact]
    public async Task TheDeviceSubject_AndTheRejoinReplace_AndTheDevRestore_LeaveEachRowTheSumOfItsReasons()
    {
        var s = await SeedTenantAsync("outside");
        await using (var db = await s.Factory.CreateForTenantAsync(s.Tenant))
            await db.DeviceSubjectOf(s.Tenant, Ct);
        await AssertEveryMemberMatchesAsync(s);

        var subject = await NewSubjectAsync(s, "rejoins");
        await s.Members.AddMemberAsync(new NewTenantMember(s.Tenant, subject, [s.ViewerRole], AccessEndsAt: Now.AddDays(-1)) { How = MembershipJoinHow.Direct, By = null }, Ct);
        var ended = await MemberIdAsync(s, subject);
        await using (var unit = await DbUnitOfWork.OpenForTenantAsync(s.Factory, s.Tenant, Ct))
        {
            var members = new TenantMembershipRepository(unit, AccountLinkRoutes.Off);
            await unit.CommitAsync(async token =>
            {
                (await members.RemoveMemberUnlessLastOwnerAsync(s.Tenant, ended, MembershipEndHow.Removed, Now, token)).Outcome.Should().Be(OwnerGuardedChange.Changed);
                await unit.Context.SaveChangesAsync(token);
                (await members.AddMemberAsync(new NewTenantMember(s.Tenant, subject, [s.CaretakerRole], ["glucose.read"]) { How = MembershipJoinHow.Direct, By = null }, token))
                    .Should().BeTrue();
                await unit.Context.SaveChangesAsync(token);
            }, Ct);
        }

        await AssertEveryMemberMatchesAsync(s);
        (await s.Members.FindMemberAsync(s.Tenant, subject, Ct))!.Roles.Select(r => r.RoleId).Should().Equal(s.CaretakerRole);

        await using (var db = await s.Factory.CreateForTenantAsync(s.Tenant))
        {
            var rows = await db.TenantMembers.Where(m => m.TenantId == s.Tenant).ToListAsync();
            await DevSnapshotMemberships.RemoveMembersOfTenantAsync(db, s.Tenant, Ct);
            await db.SaveChangesAsync();
            foreach (var row in rows)
            {
                DevSnapshotMemberships.Restore(
                    db,
                    new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = row.TenantId, SubjectId = row.SubjectId },
                    new MembershipGrant([s.ViewerRole], row.DirectPermissions, row.LimitTo24Hours, AccessEndsAt: null),
                    createdFromInviteId: null);
            }

            await db.SaveChangesAsync();
        }

        await AssertEveryMemberMatchesAsync(s);
    }

    [Fact]
    public async Task ARowWithNoReason_IsAdoptedOnItsFirstChange_WithTheAuditTheChangeAloneWrites()
    {
        var s = await SeedTenantAsync("adopt");
        var limitSubject = await NewSubjectAsync(s, "adopt-limit");
        var rolesSubject = await NewSubjectAsync(s, "adopt-roles");
        var unchangedSubject = await NewSubjectAsync(s, "adopt-unchanged");
        var limitMember = await SeedBareMemberAsync(s, limitSubject, """["glucose.read","glucose.read"]""", s.ViewerRole);
        var rolesMember = await SeedBareMemberAsync(s, rolesSubject, "NULL", s.ViewerRole);
        var unchangedMember = await SeedBareMemberAsync(s, unchangedSubject, "[]", s.ViewerRole);

        await s.Members.SetLimitTo24HoursAsync(s.Tenant, limitMember, true, Now, Ct);
        await s.Members.ReplaceRolesAsync(s.Tenant, rolesMember, [s.ViewerRole], [s.CaretakerRole], Now, Ct);
        await s.Members.SetLimitTo24HoursAsync(s.Tenant, unchangedMember, false, Now, Ct);

        await AssertEveryMemberMatchesAsync(s);
        await using var db = await s.Factory.CreateForTenantAsync(s.Tenant);

        var limitReason = await db.TenantMemberReasons.Include(r => r.ReasonRoles).SingleAsync(r => r.TenantMemberId == limitMember);
        limitReason.DirectPermissions.Should().Equal("glucose.read", "glucose.read");
        limitReason.LimitTo24Hours.Should().BeTrue();
        limitReason.ReasonRoles.Select(rr => rr.TenantRoleId).Should().Equal(s.ViewerRole);
        (await AccessAuditAsync(db, limitSubject)).Should().Equal(AuthAuditEventType.HistoryLimitChanged);

        var rolesReason = await db.TenantMemberReasons.Include(r => r.ReasonRoles).SingleAsync(r => r.TenantMemberId == rolesMember);
        rolesReason.DirectPermissions.Should().BeNull();
        rolesReason.ReasonRoles.Select(rr => rr.TenantRoleId).Should().Equal(s.CaretakerRole);
        (await AccessAuditAsync(db, rolesSubject)).Should().BeEquivalentTo([AuthAuditEventType.RoleRemoved, AuthAuditEventType.RoleAssigned]);

        var unchangedReason = await db.TenantMemberReasons.SingleAsync(r => r.TenantMemberId == unchangedMember);
        unchangedReason.DirectPermissions.Should().NotBeNull().And.BeEmpty();
        unchangedReason.LimitTo24Hours.Should().BeFalse();
        (await AccessAuditAsync(db, unchangedSubject)).Should().BeEmpty("adoption alone stages no audit row");
        var unchangedRow = await db.TenantMembers.AsNoTracking().SingleAsync(m => m.Id == unchangedMember);
        unchangedRow.DirectPermissions.Should().NotBeNull().And.BeEmpty();
        unchangedRow.LimitTo24Hours.Should().BeFalse();
        unchangedRow.AccessEndsAt.Should().BeNull();
        (await db.TenantMemberRoles.Where(mr => mr.TenantMemberId == unchangedMember).Select(mr => mr.TenantRoleId).ToListAsync())
            .Should().Equal(s.ViewerRole);
    }

    [Fact]
    public async Task TheStartupCheck_AdoptsResyncsAndLeavesAlone_AndLogsWhatItDid()
    {
        var (app, migrator) = await FreshDatabaseAsync("reasons_check");
        var s = await SeedTenantAsync("check", app);
        var bareSubject = await NewSubjectAsync(s, "check-bare");
        var driftSubject = await NewSubjectAsync(s, "check-drift");
        var teamSubject = await NewSubjectAsync(s, "check-team");
        var bare = await SeedBareMemberAsync(s, bareSubject, """["glucose.read"]""", s.ViewerRole, migrator);
        await s.Members.AddMemberAsync(new NewTenantMember(s.Tenant, driftSubject, [s.ViewerRole], ["glucose.read"]) { How = MembershipJoinHow.Direct, By = null }, Ct);
        await s.Members.AddMemberAsync(new NewTenantMember(s.Tenant, teamSubject, [s.ViewerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct);
        var drift = await MemberIdAsync(s, driftSubject);
        var team = await MemberIdAsync(s, teamSubject);
        var (teamAccessSql, teamAccess) = TeamReasonSql.NewAccess(s.Tenant, "Clinic");
        await NpgsqlTestCommands.ExecuteAsync(migrator, $"""
            {teamAccessSql}
            UPDATE tenant_members SET direct_permissions = NULL, limit_to_24_hours = true WHERE id IN ('{drift}', '{team}');
            DELETE FROM tenant_member_roles WHERE tenant_member_id = '{drift}';
            INSERT INTO tenant_member_reasons (id, tenant_member_id, kind, source_id, limit_to_24_hours)
            VALUES ('{Guid.CreateVersion7()}', '{team}', 'team', '{teamAccess}', false);
            {TeamReasonSql.StaffOfMember(teamAccess, team, null, false, null)}
            """);
        var rowsBefore = await RowsAsync(migrator);
        var teamReasonsBefore = await NpgsqlTestCommands.ScalarAsync<long>(
            migrator, $"SELECT count(*) FROM tenant_member_reasons WHERE tenant_member_id = '{team}'");

        var logger = new RecordingLogger();
        await DatabaseInitializationExtensions.ReconcileMembershipReasonsAsync(migrator, Now, TeamWrites.Apply(Now), logger, Ct);

        (await RowsAsync(migrator)).Where(r => !r.StartsWith(team.ToString())).Should().Equal(
            rowsBefore.Where(r => !r.StartsWith(team.ToString())), "the row wins over a direct reason and is never written");
        await using var db = Context(app, s.Tenant);
        var bareReason = await db.TenantMemberReasons.Include(r => r.ReasonRoles).SingleAsync(r => r.TenantMemberId == bare);
        bareReason.DirectPermissions.Should().Equal("glucose.read");
        bareReason.ReasonRoles.Select(rr => rr.TenantRoleId).Should().Equal(s.ViewerRole);
        (await MembershipReasons.MatchesAsync(db, bare, Now, Ct)).Should().BeTrue();

        var driftReason = await db.TenantMemberReasons.Include(r => r.ReasonRoles).SingleAsync(r => r.TenantMemberId == drift);
        driftReason.DirectPermissions.Should().BeNull();
        driftReason.LimitTo24Hours.Should().BeTrue();
        driftReason.ReasonRoles.Should().BeEmpty();
        (await MembershipReasons.MatchesAsync(db, drift, Now, Ct)).Should().BeTrue();

        (await NpgsqlTestCommands.ScalarAsync<long>(
            migrator, $"SELECT count(*) FROM tenant_member_reasons WHERE tenant_member_id = '{team}'"))
            .Should().Be(teamReasonsBefore);
        (await MembershipReasons.MatchesAsync(db, team, Now, Ct)).Should().BeTrue("a member with a reason of another kind is projected from its reasons");
        (await db.TenantMembers.AsNoTracking().SingleAsync(m => m.Id == team)).LimitTo24Hours.Should().BeFalse();

        logger.Entries.Should().HaveCount(3);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("Gave 1 memberships"));
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains(drift.ToString()));
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains(team.ToString()));
    }

    [Fact]
    public async Task TheStartupCheck_OnACleanDatabase_LogsAndWritesNothing()
    {
        var (app, migrator) = await FreshDatabaseAsync("reasons_clean");
        var empty = new RecordingLogger();
        await DatabaseInitializationExtensions.ReconcileMembershipReasonsAsync(migrator, Now, TeamWrites.Apply(Now), empty, Ct);
        empty.Entries.Should().BeEmpty("no table holds a row");

        var s = await SeedTenantAsync("clean", app);
        await s.Members.AddMemberAsync(new NewTenantMember(s.Tenant, await NewSubjectAsync(s, "clean-member"), [s.ViewerRole], []) { How = MembershipJoinHow.Direct, By = null }, Ct);
        var reasonsBefore = await ReasonsAsync(migrator);

        var logger = new RecordingLogger();
        await DatabaseInitializationExtensions.ReconcileMembershipReasonsAsync(migrator, Now, TeamWrites.Apply(Now), logger, Ct);

        logger.Entries.Should().BeEmpty();
        (await ReasonsAsync(migrator)).Should().Equal(reasonsBefore);
    }

    public enum StaleWrite
    {
        LostRoleThenLimit,
        LostRoleThenEnd,
        LostPermissionThenRoles,
        GainedLimitThenPermissions,
        EarlierEndThenRoleDeleted,
    }

    /// <summary>
    /// A reason an older build left behind the row is re-synced from the row before an unrelated
    /// write, so the write cannot put back what the row lost, and the audit holds only the write.
    /// </summary>
    [Theory]
    [InlineData(StaleWrite.LostRoleThenLimit)]
    [InlineData(StaleWrite.LostRoleThenEnd)]
    [InlineData(StaleWrite.LostPermissionThenRoles)]
    [InlineData(StaleWrite.GainedLimitThenPermissions)]
    [InlineData(StaleWrite.EarlierEndThenRoleDeleted)]
    public async Task AStaleReason_IsResyncedFromTheRowBeforeAWrite(StaleWrite stale)
    {
        var s = await SeedTenantAsync($"stale-{(int)stale}");
        var subject = await NewSubjectAsync(s, "stale");
        var reasonEnd = Now.AddDays(30);
        var rowEnd = Now.AddDays(5);
        await s.Members.AddMemberAsync(new NewTenantMember(
            s.Tenant, subject, [s.ViewerRole, s.CaretakerRole], ["glucose.read", "treatments.read"], AccessEndsAt: reasonEnd) { How = MembershipJoinHow.Direct, By = null }, Ct);
        var member = await MemberIdAsync(s, subject);
        var clinician = (await s.Members.FindRoleIdBySlugAsync(s.Tenant, RoleSeeds.Clinician, Ct))!.Value;

        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, stale switch
        {
            StaleWrite.LostRoleThenLimit or StaleWrite.LostRoleThenEnd =>
                $"DELETE FROM tenant_member_roles WHERE tenant_member_id = '{member}' AND tenant_role_id = '{s.ViewerRole}'",
            StaleWrite.LostPermissionThenRoles =>
                $"""UPDATE tenant_members SET direct_permissions = '["glucose.read"]' WHERE id = '{member}'""",
            StaleWrite.GainedLimitThenPermissions =>
                $"UPDATE tenant_members SET limit_to_24_hours = true WHERE id = '{member}'",
            _ => $"UPDATE tenant_members SET access_ends_at = '{rowEnd:O}' WHERE id = '{member}'",
        });

        await using (var before = await s.Factory.CreateForTenantAsync(s.Tenant))
            (await MembershipReasons.MatchesAsync(before, member, Now, Ct)).Should().BeFalse("the row has fallen out of step");
        var auditBefore = await AuditIdsAsync(s, subject);

        string[] expected;
        switch (stale)
        {
            case StaleWrite.LostRoleThenLimit:
                await s.Members.SetLimitTo24HoursAsync(s.Tenant, member, true, Now, Ct);
                expected = [AuthAuditEventType.HistoryLimitChanged];
                break;
            case StaleWrite.LostRoleThenEnd:
                (await OwnerGuardedWrites.SetAccessEndUnlessOwnerAsync(s.Factory, AccountLinkRoutes.Off, s.Tenant, member, Now.AddDays(10), Now, Ct)).Should().Be(OwnerGuardedChange.Changed);
                expected = [AuthAuditEventType.AccessEndChanged];
                break;
            case StaleWrite.LostPermissionThenRoles:
                await s.Members.ReplaceRolesAsync(s.Tenant, member, [], [clinician], Now, Ct);
                expected = [AuthAuditEventType.RoleAssigned];
                break;
            case StaleWrite.GainedLimitThenPermissions:
                await s.Members.SetDirectPermissionsAsync(s.Tenant, member, ["glucose.read", "treatments.read", "alerts.read"], Now, Ct);
                expected = [AuthAuditEventType.RoleAssigned];
                break;
            default:
                (await s.Members.DeleteRoleAsync(s.Tenant, s.CaretakerRole, Now, Ct)).Should().Be(RoleChangeOutcome.Changed);
                expected = [AuthAuditEventType.RoleRemoved];
                break;
        }

        await using var db = await s.Factory.CreateForTenantAsync(s.Tenant);
        var row = await db.TenantMembers.AsNoTracking().SingleAsync(m => m.Id == member);
        var links = await db.TenantMemberRoles.Where(mr => mr.TenantMemberId == member).Select(mr => mr.TenantRoleId).ToListAsync();
        var reason = await db.TenantMemberReasons.Include(r => r.ReasonRoles).AsNoTracking().SingleAsync(r => r.TenantMemberId == member);
        var reasonRoles = reason.ReasonRoles.Select(rr => rr.TenantRoleId).ToList();
        (await MembershipReasons.MatchesAsync(db, member, Now, Ct)).Should().BeTrue();

        switch (stale)
        {
            case StaleWrite.LostRoleThenLimit or StaleWrite.LostRoleThenEnd:
                links.Should().NotContain(s.ViewerRole);
                reasonRoles.Should().NotContain(s.ViewerRole);
                break;
            case StaleWrite.LostPermissionThenRoles:
                row.DirectPermissions.Should().Equal("glucose.read");
                reason.DirectPermissions.Should().Equal("glucose.read");
                break;
            case StaleWrite.GainedLimitThenPermissions:
                row.LimitTo24Hours.Should().BeTrue();
                reason.LimitTo24Hours.Should().BeTrue();
                break;
            default:
                row.AccessEndsAt.Should().Be(rowEnd);
                reason.AccessEndsAt.Should().Be(rowEnd);
                break;
        }

        (await AccessAuditAsync(db, subject, except: auditBefore)).Should().Equal(expected);
    }

    [Fact]
    public async Task TheStartupCheck_OverAMalformedRow_LogsAndDoesNotThrow()
    {
        var (app, migrator) = await FreshDatabaseAsync("reasons_malformed");
        var s = await SeedTenantAsync("malformed", app);
        await SeedBareMemberAsync(s, await NewSubjectAsync(s, "malformed-member"), """{"not":"a list"}""", s.ViewerRole, migrator);

        var logger = new RecordingLogger();
        var check = () => DatabaseInitializationExtensions.ReconcileMembershipReasonsAsync(migrator, Now, TeamWrites.Apply(Now), logger, Ct);

        await check.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error);
    }

    private static async Task<List<Guid>> AuditIdsAsync(Seeded seeded, Guid subject)
    {
        await using var db = await seeded.Factory.CreateForTenantAsync(seeded.Tenant);
        return await db.AuthAuditLog.AsNoTracking().Where(a => a.TargetSubjectId == subject).Select(a => a.Id).ToListAsync();
    }

    private async Task<(string App, string Migrator)> FreshDatabaseAsync(string prefix)
    {
        var fresh = await SharedPostgres.CreateMigratedDatabaseAsync(prefix);
        _migrators[fresh.AppConnectionString] = fresh.MigratorConnectionString;
        return (fresh.AppConnectionString, fresh.MigratorConnectionString);
    }

    private async Task<Guid> SeedBareMemberAsync(
        Seeded seeded, Guid subject, string permissionsJson, Guid roleId, string? migrator = null)
    {
        var member = Guid.CreateVersion7();
        var permissions = permissionsJson == "NULL" ? "NULL" : $"'{permissionsJson}'::jsonb";
        await NpgsqlTestCommands.ExecuteAsync(migrator ?? database.MigratorConnectionString, $"""
            INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, limit_to_24_hours, sys_created_at, sys_updated_at)
            VALUES ('{member}', '{seeded.Tenant}', '{subject}', {permissions}, false, now(), now());
            INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id, sys_created_at)
            VALUES ('{Guid.CreateVersion7()}', '{member}', '{roleId}', now());
            """);
        return member;
    }

    private static async Task<List<string>> AccessAuditAsync(NocturneDbContext db, Guid subject) =>
        await db.AuthAuditLog.AsNoTracking()
            .Where(a => a.TargetSubjectId == subject && AccessEvents.Contains(a.EventType))
            .Select(a => a.EventType)
            .ToListAsync();

    /// <summary>Every audit row of any type naming <paramref name="subject"/> that is not in <paramref name="except"/>.</summary>
    private static async Task<List<string>> AccessAuditAsync(NocturneDbContext db, Guid subject, List<Guid> except) =>
        await db.AuthAuditLog.AsNoTracking()
            .Where(a => a.TargetSubjectId == subject && !except.Contains(a.Id))
            .Select(a => a.EventType)
            .ToListAsync();

    private static Task<List<string>> RowsAsync(string migrator) => ReadAsync(migrator, """
        SELECT m.id::text || '|' || coalesce(m.direct_permissions::text, 'null') || '|' || m.limit_to_24_hours::text || '|'
               || coalesce(m.access_ends_at::text, 'null') || '|' || coalesce(m.created_from_invite_id::text, 'null') || '|'
               || m.sys_updated_at::text || '|'
               || coalesce((SELECT string_agg(mr.tenant_role_id::text, ',' ORDER BY mr.tenant_role_id)
                            FROM tenant_member_roles mr WHERE mr.tenant_member_id = m.id), '')
        FROM tenant_members m ORDER BY m.id
        """);

    private static Task<List<string>> ReasonsAsync(string migrator) => ReadAsync(migrator, """
        SELECT r.id::text || '|' || coalesce(r.direct_permissions::text, 'null') || '|' || r.limit_to_24_hours::text || '|'
               || r.sys_updated_at::text
        FROM tenant_member_reasons r ORDER BY r.id
        """);

    private static async Task<List<string>> ReadAsync(string connectionString, string sql)
    {
        var rows = new List<string>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
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
