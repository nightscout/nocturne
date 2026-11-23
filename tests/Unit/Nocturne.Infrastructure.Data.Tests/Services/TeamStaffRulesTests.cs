using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Services;

/// <summary>
/// When an edit of a staff member gives them more than they held: the one rule that sends the
/// approvals a site decides for itself back to pending.
/// </summary>
public class TeamStaffRulesTests
{
    private static readonly DateTime Base = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime? End(int? days) => days is { } d ? Base.AddDays(d) : null;

    private static TeamStaffNarrowing Narrowing(string? ceiling, bool limited, int? endDays) => new(ceiling, limited, End(endDays));

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, RoleSeeds.Viewer, false)]
    [InlineData(null, RoleSeeds.Clinician, false)]
    [InlineData(null, RoleSeeds.Caretaker, false)]
    [InlineData(RoleSeeds.Viewer, null, true)]
    [InlineData(RoleSeeds.Clinician, null, true)]
    [InlineData(RoleSeeds.Caretaker, null, true)]
    [InlineData(RoleSeeds.Clinician, RoleSeeds.Clinician, false)]
    [InlineData(RoleSeeds.Viewer, RoleSeeds.Viewer, false)]
    [InlineData(RoleSeeds.Caretaker, RoleSeeds.Caretaker, false)]
    [InlineData(RoleSeeds.Viewer, RoleSeeds.Clinician, true)]
    [InlineData(RoleSeeds.Viewer, RoleSeeds.Caretaker, true)]
    [InlineData(RoleSeeds.Clinician, RoleSeeds.Caretaker, true)]
    [InlineData(RoleSeeds.Clinician, RoleSeeds.Viewer, false)]
    [InlineData(RoleSeeds.Caretaker, RoleSeeds.Clinician, false)]
    [InlineData(RoleSeeds.Caretaker, RoleSeeds.Viewer, false)]
    [InlineData("a-role-no-seed-has", RoleSeeds.Viewer, true)]
    [InlineData(RoleSeeds.Viewer, "a-role-no-seed-has", true)]
    [InlineData("a-role-no-seed-has", "a-role-no-seed-has", false)]
    public void A_ceiling_is_wider_unless_it_is_provably_within_the_one_before(string? before, string? after, bool wider) =>
        TeamStaffRules.CeilingWidens(before, after).Should().Be(wider);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Lifting_the_24_hour_limit_is_wider_and_nothing_else_is(bool before, bool after, bool wider) =>
        TeamStaffRules.Widens(Narrowing(null, before, null), Narrowing(null, after, null)).Should().Be(wider);

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, 10, false)]
    [InlineData(10, null, true)]
    [InlineData(10, 11, true)]
    [InlineData(10, 10, false)]
    [InlineData(10, 9, false)]
    public void A_later_or_removed_end_is_wider_and_nothing_else_is(int? before, int? after, bool wider) =>
        TeamStaffRules.Widens(Narrowing(null, false, before), Narrowing(null, false, after)).Should().Be(wider);

    [Theory]
    [InlineData(RoleSeeds.Viewer, RoleSeeds.Caretaker, true, true, 10, 5, true)]
    [InlineData(RoleSeeds.Caretaker, RoleSeeds.Viewer, true, false, 10, 5, true)]
    [InlineData(RoleSeeds.Caretaker, RoleSeeds.Viewer, true, true, 5, 10, true)]
    [InlineData(RoleSeeds.Caretaker, RoleSeeds.Viewer, true, true, 10, 5, false)]
    public void An_edit_that_widens_one_thing_is_wider_though_it_narrows_the_others(
        string ceilingBefore, string ceilingAfter, bool limitedBefore, bool limitedAfter, int endBefore, int endAfter, bool wider) =>
        TeamStaffRules.Widens(Narrowing(ceilingBefore, limitedBefore, endBefore), Narrowing(ceilingAfter, limitedAfter, endAfter))
            .Should().Be(wider);

    [Fact]
    public void An_edit_that_narrows_every_one_of_the_three_is_not_wider() =>
        TeamStaffRules.Widens(Narrowing(null, false, null), Narrowing(RoleSeeds.Viewer, true, 5)).Should().BeFalse();

    [Fact]
    public void An_edit_that_changes_nothing_is_not_wider() =>
        TeamStaffRules.Widens(Narrowing(RoleSeeds.Clinician, true, 5), Narrowing(RoleSeeds.Clinician, true, 5)).Should().BeFalse();
}
