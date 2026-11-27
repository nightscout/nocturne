using FluentAssertions;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Logging;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The person facts of a People query that come from the viewer's own memberships: where each person comes from, by the
/// membership reasons in force (<see cref="TenantMembershipRepository.ListSourcesOfSubjectAsync"/>), and the groups the
/// viewer is staff of.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    private static PeopleQuery Only(params PeopleFilter[] filters) => new(null, filters, null);

    private static PeopleFilter Kind(PeopleFilterKind kind) => new(kind, null, null, null, null, null, null, null, null, null);

    [Fact]
    public async Task A_source_filter_keeps_the_people_followed_directly_or_through_the_named_team_and_never_one_who_blocked_the_viewer()
    {
        var ward = await TeamAsync("Ward");
        var clinic = await TeamAsync("Clinic");
        var staff = await JoinStaffAsync(ward);
        await JoinStaffAsync(clinic, staff);
        var direct = await SiteAsync("direct");
        await DirectMemberAsync(direct, staff, null);
        var viaWard = await SiteAsync("via-ward");
        await FollowAsync(ward, viaWard);
        var both = await SiteAsync("both");
        await DirectMemberAsync(both, staff, null);
        await FollowAsync(clinic, both);
        var blocked = await SiteAsync("blocked");
        await FollowAsync(ward, blocked);
        (await BlockAsync(blocked, blocked.Owner, staff)).Should().Be(OwnerDecision.Decided);
        var lapsed = await SiteAsync("lapsed");
        await DirectMemberAsync(lapsed, staff, null);
        await FollowAsync(ward, lapsed);
        await NpgsqlTestCommands.ExecuteAsTenantAsync(database.MigratorConnectionString, lapsed.Tenant, $"""
            UPDATE tenant_member_reasons SET access_ends_at = '{Now.AddDays(-1):O}'
             WHERE kind = 'direct' AND tenant_member_id = (SELECT id FROM tenant_members WHERE tenant_id = '{lapsed.Tenant}' AND subject_id = '{staff}');
            """);
        var overview = Overview(new ListLogger<TenantOverviewService>(), withReading: Guid.Empty);

        async Task<IEnumerable<Guid>> Read(PeopleQuery query) =>
            (await overview.GetOverviewAsync(staff, SessionScopes, AuthType.SessionCookie, OverviewRuleSet.Fixed, query, false, Ct))
            .Tenants.Select(t => t.TenantId);

        (await Read(PeopleQuery.Empty)).Should().BeEquivalentTo([direct.Tenant, viaWard.Tenant, both.Tenant, lapsed.Tenant]);
        (await Read(Only(Kind(PeopleFilterKind.SourceDirect)))).Should().BeEquivalentTo([direct.Tenant, both.Tenant]);
        (await Read(Only(Kind(PeopleFilterKind.SourceTeam) with { TeamId = ward.Id })))
            .Should().BeEquivalentTo([viaWard.Tenant, lapsed.Tenant], "a direct reason that has ended no longer counts");
        (await Read(Only(Kind(PeopleFilterKind.SourceTeam) with { TeamId = clinic.Id }))).Should().Equal(both.Tenant);
        (await Read(Only(Kind(PeopleFilterKind.SourceDirect), Kind(PeopleFilterKind.SourceTeam) with { TeamId = clinic.Id })))
            .Should().Equal(both.Tenant);
        (await Read(Only(Kind(PeopleFilterKind.SourceTeam) with { TeamId = Guid.NewGuid() }))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_membership_with_no_reasons_is_direct_and_a_declined_staff_member_or_one_who_left_is_followed_through_the_team_by_no_one()
    {
        var ward = await TeamAsync("Ward");
        var staff = await JoinStaffAsync(ward);
        var adopted = await SiteAsync("adopted");
        await DirectMemberAsync(adopted, staff, null);
        await NpgsqlTestCommands.ExecuteAsTenantAsync(database.MigratorConnectionString, adopted.Tenant, $"""
            DELETE FROM tenant_member_reasons
             WHERE tenant_member_id = (SELECT id FROM tenant_members WHERE tenant_id = '{adopted.Tenant}' AND subject_id = '{staff}');
            """);
        var declined = await SiteAsync("declined");
        await DirectMemberAsync(declined, staff, null);
        var declinedAccess = await FollowAsync(ward, declined);
        await HoldsTeamReasonAsync(declined, staff, declinedAccess);
        (await DeclineStaffAsync(declined, ward, declined.Owner, staff)).Should().Be(OwnerDecision.Decided);
        var clinic = await TeamAsync("Clinic");
        await JoinStaffAsync(clinic, staff);
        var left = await SiteAsync("left");
        await DirectMemberAsync(left, staff, null);
        var leftAccess = await FollowAsync(clinic, left);
        await HoldsTeamReasonAsync(left, staff, leftAccess);
        await EndAsync(Path.RemoveStaff, left, clinic, staff);
        var overview = Overview(new ListLogger<TenantOverviewService>(), withReading: Guid.Empty);

        async Task<IEnumerable<Guid>> Read(PeopleQuery query) =>
            (await overview.GetOverviewAsync(staff, SessionScopes, AuthType.SessionCookie, OverviewRuleSet.Fixed, query, false, Ct))
            .Tenants.Select(t => t.TenantId);

        (await Read(Only(Kind(PeopleFilterKind.SourceDirect)))).Should().BeEquivalentTo([adopted.Tenant, declined.Tenant, left.Tenant]);
        (await Read(Only(Kind(PeopleFilterKind.SourceTeam) with { TeamId = ward.Id }))).Should().BeEmpty("the owner declined the staff member");
        (await Read(Only(Kind(PeopleFilterKind.SourceTeam) with { TeamId = clinic.Id }))).Should().BeEmpty("the staff member left the team");
    }

    [Fact]
    public async Task A_staff_member_narrowed_to_their_groups_is_followed_through_the_team_only_for_the_people_of_those_groups()
    {
        var team = await TeamAsync("Camp");
        var staff = await JoinStaffAsync(team);
        var birch = await GroupAsync(team, "Birch");
        var inBirch = await SiteAsync("narrow-in");
        var outside = await SiteAsync("narrow-out");
        await DirectMemberAsync(outside, staff, null);
        await FollowAsync(team, inBirch);
        await FollowAsync(team, outside);
        await PutSiteAsync(team, birch, inBirch);
        await PutStaffAsync(team, birch, staff);
        await ReachAsync(team, TeamStaffReach.TheirGroups);
        var overview = Overview(new ListLogger<TenantOverviewService>(), withReading: Guid.Empty);

        async Task<IEnumerable<Guid>> Read(PeopleQuery query) =>
            (await overview.GetOverviewAsync(staff, SessionScopes, AuthType.SessionCookie, OverviewRuleSet.Fixed, query, false, Ct))
            .Tenants.Select(t => t.TenantId);

        (await Read(PeopleQuery.Empty)).Should().BeEquivalentTo([inBirch.Tenant, outside.Tenant]);
        (await Read(Only(Kind(PeopleFilterKind.SourceTeam) with { TeamId = team.Id }))).Should().Equal(inBirch.Tenant);
        (await Read(Only(Kind(PeopleFilterKind.SourceDirect)))).Should().Equal(outside.Tenant);
    }

    [Fact]
    public async Task An_in_group_filter_keeps_the_people_of_a_group_the_viewer_is_staff_of_and_no_one_for_any_other_group()
    {
        var team = await TeamAsync("Camp");
        var staff = await JoinStaffAsync(team);
        var birch = await GroupAsync(team, "Birch");
        var oak = await GroupAsync(team, "Oak");
        await PutStaffAsync(team, birch, staff);
        var inBirch = await SiteAsync("in-birch");
        var inOak = await SiteAsync("in-oak");
        var outside = await SiteAsync("outside");
        foreach (var site in new[] { inBirch, inOak, outside })
            await FollowAsync(team, site);
        await PutSiteAsync(team, birch, inBirch);
        await PutSiteAsync(team, oak, inOak);
        var overview = Overview(new ListLogger<TenantOverviewService>(), withReading: Guid.Empty);

        async Task<IEnumerable<Guid>> Read(Guid group) =>
            (await overview.GetOverviewAsync(
                staff, SessionScopes, AuthType.SessionCookie, OverviewRuleSet.Fixed,
                Only(Kind(PeopleFilterKind.InGroup) with { GroupId = group }), false, Ct))
            .Tenants.Select(t => t.TenantId);

        (await Read(birch)).Should().Equal(inBirch.Tenant);
        (await Read(oak)).Should().BeEmpty("the viewer is not staff of that group");
        (await Read(Guid.NewGuid())).Should().BeEmpty();
    }
}
