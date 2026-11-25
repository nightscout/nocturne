using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Tests.Repositories.V4;

namespace Nocturne.Infrastructure.Data.Tests.Services;

/// <summary>
/// What a team's access gives a staff member under their ceiling (<see cref="TeamReasons.GrantOf"/>), and
/// the startup check's cost on an instance with no team.
/// </summary>
public class TeamReasonsTests
{
    private static readonly Guid Night = Guid.CreateVersion7();
    private static readonly Guid Caretaker = Guid.CreateVersion7();
    private static readonly DateTime End = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly TeamReasons.AccessRole[] Roles =
    [
        new(Night, [Scope.GlucoseRead, Scope.TreatmentsRead]),
        new(Caretaker, RoleSeeds.Permissions[RoleSeeds.Caretaker]),
    ];

    [Fact]
    public void With_no_ceiling_every_role_is_given_whole()
    {
        var grant = TeamReasons.GrantOf(Roles, null, true, End)!;

        grant.RoleIds.Should().Equal(Night, Caretaker);
        grant.DirectPermissions.Should().BeNull();
        (grant.LimitTo24Hours, grant.AccessEndsAt).Should().Be((true, (DateTime?)End));
    }

    [Fact]
    public void A_role_within_the_ceiling_is_given_whole_and_of_a_wider_one_only_what_both_grant()
    {
        var grant = TeamReasons.GrantOf(Roles, RoleSeeds.Viewer, false, null)!;

        grant.RoleIds.Should().BeEmpty();
        grant.DirectPermissions.Should().Equal(Scope.DeviceActuate, Scope.DeviceNotify, Scope.GlucoseRead, Scope.ReportsRead);
    }

    [Fact]
    public void A_ceiling_that_holds_one_role_keeps_its_id_and_caps_the_other()
    {
        var grant = TeamReasons.GrantOf(Roles, RoleSeeds.Clinician, false, null)!;

        grant.RoleIds.Should().Equal(Night);
        grant.DirectPermissions.Should().BeEquivalentTo(RoleSeeds.Permissions[RoleSeeds.Clinician])
            .And.BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void A_ceiling_that_grants_nothing_of_the_roles_gives_nothing() =>
        TeamReasons.GrantOf([new TeamReasons.AccessRole(Night, [Scope.TreatmentsRead])], RoleSeeds.Viewer, false, null)
            .Should().BeNull();

    [Fact]
    public void A_ceiling_no_seed_role_has_gives_nothing() =>
        TeamReasons.GrantOf(Roles, "retired", false, null).Should().BeNull();

    [Fact]
    public void Two_grants_are_the_same_whatever_order_their_roles_are_in()
    {
        var first = new MembershipGrant([Night, Caretaker], [Scope.GlucoseRead], true, End);

        TeamReasons.Same(first, new MembershipGrant([Caretaker, Night], [Scope.GlucoseRead], true, End)).Should().BeTrue();
        TeamReasons.Same(first, new MembershipGrant([Caretaker, Night], null, true, End)).Should().BeFalse();
        TeamReasons.Same(first, new MembershipGrant([Caretaker, Night], [Scope.GlucoseRead], false, End)).Should().BeFalse();
        TeamReasons.Same(first, new MembershipGrant([Caretaker, Night], [Scope.GlucoseRead], true, null)).Should().BeFalse();
    }

    [Fact]
    public async Task The_startup_check_on_an_instance_with_no_team_runs_one_read_and_nothing_else()
    {
        var captured = new CapturedSql();

        var result = await TeamReasonChanges.SyncAllAsync(
            captured.CreateContext,
            (_, _, _) => throw new InvalidOperationException("nothing is out of line"),
            End,
            NullLogger.Instance,
            CancellationToken.None);

        result.Should().Be((0, 0));
        captured.Commands.Should().ContainSingle();
    }
}
