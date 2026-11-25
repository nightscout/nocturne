using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// An owner of a site holds no team reason there: each write that makes or unmakes an owner brings the
/// subject's team reasons into line in its commit, and the writes that let a person in directly take the
/// subject's row before any other lock, against account deletion.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    public enum Owning
    {
        RoleChange,
        Invite,
        Request,
    }

    private TenantService Tenants(Site site, Guid actor) => new(
        Contexts(site.Tenant, actor),
        new TenantRepository(Contexts(site.Tenant, actor)),
        new TenantMembershipRepository(Contexts(site.Tenant, actor), AccountLinkRoutes.Off),
        new SubjectRepository(Contexts(site.Tenant, actor), AccountLinkRoutes.Off),
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        new GrantRevocationService(NewSessions()),
        new FakeTimeProvider(new DateTimeOffset(Now)),
        Mock.Of<ILogger<TenantService>>(),
        AccountLinkRoutes.Off, InertTeamStaff.Announcer());

    private MemberInviteService Invites(Site site, Guid actor) => new(
        new TenantMembershipRepository(Contexts(site.Tenant, actor), AccountLinkRoutes.Off),
        new MemberInviteRepository(Contexts(site.Tenant, actor)),
        new SubjectRepository(Contexts(site.Tenant, actor), AccountLinkRoutes.Off),
        Contexts(site.Tenant, actor),
        Mock.Of<IJwtService>(),
        Mock.Of<ITenantRoleService>(),
        Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example" }),
        new FakeTimeProvider(new DateTimeOffset(Now)),
        NullLogger<MemberInviteService>.Instance,
        AccountLinkRoutes.Off,
        new GrantRevocationService(NewSessions()), InertTeamStaff.Announcer());

    private MembershipRequestService Requests(Site site, Guid actor) => new(
        new TenantRepository(Contexts(site.Tenant, actor)),
        new TenantMembershipRepository(Contexts(site.Tenant, actor), AccountLinkRoutes.Off),
        new SubjectRepository(Contexts(site.Tenant, actor), AccountLinkRoutes.Off),
        Contexts(site.Tenant, actor),
        Mock.Of<ITenantRoleService>(r => r.ValidateRoleGrantAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())
            == Task.FromResult(RoleGrantValidation.Valid)),
        Mock.Of<IInAppNotificationService>(),
        Mock.Of<IAuthAuditService>(),
        new FakeTimeProvider(new DateTimeOffset(Now)),
        NullLogger<MembershipRequestService>.Instance,
        AccountLinkRoutes.Off,
        new GrantRevocationService(NewSessions()), InertTeamStaff.Announcer());

    private async Task<Guid> OwnerRoleAsync(Site site) =>
        (await Members(site.Tenant).FindRoleIdBySlugAsync(site.Tenant, RoleSeeds.Owner, Ct))!.Value;

    private async Task<Guid> MemberIdAsync(Site site, Guid subject) =>
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, subject, Ct))!.Id;

    /// <summary>A second owner of the site, let in by the site's owner, so the first stays when they step down.</summary>
    private async Task<Guid> SecondOwnerAsync(Site site)
    {
        var owner = await NewSubjectAsync("second-owner");
        (await Tenants(site, site.Owner).AddMemberAsync(site.Tenant, owner, [await OwnerRoleAsync(site)], ct: Ct)).Should().BeTrue();
        return owner;
    }

    /// <summary>Ends <paramref name="subject"/>'s direct reason, and their row with it, a day ago, as the sweep finds it.</summary>
    private async Task LapseDirectReasonAsync(Site site, Guid subject)
    {
        var lapsed = Now.AddDays(-1);
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            SELECT set_config('app.current_tenant_id', '{site.Tenant}', false);
            UPDATE tenant_member_reasons SET access_ends_at = '{lapsed:O}'
             WHERE kind = 'direct' AND tenant_member_id = (SELECT id FROM tenant_members WHERE tenant_id = '{site.Tenant}' AND subject_id = '{subject}');
            UPDATE tenant_members SET access_ends_at = '{lapsed:O}' WHERE tenant_id = '{site.Tenant}' AND subject_id = '{subject}';
            """);
    }

    private async Task<string> InviteAsync(Site site, Guid role)
    {
        var token = Guid.NewGuid().ToString("N");
        await new MemberInviteRepository(Contexts(site.Tenant)).CreateAsync(
            new NewMemberInvite(site.Tenant, site.Owner, HashUtils.Sha256Hex(token), [role], null, "Family", false, Now.AddDays(7), null, Now, null),
            Ct);
        return token;
    }

    private Task<Guid> RequestAsync(Site site, Guid subject) =>
        Members(site.Tenant).CreateRequestAsync(new NewMembershipRequest(site.Tenant, subject, null, Now), Ct);

    private async Task<OwnerGuardedChange> ReplaceRolesAsync(Site site, Guid actor, Guid subject, Guid removed, Guid added) =>
        await Tenants(site, actor).ReplaceMemberRolesAsync(site.Tenant, await MemberIdAsync(site, subject), [removed], [added], Ct);

    /// <summary>The subject's membership holds only its direct reason and is the sum of it.</summary>
    private async Task HoldsOnlyTheDirectReasonAsync(Site site, Guid subject)
    {
        (await KindsAsync(site, subject)).Should().Equal($"{MembershipReasonKind.Direct}:");
        await RowIsTheSumAsync(site, subject);
        (await OutOfLineAsync(subject, site)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_owner_who_is_staff_holds_no_team_reason_after_a_join_an_approval_a_role_edit_a_staff_edit_or_the_startup_check()
    {
        var site = await SiteAsync("owner-staff");
        var night = await NightRoleAsync(site);
        var following = await TeamAsync("Ward");
        await FollowAsync(following, site, Gives(site, [night]));
        var team = await TeamAsync("Clinic");
        await JoinStaffAsync(team, site.Owner);

        await FollowAsync(team, site, Gives(site, [night]));
        await HoldsOnlyTheDirectReasonAsync(site, site.Owner);

        await JoinStaffAsync(following, site.Owner);
        await HoldsOnlyTheDirectReasonAsync(site, site.Owner);

        (await EditNightAsync(site, night)).Outcome.Should().Be(RoleChangeOutcome.Changed);
        await HoldsOnlyTheDirectReasonAsync(site, site.Owner);

        (await Service(Guid.Empty, team.Manager).UpdateStaffAsync(
                team.Id, team.Manager, site.Owner, new TeamStaffNarrowing(null, true, Now.AddDays(5)), Ct))
            .Should().Be(TeamStaffChange.Changed);
        await HoldsOnlyTheDirectReasonAsync(site, site.Owner);

        await ReconcileAsync();
        await HoldsOnlyTheDirectReasonAsync(site, site.Owner);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, site.Owner, Ct))!.Roles.Select(r => r.RoleId)
            .Should().Equal(await OwnerRoleAsync(site));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Losing_the_owner_role_gives_the_team_reason_the_rows_give(bool stepDown)
    {
        var (site, team, access, _) = await SiteAndTeamAsync($"owner-loses-{stepDown}");
        var owner = await SecondOwnerAsync(site);
        var staffEnds = Now.AddDays(5);
        (await JoinAsync(team, owner, new TeamStaffNarrowing(null, true, staffEnds))).Refusal.Should().BeNull();
        await HoldsOnlyTheDirectReasonAsync(site, owner);
        var member = await MemberIdAsync(site, owner);
        var caretaker = site.CaretakerRole;

        (await ReplaceRolesAsync(site, stepDown ? owner : site.Owner, owner, await OwnerRoleAsync(site), caretaker))
            .Should().Be(OwnerGuardedChange.Changed);

        (await ReasonAsync(site, owner, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, true, staffEnds));
        await HoldsTeamReasonAsync(site, owner, access);
        var held = (await Members(site.Tenant).FindMemberAsync(site.Tenant, owner, Ct))!;
        held.Id.Should().Be(member);
        held.AccessEndsAt.Should().BeNull("the direct reason has no end");
        held.Roles.Select(r => r.RoleId).Should().BeEquivalentTo([caretaker, site.ViewerRole]);
        (await OutOfLineAsync(owner, site)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(Owning.RoleChange)]
    [InlineData(Owning.Invite)]
    [InlineData(Owning.Request)]
    public async Task Becoming_an_owner_removes_the_team_reason_and_keeps_the_membership_and_what_they_made(Owning path)
    {
        var (site, team, access, staff) = await SiteAndTeamAsync($"owner-gains-{path}");
        var ownerRole = await OwnerRoleAsync(site);
        if (path == Owning.RoleChange)
            await DirectMemberAsync(site, staff, endsAt: null);
        (await JoinAsync(team, staff, new TeamStaffNarrowing(null, false, Now.AddDays(5)))).Refusal.Should().BeNull();
        await HoldsTeamReasonAsync(site, staff, access);
        var member = await MemberIdAsync(site, staff);
        var made = await MadeByAsync(site, staff);

        switch (path)
        {
            case Owning.RoleChange:
                (await ReplaceRolesAsync(site, site.Owner, staff, site.ViewerRole, ownerRole)).Should().Be(OwnerGuardedChange.Changed);
                break;
            case Owning.Invite:
                (await Invites(site, staff).AcceptInviteAsync(await InviteAsync(site, ownerRole), staff, site.Tenant)).Success.Should().BeTrue();
                break;
            case Owning.Request:
                (await Requests(site, site.Owner).ApproveRequestAsync(
                        await RequestAsync(site, staff), site.Tenant, [ownerRole], site.Owner, [Scope.FullAccess], accessEndsAt: null, ct: Ct))
                    .Success.Should().BeTrue();
                break;
        }

        await HoldsOnlyTheDirectReasonAsync(site, staff);
        var held = (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct))!;
        held.Id.Should().Be(member);
        held.AccessEndsAt.Should().BeNull();
        held.Roles.Select(r => r.RoleId).Should().Equal(ownerRole);
        (await StateAsync(site, made)).Should().Be(Untouched);
        var ended = (await RowsAsync(site.Tenant, staff)).Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberRemoved).Subject;
        ended.Read(MemberAccessAudit.HowKey).Should().Be("team");
        ended.Team.Should().Be(team.Id);
    }

    [Fact]
    public async Task An_owner_removed_entirely_keeps_access_through_the_team_and_the_audit_records_both()
    {
        var (site, team, access, _) = await SiteAndTeamAsync("owner-removed");
        var owner = await SecondOwnerAsync(site);
        await JoinStaffAsync(team, owner);
        await HoldsOnlyTheDirectReasonAsync(site, owner);

        (await Tenants(site, site.Owner).RemoveMemberAsync(site.Tenant, owner, Ct)).Ok.Should().BeTrue();

        (await KindsAsync(site, owner)).Should().Equal($"{MembershipReasonKind.Team}:{access}");
        await HoldsTeamReasonAsync(site, owner, access);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, owner, Ct))!.Roles.Select(r => r.RoleId).Should().Equal(site.ViewerRole);
        var rows = await RowsAsync(site.Tenant, owner);
        var removed = rows.FindLastIndex(r => r.EventType == AuthAuditEventType.MemberRemoved);
        removed.Should().BeGreaterThanOrEqualTo(0);
        rows[removed].Read(MemberAccessAudit.HowKey).Should().NotBe("team");
        var joined = rows.Skip(removed + 1).Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberJoined).Subject;
        joined.Read(MemberAccessAudit.HowKey).Should().Be("team");
        joined.Team.Should().Be(team.Id);
    }

    [Fact]
    public async Task An_owner_reason_that_has_lapsed_does_not_keep_the_team_reason_out()
    {
        var (site, team, access, _) = await SiteAndTeamAsync("owner-lapsed");
        var owner = await SecondOwnerAsync(site);
        await LapseDirectReasonAsync(site, owner);

        await JoinStaffAsync(team, owner);

        (await ReasonAsync(site, owner, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, null));
        await HoldsTeamReasonAsync(site, owner, access);
        (await OutOfLineAsync(owner, site)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_startup_check_removes_a_team_reason_an_owner_was_given_before()
    {
        var (site, team, access, _) = await SiteAndTeamAsync("owner-backfill");
        await JoinStaffAsync(team, site.Owner);
        await HoldsOnlyTheDirectReasonAsync(site, site.Owner);
        var member = await MemberIdAsync(site, site.Owner);
        var reason = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            SELECT set_config('app.current_tenant_id', '{site.Tenant}', false);
            INSERT INTO tenant_member_reasons (id, tenant_member_id, kind, source_id, limit_to_24_hours, access_ends_at)
            VALUES ('{reason}', '{member}', 'team', '{access}', false, NULL);
            INSERT INTO tenant_member_reason_roles (id, tenant_member_reason_id, tenant_role_id)
            VALUES ('{Guid.CreateVersion7()}', '{reason}', '{site.ViewerRole}');
            """);
        (await OutOfLineAsync(site.Owner, site)).Should().Equal(site.Tenant);

        await ReconcileAsync();

        await HoldsOnlyTheDirectReasonAsync(site, site.Owner);
        (await MemberIdAsync(site, site.Owner)).Should().Be(member);
        var ended = (await RowsAsync(site.Tenant, site.Owner)).Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberRemoved).Subject;
        ended.Read(MemberAccessAudit.HowKey).Should().Be("team");
        ended.Team.Should().Be(team.Id);
    }

    [Fact]
    public async Task An_owner_whose_row_has_no_reason_yet_is_read_as_an_owner_from_the_row()
    {
        var (site, team, _, _) = await SiteAndTeamAsync("owner-row-only");
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            SELECT set_config('app.current_tenant_id', '{site.Tenant}', false);
            DELETE FROM tenant_member_reasons
             WHERE tenant_member_id = (SELECT id FROM tenant_members WHERE tenant_id = '{site.Tenant}' AND subject_id = '{site.Owner}');
            """);

        await JoinStaffAsync(team, site.Owner);

        (await KindsAsync(site, site.Owner)).Should().NotContain(k => k.StartsWith($"{MembershipReasonKind.Team}:"));
        (await OutOfLineAsync(site.Owner, site)).Should().BeEmpty();
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, site.Owner, Ct))!.Roles.Select(r => r.RoleId)
            .Should().Equal(await OwnerRoleAsync(site));
    }

    /// <remarks>
    /// The subject's direct reason has ended, so the accept replaces it. The accept first holds the
    /// subject's row and waits at their membership; the deletion first holds the row <c>FOR UPDATE</c> and
    /// waits at the site's owners lock, and the accept then finds no account.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_invite_accept_and_the_accepters_account_deletion_at_once_both_finish(bool acceptFirst)
    {
        var site = await SiteAsync($"race-invite-delete-{acceptFirst}");
        var subject = await NewSubjectAsync("rejoining");
        await DirectMemberAsync(site, subject, Now.AddDays(-1));
        var token = await InviteAsync(site, await OwnerRoleAsync(site));
        AcceptMemberInviteResult? accepted = null;

        Func<Task> accept = async () => accepted = await Invites(site, subject).AcceptInviteAsync(token, subject, site.Tenant);
        Func<Task> delete = () => Deletion(subject).DeleteAsync(subject, false, Ct);
        if (acceptFirst)
            await RaceAsync(site, subject, accept, delete);
        else
            await RaceAsync(site, holder => holder.LockTenantOwnersAsync(site.Tenant, Ct), delete, accept);

        accepted!.Success.Should().Be(acceptFirst);
        await IsGoneAsync(subject);
    }

    /// <remarks>The orders of <see cref="An_invite_accept_and_the_accepters_account_deletion_at_once_both_finish"/>.</remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_request_approval_and_the_requesters_account_deletion_at_once_both_finish(bool approveFirst)
    {
        var site = await SiteAsync($"race-request-delete-{approveFirst}");
        var subject = await NewSubjectAsync("rejoining");
        await DirectMemberAsync(site, subject, Now.AddDays(-1));
        var request = await RequestAsync(site, subject);
        var ownerRole = await OwnerRoleAsync(site);
        DecideMembershipRequestResult? approved = null;

        Func<Task> approve = async () => approved = await Requests(site, site.Owner).ApproveRequestAsync(
            request, site.Tenant, [ownerRole], site.Owner, [Scope.FullAccess], accessEndsAt: null, ct: Ct);
        Func<Task> delete = () => Deletion(subject).DeleteAsync(subject, false, Ct);
        if (approveFirst)
            await RaceAsync(site, subject, approve, delete);
        else
            await RaceAsync(site, holder => holder.LockTenantOwnersAsync(site.Tenant, Ct), delete, approve);

        approved!.Success.Should().Be(approveFirst);
        await IsGoneAsync(subject);
    }

    /// <remarks>
    /// Both wait at the site's team access lock, in the order given. A join ahead of the step-down finds an
    /// owner and gives nothing; the step-down then gives the reason.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_step_down_and_a_staff_join_at_once_both_finish_and_the_former_owner_holds_the_team_reason(bool stepDownFirst)
    {
        var (site, team, access, _) = await SiteAndTeamAsync($"race-step-down-join-{stepDownFirst}");
        var owner = await SecondOwnerAsync(site);
        var ownerRole = await OwnerRoleAsync(site);
        var token = await StaffInviteAsync(team, new TeamStaffNarrowing(null, false, null));
        OwnerGuardedChange? stepped = null;

        Func<Task> stepDown = async () => stepped = await ReplaceRolesAsync(site, owner, owner, ownerRole, site.CaretakerRole);
        Func<Task> join = () => Service(Guid.Empty, owner).JoinStaffAsync(token, owner, Ct);
        await RaceAsync(
            site, holder => holder.LockTeamAccessAsync(site.Tenant, Ct), stepDownFirst ? stepDown : join, stepDownFirst ? join : stepDown);

        stepped.Should().Be(OwnerGuardedChange.Changed);
        await HoldsTeamReasonAsync(site, owner, access);
        (await OutOfLineAsync(owner, site)).Should().BeEmpty();
    }

    /// <remarks>
    /// Each takes the site's owners lock before its team access lock, which the holder holds: the first
    /// holds the owners lock and waits at the access lock, and the second waits at the owners lock. An
    /// approval ahead of the role change gives the staff member the reason; the role change then removes it.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_role_change_to_owner_and_an_approval_at_once_both_finish_and_the_new_owner_holds_no_team_reason(bool changeFirst)
    {
        var site = await SiteAsync($"race-owner-approve-{changeFirst}");
        var team = await TeamAsync("Clinic");
        var staff = await JoinStaffAsync(team);
        var kept = await JoinStaffAsync(team);
        await DirectMemberAsync(site, staff, endsAt: null);
        var ownerRole = await OwnerRoleAsync(site);
        var token = await AccessLinkAsync(team);
        OwnerGuardedChange? changed = null;
        TeamAccessApproval? approval = null;

        Func<Task> change = async () => changed = await ReplaceRolesAsync(site, site.Owner, staff, site.ViewerRole, ownerRole);
        Func<Task> approve = async () => approval = await ApproveAsync(site, token, Gives(site, [site.ViewerRole]));
        await RaceAsync(
            site, holder => holder.LockTeamAccessAsync(site.Tenant, Ct), changeFirst ? change : approve, changeFirst ? approve : change);

        changed.Should().Be(OwnerGuardedChange.Changed);
        approval!.Refusal.Should().BeNull();
        await HoldsOnlyTheDirectReasonAsync(site, staff);
        await HoldsTeamReasonAsync(site, kept, approval.AccessId!.Value);
    }
}
