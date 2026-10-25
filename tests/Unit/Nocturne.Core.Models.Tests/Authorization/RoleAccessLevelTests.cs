using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

public class RoleAccessLevelTests
{
    [Theory]
    [InlineData(RoleSeeds.Viewer, RoleAccessLevel.ReadOnly)]
    [InlineData(RoleSeeds.Clinician, RoleAccessLevel.ReadOnly)]
    [InlineData(RoleSeeds.Caretaker, RoleAccessLevel.ReadAndLogTreatments)]
    [InlineData(RoleSeeds.Admin, RoleAccessLevel.Manage)]
    [InlineData(RoleSeeds.Owner, RoleAccessLevel.Manage)]
    public void SeededRoles_HaveTheLevelTheirPermissionsGive(string slug, RoleAccessLevel level) =>
        RoleAccessLevels.Of(RoleSeeds.Permissions[slug]).Should().Be(level);

    [Fact]
    public void ARoleThatGrantsNothing_HasNoLevel() =>
        RoleAccessLevels.Of(RoleSeeds.Permissions[RoleSeeds.Denied]).Should().BeNull();

    [Theory]
    [InlineData(Scope.GlucoseReadWrite, RoleAccessLevel.Manage)]
    [InlineData(Scope.TreatmentsReadWrite, RoleAccessLevel.ReadAndLogTreatments)]
    [InlineData(Scope.AlertsReadWrite, RoleAccessLevel.ReadAndLogTreatments)]
    [InlineData(Scope.TherapyReadWrite, RoleAccessLevel.Manage)]
    [InlineData(Scope.TenantSettings, RoleAccessLevel.Manage)]
    [InlineData(Scope.MembersInvite, RoleAccessLevel.Manage)]
    [InlineData(Scope.DeviceActuate, RoleAccessLevel.ReadOnly)]
    [InlineData("device.manage", RoleAccessLevel.Manage)]
    public void AnEditedClinician_IsJudgedByWhatItWasGiven(string added, RoleAccessLevel level) =>
        RoleAccessLevels.Of([.. RoleSeeds.Permissions[RoleSeeds.Clinician], added]).Should().Be(level);
}
