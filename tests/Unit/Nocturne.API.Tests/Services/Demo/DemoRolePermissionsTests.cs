using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Services.Demo;

/// <summary>
/// Anyone can obtain a session for the demo member, so what its role carries is the whole
/// blast radius of the demo.
/// </summary>
public class DemoRolePermissionsTests
{
    /// <summary>
    /// Role and direct permissions are unioned into a member's effective set, so the ability
    /// to edit either is the ability to grant oneself <c>*</c>. The demo role must not hold
    /// any of these, or an anonymous visitor can escalate to tenant superuser.
    /// </summary>
    [Theory]
    [InlineData(Scope.FullAccess)]
    [InlineData(Scope.MembersManage)]
    [InlineData(Scope.MembersInvite)]
    [InlineData(Scope.RolesManage)]
    [InlineData(Scope.SharingManage)]
    [InlineData(Scope.AuditManage)]
    [InlineData(Scope.AuditRead)]
    public void DemoVisitorPermissions_ExcludeEscalationAndDisclosureAtoms(string atom) =>
        Scope.DemoVisitorPermissions.Should().NotContain(atom);

    [Fact]
    public void DemoVisitorPermissions_StillCoverTheSurfacesTheDemoExistsToShow()
    {
        Scope.DemoVisitorPermissions.Should().Contain(
        [
            Scope.GlucoseReadWrite,
            Scope.TreatmentsReadWrite,
            Scope.TherapyReadWrite,
            Scope.AlertsReadWrite,
            Scope.ReportsRead,
            Scope.TenantSettings,
        ]);
    }

    [Fact]
    public void DemoVisitorPermissions_AreAllKnownAtoms() =>
        Scope.DemoVisitorPermissions
            .Where(p => !Scope.PermissionAtoms.Contains(p))
            .Should().BeEmpty();

    [Fact]
    public void DemoVisitorPermissions_AreNarrowerThanTheAdminSeedRole()
    {
        // Documents the deliberate divergence: the demo used to reuse the admin role, which
        // carries the member-management atoms above.
        var admin = RoleSeeds.Permissions[RoleSeeds.Admin];
        Scope.DemoVisitorPermissions.Should().BeSubsetOf(admin);
        Scope.DemoVisitorPermissions.Should().NotBeEquivalentTo(admin);
    }
}
