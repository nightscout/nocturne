using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// <see cref="Scope.IsDataOnly"/>: what a team may be given. Every permission atom is decided one way
/// or the other here, so a new atom fails until someone decides whether a team may hold it.
/// </summary>
[Trait("Category", "Unit")]
public class ScopeDataOnlyTests
{
    private static readonly string[] DataPermissions =
    [
        Scope.GlucoseRead, Scope.GlucoseReadWrite,
        Scope.TreatmentsRead, Scope.TreatmentsReadWrite,
        Scope.DevicesRead, Scope.DevicesReadWrite,
        Scope.HeartRateRead, Scope.HeartRateReadWrite,
        Scope.StepCountRead, Scope.StepCountReadWrite,
        Scope.SleepRead, Scope.SleepReadWrite,
        Scope.FoodRead, Scope.FoodReadWrite,
        Scope.ConnectorsRead, Scope.ConnectorsReadWrite,
        Scope.ReportsRead,
        Scope.TherapyRead, Scope.TherapyReadWrite,
        Scope.AlertsRead, Scope.AlertsReadWrite, Scope.AlertsAcknowledge,
        Scope.IdentityRead,
        Scope.DeviceNotify, Scope.DeviceActuate,
    ];

    [Fact]
    public void Every_permission_atom_is_either_data_or_administrative_and_not_both()
    {
        DataPermissions.Should().OnlyHaveUniqueItems();
        DataPermissions.Intersect(Scope.AdministrativePermissions).Should().BeEmpty();
        DataPermissions.Concat(Scope.AdministrativePermissions.Where(p => p != Scope.FullAccess))
            .Should().BeEquivalentTo(Scope.PermissionAtoms);
    }

    public static TheoryData<string> Administrative => new(Scope.AdministrativePermissions);

    [Theory]
    [MemberData(nameof(Administrative))]
    public void A_set_holding_an_administrative_permission_is_not_data_only(string permission) =>
        Scope.IsDataOnly([Scope.GlucoseRead, permission]).Should().BeFalse();

    [Fact]
    public void A_set_of_data_permissions_is_data_only_and_so_is_an_empty_one()
    {
        Scope.IsDataOnly(DataPermissions).Should().BeTrue();
        Scope.IsDataOnly([]).Should().BeTrue();
    }

    [Theory]
    [InlineData(RoleSeeds.Caretaker, true)]
    [InlineData(RoleSeeds.Clinician, true)]
    [InlineData(RoleSeeds.Viewer, true)]
    [InlineData(RoleSeeds.Admin, false)]
    [InlineData(RoleSeeds.Owner, false)]
    public void The_seed_roles_a_team_may_be_offered_are_those_for_data(string slug, bool dataOnly) =>
        Scope.IsDataOnly(RoleSeeds.Permissions[slug]).Should().Be(dataOnly);
}
