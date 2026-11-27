using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The People page's reads for a staff member: the groups they are in (<see cref="TeamRepository.GetGroupsOfStaffAsync"/>)
/// and one group's overview (<see cref="TenantOverviewService.GetGroupOverviewAsync"/>), on contexts pinned to them, over
/// sites the team reaches with every state an owner's decisions leave a staff member in.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    private static readonly IReadOnlySet<string> SessionScopes = new HashSet<string> { Scope.FullAccess };

    [Fact]
    public async Task A_staff_members_groups_are_listed_by_name_with_the_team_where_two_share_a_name_and_go_once_their_end_passes()
    {
        var north = await TeamAsync("Camp North");
        var south = await TeamAsync("Camp South");
        var staff = await JoinStaffAsync(north);
        await JoinStaffAsync(south, staff);
        var birch = await GroupAsync(north, "birch");
        var aspen = await GroupAsync(north, "Aspen");
        var ward = await GroupAsync(north, "Ward");
        var southWard = await GroupAsync(south, "ward");
        await GroupAsync(south, "Other");
        foreach (var group in new[] { birch, aspen, ward })
            await PutStaffAsync(north, group, staff);
        await PutStaffAsync(south, southWard, staff);

        (await Teams().GetGroupsOfStaffAsync(staff, Now, Ct)).Should().Equal(
            new[]
            {
                new StaffGroup(aspen, "Aspen", "Camp North", false),
                new StaffGroup(birch, "birch", "Camp North", false),
            }.Concat(new[]
            {
                new StaffGroup(ward, "Ward", "Camp North", true),
                new StaffGroup(southWard, "ward", "Camp South", true),
            }.OrderBy(g => g.Id)));
        (await Teams().GetGroupsOfStaffAsync(north.Manager, Now, Ct)).Should().BeEmpty("a manager is in no group as staff");

        var ending = await NewSubjectAsync("ending");
        (await JoinAsync(north, ending, new TeamStaffNarrowing(null, false, Now.AddHours(1)))).Refusal.Should().BeNull();
        await PutStaffAsync(north, birch, ending);
        (await Teams().GetGroupsOfStaffAsync(ending, Now, Ct)).Select(g => g.Id).Should().Equal(birch);
        (await Teams().GetGroupsOfStaffAsync(ending, Now.AddHours(2), Ct)).Should().BeEmpty();
        (await Teams().GetPeopleOfStaffGroupAsync(birch, ending, Now.AddHours(2), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_groups_overview_lists_the_people_the_staff_member_reads_and_counts_the_ones_they_cannot_by_number_only()
    {
        var team = await TeamAsync("Camp");
        var staff = await JoinStaffAsync(team);
        var group = await GroupAsync(team, "Birch cabin");
        await PutStaffAsync(team, group, staff);

        var flagged = await SiteAsync("b-flagged");
        var calm = await SiteAsync("a-calm");
        var outside = await SiteAsync("outside");
        var blocked = await SiteAsync("blocked");
        var stopped = await SiteAsync("stopped");
        var readElsewhere = await SiteAsync("read-elsewhere");
        var followedElsewhere = await SiteAsync("followed-elsewhere");
        foreach (var site in new[] { flagged, calm, outside, blocked, stopped, readElsewhere, followedElsewhere })
            await FollowAsync(team, site);
        var clinic = await TeamAsync("Clinic");
        await JoinStaffAsync(clinic, staff);
        await FollowAsync(clinic, readElsewhere);
        await FollowAsync(await TeamAsync("Ward team"), followedElsewhere);
        var pending = await SiteAsync("pending");
        await FollowAsync(team, pending, Gives(pending, [pending.ViewerRole], staffRule: TeamStaffRule.OwnerApproves));
        var declined = await SiteAsync("declined");
        await FollowAsync(team, declined, Gives(declined, [declined.ViewerRole], staffRule: TeamStaffRule.OwnerApproves));
        (await DeclineStaffAsync(declined, team, declined.Owner, staff)).Should().Be(OwnerDecision.Decided);
        (await BlockAsync(blocked, blocked.Owner, staff)).Should().Be(OwnerDecision.Decided);
        var narrowed = await SiteAsync("narrowed");
        var denied = (await Members(narrowed.Tenant).FindRoleIdBySlugAsync(narrowed.Tenant, RoleSeeds.Denied, Ct))!.Value;
        await FollowAsync(team, narrowed, Gives(narrowed, [denied]));
        foreach (var site in new[] { flagged, calm, pending, declined, blocked, narrowed, stopped, readElsewhere, followedElsewhere })
            await PutSiteAsync(team, group, site);
        foreach (var site in new[] { stopped, readElsewhere, followedElsewhere })
            (await Service(site.Tenant, site.Owner).StopAsync(site.Tenant, team.Id, Ct)).Should().BeTrue();

        var hidden = new[] { pending, declined, blocked, narrowed };
        foreach (var site in hidden)
            await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString,
                $"UPDATE tenants SET display_name = 'Hidden {site.Tenant:N}' WHERE id = '{site.Tenant}'");
        var errors = new TenantOverviewTestKit.ErrorLog();
        var overview = Overview(errors, withReading: calm.Tenant);

        (await Teams().GetPeopleOfStaffGroupAsync(group, staff, Now, Ct)).Should().BeEquivalentTo(
            new[] { flagged, calm, pending, declined, blocked, narrowed }.Select(s => s.Tenant), "a stopped site keeps its place unlisted");

        var listed = await overview.GetGroupOverviewAsync(staff, group, SessionScopes, AuthType.SessionCookie, OverviewFlagRules.Judge, false, Ct);
        listed!.Tenants.Select(t => t.TenantId).Should().Equal(flagged.Tenant, calm.Tenant);
        listed.Tenants[0].Attention.Should().NotBeEmpty();
        listed.Tenants[1].Attention.Should().BeEmpty();
        listed.HiddenCount.Should().Be(
            hidden.Length, "the pending, declined, stopped and narrowed people; not a site that stopped this team, whoever else follows it");

        var json = JsonSerializer.Serialize(listed);
        foreach (var site in hidden.Concat([stopped, outside, readElsewhere, followedElsewhere]))
        {
            json.Should().NotContain(site.Tenant.ToString());
            json.Should().NotContain(site.Tenant.ToString("N"));
        }

        var everyone = await overview.GetOverviewAsync(staff, SessionScopes, AuthType.SessionCookie, OverviewFlagRules.Judge, false, Ct);
        everyone.Tenants.Select(t => t.TenantId).Should().BeEquivalentTo(
            [flagged.Tenant, calm.Tenant, outside.Tenant, readElsewhere.Tenant],
            "the staff member reads the site that stopped the team through the clinic");
        everyone.HiddenCount.Should().BeNull();

        (await overview.GetGroupOverviewAsync(team.Manager, group, SessionScopes, AuthType.SessionCookie, OverviewFlagRules.Judge, false, Ct))
            .Should().BeNull("a manager is not in the group");
        (await overview.GetGroupOverviewAsync(staff, await GroupAsync(team, "Oak cabin"), SessionScopes, AuthType.SessionCookie, OverviewFlagRules.Judge, false, Ct))
            .Should().BeNull("the staff member is not in that group");
        errors.Messages.Should().BeEmpty("every person was read, none stood in for by a failed read");
    }

    [Fact]
    public async Task Each_person_on_the_everyone_overview_is_under_the_first_of_the_staff_members_own_groups_that_holds_them()
    {
        var camp = await TeamAsync("Camp");
        var staff = await JoinStaffAsync(camp);
        var aspen = await GroupAsync(camp, "Aspen");
        var birch = await GroupAsync(camp, "birch");
        await PutStaffAsync(camp, birch, staff);
        var ward = await TeamAsync("Ward team");
        await JoinStaffAsync(ward, staff);
        var oak = await GroupAsync(ward, "Oak");
        var elm = await GroupAsync(ward, "elm");
        await PutStaffAsync(ward, oak, staff);
        await PutStaffAsync(ward, elm, staff);

        var inBoth = await SiteAsync("in-both");
        var aspenOnly = await SiteAsync("aspen-only");
        var inNone = await SiteAsync("in-none");
        var wardOnly = await SiteAsync("ward-only");
        var everywhere = await SiteAsync("everywhere");
        foreach (var site in new[] { inBoth, aspenOnly, inNone, everywhere })
            await FollowAsync(camp, site);
        foreach (var site in new[] { wardOnly, everywhere })
            await FollowAsync(ward, site);
        foreach (var (team, group, site) in new[]
        {
            (camp, aspen, inBoth), (camp, birch, inBoth), (camp, aspen, aspenOnly),
            (ward, oak, wardOnly), (ward, elm, wardOnly),
            (camp, aspen, everywhere), (camp, birch, everywhere), (ward, oak, everywhere), (ward, elm, everywhere),
        })
            await PutSiteAsync(team, group, site);

        string.CompareOrdinal(oak.ToString(), elm.ToString()).Should().BeNegative("an order by id would put Oak first");
        (await Teams().GetGroupsOfStaffAsync(staff, Now, Ct)).Select(g => g.Id).Should().Equal(birch, elm, oak);

        var overview = Overview(new TenantOverviewTestKit.ErrorLog(), withReading: Guid.Empty);
        var everyone = await overview.GetOverviewAsync(staff, SessionScopes, AuthType.SessionCookie, OverviewFlagRules.Judge, false, Ct);
        everyone.Tenants.ToDictionary(t => t.TenantId, t => t.FirstGroupId).Should().BeEquivalentTo(new Dictionary<Guid, Guid?>
        {
            [inBoth.Tenant] = birch,
            [aspenOnly.Tenant] = null,
            [inNone.Tenant] = null,
            [wardOnly.Tenant] = elm,
            [everywhere.Tenant] = birch,
        });
        var json = JsonSerializer.Serialize(everyone);
        json.Should().NotContain(aspen.ToString(), "the staff member is not in Aspen");
        json.Should().NotContain(oak.ToString(), "nobody is under Oak, so nothing says who is in it");

        var birchOverview = await overview.GetGroupOverviewAsync(staff, birch, SessionScopes, AuthType.SessionCookie, OverviewFlagRules.Judge, false, Ct);
        birchOverview!.Tenants.Select(t => t.TenantId).Should().BeEquivalentTo([inBoth.Tenant, everywhere.Tenant]);
        birchOverview.Tenants.Should().OnlyContain(t => t.FirstGroupId == null, "only the overview of everyone puts people under headings");
    }

    [Fact]
    public async Task Two_staff_members_with_the_same_access_read_the_same_groups_overview()
    {
        var team = await TeamAsync("Camp");
        var first = await JoinStaffAsync(team);
        var second = await JoinStaffAsync(team);
        var group = await GroupAsync(team, "Birch cabin");
        await PutStaffAsync(team, group, first);
        await PutStaffAsync(team, group, second);

        var amy = await SiteAsync("amy");
        var bo = await SiteAsync("bo");
        await FollowAsync(team, amy);
        await FollowAsync(team, bo);
        var pending = await SiteAsync("pending");
        await FollowAsync(team, pending, Gives(pending, [pending.ViewerRole], staffRule: TeamStaffRule.OwnerApproves));
        foreach (var site in new[] { amy, bo, pending })
            await PutSiteAsync(team, group, site);

        var overview = Overview(new TenantOverviewTestKit.ErrorLog(), withReading: Guid.Empty);
        var one = await overview.GetGroupOverviewAsync(first, group, SessionScopes, AuthType.SessionCookie, OverviewFlagRules.Judge, false, Ct);
        var two = await overview.GetGroupOverviewAsync(second, group, SessionScopes, AuthType.SessionCookie, OverviewFlagRules.Judge, false, Ct);

        one!.Tenants.Select(t => t.TenantId).Should().Equal(amy.Tenant, bo.Tenant);
        one.HiddenCount.Should().Be(1);
        JsonSerializer.Serialize(two).Should().Be(JsonSerializer.Serialize(one));
    }

    [Fact]
    public async Task A_place_whose_group_is_another_teams_opens_no_group()
    {
        var north = await TeamAsync("North");
        var staff = await JoinStaffAsync(north);
        var south = new Team((await Teams(staff).CreateAsync(staff, "South", Ct)).Id, "South", staff);
        var site = await SiteAsync("crossed");
        await FollowAsync(north, site);
        await FollowAsync(south, site);
        var southGroup = await GroupAsync(south, "Ward");
        await PutSiteAsync(south, southGroup, site);

        await using (var db = await Contexts(Guid.Empty).CreatePlatformAsync(Ct))
        {
            var member = await db.TeamMembers.SingleAsync(m => m.TeamId == north.Id && m.SubjectId == staff, Ct);
            db.TeamGroupStaff.Add(new TeamGroupStaffEntity
            {
                Id = Guid.CreateVersion7(), TeamGroupId = southGroup, TeamId = north.Id, TeamMemberId = member.Id,
            });
            await db.SaveChangesAsync(Ct);
        }

        (await Teams().GetGroupsOfStaffAsync(staff, Now, Ct)).Should().BeEmpty("they manage South, and are staff only of North");
        (await Teams().GetPeopleOfStaffGroupAsync(southGroup, staff, Now, Ct))
            .Should().BeNull("a place counts only in a group of the staff member's own team");
    }

    [Fact]
    public async Task A_place_that_names_another_team_opens_no_group_and_gives_no_reason_at_its_sites()
    {
        var (site, north, access, staff) = await StaffWithReasonAsync("place-team");
        var south = await Teams(staff).CreateAsync(staff, "South", Ct);
        await ReachAsync(north, TeamStaffReach.TheirGroups);
        await HoldsNoTeamReasonAsync(site, staff, access);
        var group = await GroupAsync(north, "Ward");

        await using (var db = await Contexts(Guid.Empty).CreatePlatformAsync(Ct))
        {
            var member = await db.TeamMembers.SingleAsync(m => m.TeamId == north.Id && m.SubjectId == staff, Ct);
            db.TeamGroupStaff.Add(new TeamGroupStaffEntity
            {
                Id = Guid.CreateVersion7(), TeamGroupId = group, TeamId = south.Id, TeamMemberId = member.Id,
            });
            await db.SaveChangesAsync(Ct);
        }

        await PutSiteAsync(north, group, site);

        (await Teams().GetGroupsOfStaffAsync(staff, Now, Ct)).Should().BeEmpty("the place names South, which they manage");
        (await Teams().GetPeopleOfStaffGroupAsync(group, staff, Now, Ct)).Should().BeNull();
        await HoldsNoTeamReasonAsync(site, staff, access);
    }

    /// <summary>The overview on this database, reading as nobody but the subject it is asked for; only <paramref name="withReading"/> has a reading.</summary>
    private TenantOverviewService Overview(TenantOverviewTestKit.ErrorLog errors, Guid withReading)
    {
        var current = Guid.Empty;
        var accessor = new Mock<ITenantAccessor>();
        accessor.Setup(a => a.SetTenant(It.IsAny<TenantContext>())).Callback<TenantContext>(tc => current = tc.TenantId);
        var canonical = new Mock<ICanonicalGlucoseService>();
        canonical.Setup(s => s.GetLatestAsync(It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(current == withReading
                ? new SensorGlucose { Mgdl = 120, Timestamp = DateTime.UtcNow.AddMinutes(-2) }
                : null));
        canonical.Setup(s => s.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var services = new ServiceCollection();
        services.AddScoped(_ => accessor.Object);
        services.AddScoped<ICategoryReadContext>(_ => new CategoryReadContext());
        services.AddScoped<INocturneDbContextFactory>(_ => Contexts(current));
        services.AddScoped<IAlertRepository>(sp => new AlertRepository(sp.GetRequiredService<INocturneDbContextFactory>()));
        services.AddScoped(_ => canonical.Object);

        return new TenantOverviewService(
            Members(Guid.Empty),
            Teams(),
            services.BuildServiceProvider().CreateScope().ServiceProvider,
            new GlucoseStatusClassifier(new ConfigurationBuilder().Build(), NullLogger<GlucoseStatusClassifier>.Instance),
            Mock.Of<IPersonalFlagRules>(),
            new FakeTimeProvider(new DateTimeOffset(Now)),
            errors);
    }
}
