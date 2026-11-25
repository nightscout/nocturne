using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// A membership holding a team's reason beside its direct one, or only a team's, on PostgreSQL under the
/// runtime role. The team reasons are written straight to the tables, with the staff rows they stand on,
/// and projected by the startup check. The direct reason's writers change only it and refuse a
/// member who holds none; the team entry points name the team and never read as the member leaving.
/// </summary>
[Trait("Category", "Integration")]
public class MembershipTeamReasonPostgresTests(MembershipTeamReasonPostgresTests.Database database)
    : IClassFixture<MembershipTeamReasonPostgresTests.Database>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Now = new(2026, 11, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ends = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("membership_team_reason");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed record Site(
        Guid Tenant, SuppliedContextFactory Factory, TenantMembershipRepository Members, Guid Owner, Guid OwnerRole, Guid ViewerRole,
        Guid CaretakerRole);

    private sealed record Row(string EventType, string? Details, Guid? Team, Guid? Actor)
    {
        public string? Read(string key)
        {
            if (Details is null)
                return null;
            using var document = JsonDocument.Parse(Details);
            return document.RootElement.TryGetProperty(key, out var value) ? value.GetString() : null;
        }
    }

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    private async Task<Site> SiteAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', '{slug}-{tenant:N}', 'team-reason', true, now(), now());
            """);
        var factory = new SuppliedContextFactory(() => Context(tenant));
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        await members.SeedRolesAsync(tenant, Ct);
        var ownerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, Ct))!.Value;
        var viewerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Viewer, Ct))!.Value;
        var caretakerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, Ct))!.Value;
        var owner = await NewSubjectAsync(factory, $"{slug}-owner");
        (await members.AddMemberAsync(new NewTenantMember(tenant, owner, [ownerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        return new Site(tenant, factory, members, owner, ownerRole, viewerRole, caretakerRole);
    }

    private static Task<Guid> NewSubjectAsync(SuppliedContextFactory factory, string name) =>
        new SubjectRepository(factory, AccountLinkRoutes.Off).CreateAsync(new NewSubject(name), Ct);

    /// <summary>A team named <paramref name="name"/> with access to <paramref name="tenant"/>; returns the access.</summary>
    private async Task<(Guid Team, Guid Access)> TeamAccessAsync(Guid tenant, string name)
    {
        var team = Guid.CreateVersion7();
        var access = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, TeamReasonSql.Access(tenant, team, access, name));
        return (team, access);
    }

    /// <summary>
    /// Writes a reason from <paramref name="access"/> giving <paramref name="role"/> to the subject's membership,
    /// adding the row when they hold none, with the staff rows it stands on, and projects it as the startup
    /// check does.
    /// </summary>
    private async Task<Guid> TeamReasonAsync(
        Site site, Guid subject, Guid access, Guid role, bool limitTo24Hours = false, DateTime? endsAt = null)
    {
        var memberId = await WriteTeamReasonAsync(
            site, subject, access, role, limitTo24Hours, endsAt, TeamReasonSql.Staff(access, subject, role, limitTo24Hours, endsAt));
        await DatabaseInitializationExtensions.ReconcileMembershipReasonsAsync(
            database.MigratorConnectionString, Now, TeamWrites.Apply(Now), NullLogger.Instance, Ct);
        return memberId;
    }

    /// <summary>
    /// Writes the reason alone, after <paramref name="staffSql"/>, leaving the row as it was: no check runs
    /// to remove a reason that no staff row stands on.
    /// </summary>
    private async Task<Guid> WriteTeamReasonAsync(
        Site site, Guid subject, Guid access, Guid role, bool limitTo24Hours, DateTime? endsAt, string staffSql)
    {
        var member = (await site.Members.FindMemberAsync(site.Tenant, subject, Ct))?.Id;
        var memberId = member ?? Guid.CreateVersion7();
        var reason = Guid.CreateVersion7();
        var ends = endsAt is { } end ? $"'{end:O}'" : "NULL";
        var limit = limitTo24Hours ? "true" : "false";
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            SELECT set_config('app.current_tenant_id', '{site.Tenant}', false);
            {(member is null ? $"""
                INSERT INTO tenant_members (id, tenant_id, subject_id, limit_to_24_hours, access_ends_at, sys_created_at, sys_updated_at)
                VALUES ('{memberId}', '{site.Tenant}', '{subject}', {limit}, {ends}, now(), now());
                """ : "")}
            INSERT INTO tenant_member_reasons (id, tenant_member_id, kind, source_id, limit_to_24_hours, access_ends_at)
            VALUES ('{reason}', '{memberId}', 'team', '{access}', {limit}, {ends});
            INSERT INTO tenant_member_reason_roles (id, tenant_member_reason_id, tenant_role_id)
            VALUES ('{Guid.CreateVersion7()}', '{reason}', '{role}');
            {staffSql}
            """);
        return memberId;
    }

    /// <summary>A member who holds only the reason a new team's access gives: caretaker.</summary>
    private async Task<(Guid Subject, Guid Member, Guid Access)> TeamOnlyAsync(Site site, string name)
    {
        var subject = await NewSubjectAsync(site.Factory, name);
        var (_, access) = await TeamAccessAsync(site.Tenant, $"{name} team");
        var member = await TeamReasonAsync(site, subject, access, site.CaretakerRole);
        return (subject, member, access);
    }

    /// <summary>A viewer let in directly who also holds the reason a new team's access gives: caretaker.</summary>
    private async Task<(Guid Subject, Guid Member, Guid Access)> MixedAsync(
        Site site, string name, IReadOnlyList<string>? permissions = null, bool teamLimit = false)
    {
        var subject = await NewSubjectAsync(site.Factory, name);
        (await site.Members.AddMemberAsync(
                new NewTenantMember(site.Tenant, subject, [site.ViewerRole], permissions?.ToList()) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        var (_, access) = await TeamAccessAsync(site.Tenant, $"{name} team");
        var member = await TeamReasonAsync(site, subject, access, site.CaretakerRole, teamLimit);
        return (subject, member, access);
    }

    private async Task<List<Row>> RowsAsync(Site site, Guid subject)
    {
        await using var db = Context(Guid.Empty);
        return (await db.AuthAuditLog.AsNoTracking()
                .Where(a => a.TenantId == site.Tenant && a.TargetSubjectId == subject)
                .OrderBy(a => a.Id)
                .ToListAsync())
            .Select(a => new Row(a.EventType, a.DetailsJson, a.TeamId, a.ActorSubjectId))
            .ToList();
    }

    private async Task<List<(string Kind, Guid? Source)>> ReasonsAsync(Guid member) =>
        (await ReadAsync($"SELECT kind || '|' || coalesce(source_id::text, '') FROM tenant_member_reasons WHERE tenant_member_id = '{member}' ORDER BY kind"))
        .Select(r => r.Split('|'))
        .Select(p => (p[0], p[1].Length == 0 ? (Guid?)null : Guid.Parse(p[1])))
        .ToList();

    private async Task<string> RowStateAsync(Guid member) =>
        (await ReadAsync($"""
            SELECT coalesce(m.direct_permissions::text, 'null') || '|' || m.limit_to_24_hours::text || '|'
                   || coalesce(m.access_ends_at::text, 'null') || '|'
                   || coalesce((SELECT string_agg(mr.tenant_role_id::text, ',' ORDER BY mr.tenant_role_id)
                                FROM tenant_member_roles mr WHERE mr.tenant_member_id = m.id), '') || '|'
                   || (SELECT count(*) FROM tenant_member_reasons r WHERE r.tenant_member_id = m.id)::text || '|'
                   || (SELECT count(*) FROM tenant_member_reason_roles rr
                        JOIN tenant_member_reasons r ON r.id = rr.tenant_member_reason_id WHERE r.tenant_member_id = m.id)::text
            FROM tenant_members m WHERE m.id = '{member}'
            """)).SingleOrDefault() ?? "absent";

    private async Task<long> AuditCountAsync(Site site) =>
        long.Parse((await ReadAsync($"SELECT count(*)::text FROM auth_audit_log WHERE tenant_id = '{site.Tenant}'")).Single());

    private async Task<List<Guid>> RoleIdsAsync(Site site, Guid member) =>
        [.. (await site.Members.GetMemberAsync(site.Tenant, member, Ct))!.Roles.Select(r => r.RoleId)];

    private async Task RowIsTheSumAsync(Site site, Guid member)
    {
        await using var db = Context(site.Tenant);
        (await MembershipReasons.MatchesAsync(db, member, Now, Ct)).Should().BeTrue("the row is the sum of its reasons");
    }

    private async Task<T> InTransactionAsync<T>(Site site, Func<NocturneDbContext, Task<T>> work)
    {
        await using var db = Context(site.Tenant);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var result = await work(db);
        await db.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
        return result;
    }

    private async Task<T> InUnitAsync<T>(Site site, Func<DbUnitOfWork, Task<T>> work)
    {
        await using var unit = DbUnitOfWork.Own(Context(site.Tenant));
        return await unit.CommitAsync(async token =>
        {
            var result = await work(unit);
            await unit.Context.SaveChangesAsync(token);
            return result;
        }, ct: Ct);
    }

    [Fact]
    public async Task Letting_a_team_only_member_in_directly_adds_a_direct_reason_with_a_new_members_rows_and_the_sum_holds_both()
    {
        var site = await SiteAsync("add-direct");
        var (subject, member, access) = await TeamOnlyAsync(site, "add-direct-staff");
        var before = (await RowsAsync(site, subject)).Count;

        (await site.Members.AddDirectMemberAsync(
                new NewTenantMember(site.Tenant, subject, [site.ViewerRole], ["treatments.read"], LimitTo24Hours: true, AccessEndsAt: Ends)
                    { How = MembershipJoinHow.Invite, By = site.Owner },
                Now, Ct))
            .Should().BeTrue();

        var rows = (await RowsAsync(site, subject)).Skip(before).ToList();
        rows.Select(r => r.EventType).Should().Equal(
            AuthAuditEventType.MemberJoined, AuthAuditEventType.RoleAssigned, AuthAuditEventType.RoleAssigned,
            AuthAuditEventType.HistoryLimitChanged, AuthAuditEventType.AccessEndChanged);
        rows[0].Should().Match<Row>(r => r.Read("how") == "invite" && r.Read("by") == site.Owner.ToString() && r.Team == null);
        rows[1].Read("role").Should().Be(RoleSeeds.Viewer);
        rows[2].Read("permission").Should().Be("treatments.read");

        (await ReasonsAsync(member)).Should().BeEquivalentTo([(MembershipReasonKind.Direct, (Guid?)null), (MembershipReasonKind.Team, access)]);
        await RowIsTheSumAsync(site, member);
        (await RoleIdsAsync(site, member)).Should().BeEquivalentTo([site.ViewerRole, site.CaretakerRole]);
        var record = (await site.Members.GetMemberAsync(site.Tenant, member, Ct))!;
        record.LimitTo24Hours.Should().BeFalse("the team's reason gives full history");
        record.AccessEndsAt.Should().BeNull("the team's reason does not end");
        record.DirectReason.Should().Match<TenantMemberReasonRecord>(d => d.LimitTo24Hours && d.AccessEndsAt == Ends);
    }

    [Fact]
    public async Task Letting_a_team_only_member_in_directly_gives_the_row_the_label_and_username_a_new_member_gets()
    {
        var site = await SiteAsync("add-label");
        var (subject, member, _) = await TeamOnlyAsync(site, "add-label-staff");
        var username = $"sam-{Guid.NewGuid():N}";

        (await site.Members.AddDirectMemberAsync(
                new NewTenantMember(site.Tenant, subject, [site.ViewerRole], Label: "Family", Username: username)
                    { How = MembershipJoinHow.Invite, By = site.Owner },
                Now, Ct))
            .Should().BeTrue();

        var record = (await site.Members.GetMemberAsync(site.Tenant, member, Ct))!;
        record.Label.Should().Be("Family");
        record.Username.Should().Be(username);
        await RowIsTheSumAsync(site, member);
    }

    [Fact]
    public async Task Letting_in_directly_is_refused_while_a_direct_reason_is_held()
    {
        var site = await SiteAsync("add-refused");
        var (subject, member, _) = await MixedAsync(site, "add-refused-mixed");
        var state = await RowStateAsync(member);
        var audit = await AuditCountAsync(site);

        (await site.Members.AddDirectMemberAsync(
                new NewTenantMember(site.Tenant, subject, [site.CaretakerRole]) { How = MembershipJoinHow.Direct, By = null }, Now, Ct))
            .Should().BeFalse();
        (await site.Members.AddMemberAsync(
                new NewTenantMember(site.Tenant, subject, [site.CaretakerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeFalse();

        (await RowStateAsync(member)).Should().Be(state);
        (await AuditCountAsync(site)).Should().Be(audit);
        await RowIsTheSumAsync(site, member);
    }

    [Fact]
    public async Task Is_a_direct_member_asks_for_a_direct_reason_and_is_a_member_for_any_reason()
    {
        var site = await SiteAsync("is-direct");
        var (teamOnly, _, _) = await TeamOnlyAsync(site, "is-direct-staff");
        var (mixed, _, _) = await MixedAsync(site, "is-direct-mixed");

        (await site.Members.IsMemberAsync(site.Tenant, teamOnly, Now, Ct)).Should().BeTrue();
        (await site.Members.IsDirectMemberAsync(site.Tenant, teamOnly, Now, Ct)).Should().BeFalse();
        (await site.Members.IsDirectMemberAsync(site.Tenant, mixed, Now, Ct)).Should().BeTrue();
        (await site.Members.IsDirectMemberAsync(site.Tenant, site.Owner, Now, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Removing_the_direct_reason_of_a_mixed_member_keeps_the_row_for_the_team_and_writes_what_the_direct_reason_held()
    {
        var site = await SiteAsync("remove-direct");
        var (subject, member, access) = await MixedAsync(site, "remove-direct-mixed", ["treatments.read"]);
        var before = (await RowsAsync(site, subject)).Count;

        (await OwnerGuardedWrites.RemoveMemberUnlessLastOwnerAsync(site.Factory, AccountLinkRoutes.Off, site.Tenant, member, MembershipEndHow.Removed, Now, Ct))
            .Outcome.Should().Be(OwnerGuardedChange.Changed);

        var rows = (await RowsAsync(site, subject)).Skip(before).ToList();
        rows.Select(r => r.EventType).Should().Equal(
            AuthAuditEventType.RoleRemoved, AuthAuditEventType.RoleRemoved, AuthAuditEventType.MemberRemoved);
        rows[0].Read("role").Should().Be(RoleSeeds.Viewer);
        rows[1].Read("permission").Should().Be("treatments.read");
        rows[2].Should().Match<Row>(r => r.Read("how") == "removed" && r.Team == null);

        (await ReasonsAsync(member)).Should().Equal((MembershipReasonKind.Team, (Guid?)access));
        await RowIsTheSumAsync(site, member);
        (await RoleIdsAsync(site, member)).Should().Equal(site.CaretakerRole);
        (await site.Members.GetMemberAsync(site.Tenant, member, Ct))!.DirectPermissions.Should().BeNull();
    }

    [Fact]
    public async Task Each_writer_of_the_direct_reason_refuses_a_team_only_member_and_writes_nothing()
    {
        var site = await SiteAsync("team-only-refused");
        var (_, member, _) = await TeamOnlyAsync(site, "team-only-refused-staff");
        var state = await RowStateAsync(member);
        var audit = await AuditCountAsync(site);

        (await OwnerGuardedWrites.RemoveMemberUnlessLastOwnerAsync(site.Factory, AccountLinkRoutes.Off, site.Tenant, member, MembershipEndHow.Removed, Now, Ct))
            .Outcome.Should().Be(OwnerGuardedChange.FromTeam);
        (await OwnerGuardedWrites.SetAccessEndUnlessOwnerAsync(site.Factory, AccountLinkRoutes.Off, site.Tenant, member, Ends, Now, Ct)).Should().Be(OwnerGuardedChange.FromTeam);
        (await OwnerGuardedWrites.ReplaceRolesUnlessLastOwnerAsync(site.Factory, AccountLinkRoutes.Off, site.Tenant, member, [site.CaretakerRole], [site.ViewerRole], Now, Ct))
            .Should().Be(OwnerGuardedChange.FromTeam);
        (await site.Members.ReplaceRolesAsync(site.Tenant, member, [site.CaretakerRole], [site.ViewerRole], Now, Ct)).Should().BeFalse();
        (await site.Members.SetDirectPermissionsAsync(site.Tenant, member, ["glucose.read"], Now, Ct)).Should().BeFalse();
        (await site.Members.SetLimitTo24HoursAsync(site.Tenant, member, true, Now, Ct)).Should().BeFalse();

        (await RowStateAsync(member)).Should().Be(state);
        (await AuditCountAsync(site)).Should().Be(audit);
        await RowIsTheSumAsync(site, member);
    }

    [Fact]
    public async Task A_role_change_of_a_mixed_member_changes_only_the_direct_reason_and_leaves_a_role_the_team_gives()
    {
        var site = await SiteAsync("roles-mixed");
        var (_, member, _) = await MixedAsync(site, "roles-mixed-member");

        (await OwnerGuardedWrites.ReplaceRolesUnlessLastOwnerAsync(site.Factory, AccountLinkRoutes.Off, site.Tenant, member, [site.ViewerRole], [site.CaretakerRole], Now, Ct))
            .Should().Be(OwnerGuardedChange.Changed);
        await RowIsTheSumAsync(site, member);
        (await OwnerGuardedWrites.ReplaceRolesUnlessLastOwnerAsync(site.Factory, AccountLinkRoutes.Off, site.Tenant, member, [site.CaretakerRole], [site.ViewerRole], Now, Ct))
            .Should().Be(OwnerGuardedChange.Changed);
        await RowIsTheSumAsync(site, member);

        (await RoleIdsAsync(site, member)).Should().BeEquivalentTo([site.ViewerRole, site.CaretakerRole],
            "taking caretaker from the direct reason leaves the caretaker the team gives");
        var record = (await site.Members.GetMemberAsync(site.Tenant, member, Ct))!;
        record.DirectReason!.Roles.Select(r => r.RoleId).Should().Equal(site.ViewerRole);
        record.HeldReasons.Single(r => r.Kind == MembershipReasonKind.Team).Roles.Select(r => r.RoleId).Should().Equal(site.CaretakerRole);
    }

    [Fact]
    public async Task A_limit_on_the_direct_reason_is_lifted_by_a_team_reason_that_has_none()
    {
        var site = await SiteAsync("limit-mixed");
        var (_, member, _) = await MixedAsync(site, "limit-mixed-member");

        (await site.Members.SetLimitTo24HoursAsync(site.Tenant, member, true, Now, Ct)).Should().BeTrue();
        await RowIsTheSumAsync(site, member);

        var record = (await site.Members.GetMemberAsync(site.Tenant, member, Ct))!;
        record.DirectReason!.LimitTo24Hours.Should().BeTrue();
        record.LimitTo24Hours.Should().BeFalse();
    }

    [Fact]
    public async Task The_end_date_of_a_mixed_member_moves_only_the_direct_reason()
    {
        var site = await SiteAsync("end-mixed");
        var (_, member, _) = await MixedAsync(site, "end-mixed-member");

        (await OwnerGuardedWrites.SetAccessEndUnlessOwnerAsync(site.Factory, AccountLinkRoutes.Off, site.Tenant, member, Ends, Now, Ct)).Should().Be(OwnerGuardedChange.Changed);
        await RowIsTheSumAsync(site, member);

        var record = (await site.Members.GetMemberAsync(site.Tenant, member, Ct))!;
        record.DirectReason!.AccessEndsAt.Should().Be(Ends);
        record.AccessEndsAt.Should().BeNull();
    }

    [Fact]
    public async Task The_member_list_reads_each_reason_with_its_teams_name_and_never_a_team_of_another_site()
    {
        var site = await SiteAsync("list");
        var other = await SiteAsync("list-other");
        var (_, mixed, _) = await MixedAsync(site, "list-mixed");
        var (_, foreignAccess) = await TeamAccessAsync(other.Tenant, "Elsewhere");
        var foreignSubject = await NewSubjectAsync(site.Factory, "list-foreign");
        (await site.Members.AddMemberAsync(new NewTenantMember(site.Tenant, foreignSubject, [site.ViewerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        var foreign = await WriteTeamReasonAsync(site, foreignSubject, foreignAccess, site.CaretakerRole, false, null, string.Empty);

        var listed = await site.Members.ListMembersAsync(site.Tenant, Ct);

        var mixedRecord = listed.Single(m => m.Id == mixed);
        mixedRecord.HeldReasons.Select(r => r.Kind).Should().Equal(MembershipReasonKind.Direct, MembershipReasonKind.Team);
        mixedRecord.HeldReasons[1].Team!.Name.Should().Be("list-mixed team");
        listed.Single(m => m.Id == foreign).HeldReasons.Single(r => r.Kind == MembershipReasonKind.Team).Team.Should().BeNull();
        listed.Single(m => m.SubjectId == site.Owner).HeldReasons.Should().ContainSingle()
            .Which.Kind.Should().Be(MembershipReasonKind.Direct);
    }

    [Fact]
    public async Task An_access_cannot_be_deleted_while_a_reason_names_it_and_deleting_the_site_removes_everything()
    {
        var site = await SiteAsync("fk");
        var (_, member, access) = await MixedAsync(site, "fk-mixed");

        var deleteAccess = () => NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            SELECT set_config('app.current_tenant_id', '{site.Tenant}', false);
            DELETE FROM team_tenant_access WHERE id = '{access}';
            """);
        (await deleteAccess.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);

        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"DELETE FROM tenants WHERE id = '{site.Tenant}';");

        (await RowStateAsync(member)).Should().Be("absent");
        (await ReadAsync($"SELECT count(*)::text FROM tenant_member_reasons WHERE tenant_member_id = '{member}'")).Should().Equal("0");
        (await ReadAsync($"SELECT count(*)::text FROM team_tenant_access WHERE id = '{access}'")).Should().Equal("0");
    }

    [Fact]
    public async Task The_writer_refuses_a_team_reason_that_gives_the_owner_role()
    {
        var site = await SiteAsync("owner-refused");
        var subject = await NewSubjectAsync(site.Factory, "owner-refused-staff");
        var (_, access) = await TeamAccessAsync(site.Tenant, "Owners");

        var add = () => InTransactionAsync(site, db => TenantMembershipRepository.StageAddTeamReasonAsync(
            db, site.Tenant, subject, access, new MembershipGrant([site.OwnerRole], null, false, null),
            new MemberAccessSource(subject), Now, Ct));

        await add.Should().ThrowAsync<InvalidOperationException>();
        (await site.Members.FindMemberAsync(site.Tenant, subject, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task The_writer_refuses_to_set_a_team_reason_to_the_owner_role()
    {
        var site = await SiteAsync("owner-set-refused");
        var subject = await NewSubjectAsync(site.Factory, "owner-set-refused-staff");
        var (_, access) = await TeamAccessAsync(site.Tenant, "Owners");
        await InTransactionAsync(site, db => TenantMembershipRepository.StageAddTeamReasonAsync(
            db, site.Tenant, subject, access, new MembershipGrant([site.CaretakerRole], null, false, null),
            MemberAccessSource.System, Now, Ct));

        var set = () => InUnitAsync(site, unit => TenantMembershipRepository.StageSetTeamReasonAsync(
            unit, site.Tenant, subject, access, new MembershipGrant([site.OwnerRole], null, false, null),
            MemberAccessSource.System, Now, Ct));

        await set.Should().ThrowAsync<InvalidOperationException>();
        (await site.Members.FindMemberAsync(site.Tenant, subject, Ct))!.Roles.Select(r => r.RoleId).Should().Equal(site.CaretakerRole);
    }

    [Fact]
    public async Task A_team_reason_names_the_team_and_a_staff_member_ending_it_themselves_is_not_recorded_as_leaving()
    {
        var site = await SiteAsync("team-entry");
        var subject = await NewSubjectAsync(site.Factory, "team-entry-staff");
        var (team, access) = await TeamAccessAsync(site.Tenant, "Clinic");
        var bySelf = new MemberAccessSource(subject);

        await InTransactionAsync(site, db => TenantMembershipRepository.StageAddTeamReasonAsync(
            db, site.Tenant, subject, access, new MembershipGrant([site.CaretakerRole], null, false, null), bySelf, Now, Ct));
        var member = (await site.Members.FindMemberAsync(site.Tenant, subject, Ct))!;
        await RowIsTheSumAsync(site, member.Id);
        member.DirectReason.Should().BeNull();
        member.Roles.Select(r => r.RoleId).Should().Equal(site.CaretakerRole);

        (await InUnitAsync(site, unit => TenantMembershipRepository.StageRemoveTeamReasonAsync(
            unit, site.Tenant, subject, access, bySelf, Now, Ct))).Should().BeEmpty();

        var rows = await RowsAsync(site, subject);
        rows.Select(r => r.EventType).Should().Equal(
            AuthAuditEventType.MemberJoined, AuthAuditEventType.RoleAssigned,
            AuthAuditEventType.RoleRemoved, AuthAuditEventType.MemberRemoved);
        rows[0].Should().Match<Row>(r => r.Read("how") == "team" && r.Read("name") == "Clinic" && r.Team == team && r.Actor == subject);
        rows[^1].Should().Match<Row>(r => r.Read("how") == "team" && r.Read("name") == "Clinic" && r.Team == team && r.Actor == subject);
        (await site.Members.FindMemberAsync(site.Tenant, subject, Ct)).Should().BeNull("the team's was the only reason");
    }

    [Fact]
    public async Task Ending_a_team_reason_beside_a_direct_one_keeps_the_membership()
    {
        var site = await SiteAsync("team-end-mixed");
        var (subject, member, access) = await MixedAsync(site, "team-end-mixed-member");

        (await InUnitAsync(site, unit => TenantMembershipRepository.StageRemoveTeamReasonAsync(
            unit, site.Tenant, subject, access, MemberAccessSource.System, Now, Ct))).Should().BeEmpty();

        (await ReasonsAsync(member)).Should().Equal((MembershipReasonKind.Direct, (Guid?)null));
        await RowIsTheSumAsync(site, member);
        (await RoleIdsAsync(site, member)).Should().Equal(site.ViewerRole);
    }

    [Fact]
    public async Task The_demo_reset_and_account_deletion_remove_every_reason_with_the_row()
    {
        var site = await SiteAsync("remove-all");
        var (reset, resetMember, _) = await MixedAsync(site, "remove-all-reset");
        var (deleted, deletedMember, _) = await TeamOnlyAsync(site, "remove-all-deleted");

        (await site.Members.RemoveMembershipsOfSubjectAsync(reset, Ct)).Removed.Should().Be(1);
        await new SubjectRepository(site.Factory, AccountLinkRoutes.Off).DeleteAsync(deleted, Ct);

        (await RowStateAsync(resetMember)).Should().Be("absent");
        (await RowStateAsync(deletedMember)).Should().Be("absent");
        (await ReadAsync($"SELECT count(*)::text FROM tenant_member_reasons WHERE tenant_member_id IN ('{resetMember}', '{deletedMember}')"))
            .Should().Equal("0");
    }

    private async Task<List<string>> ReadAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }
}
