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
    [InlineData(TenantPermissions.Superuser)]
    [InlineData(TenantPermissions.MembersManage)]
    [InlineData(TenantPermissions.MembersInvite)]
    [InlineData(TenantPermissions.RolesManage)]
    [InlineData(TenantPermissions.SharingManage)]
    [InlineData(TenantPermissions.AuditManage)]
    [InlineData(TenantPermissions.AuditRead)]
    public void DemoVisitorPermissions_ExcludeEscalationAndDisclosureAtoms(string atom) =>
        TenantPermissions.DemoVisitorPermissions.Should().NotContain(atom);

    [Fact]
    public void DemoVisitorPermissions_StillCoverTheSurfacesTheDemoExistsToShow()
    {
        TenantPermissions.DemoVisitorPermissions.Should().Contain(
        [
            TenantPermissions.GlucoseReadWrite,
            TenantPermissions.TreatmentsReadWrite,
            TenantPermissions.TherapyReadWrite,
            TenantPermissions.AlertsReadWrite,
            TenantPermissions.ReportsRead,
            TenantPermissions.TenantSettings,
        ]);
    }

    [Fact]
    public void DemoVisitorPermissions_AreAllKnownAtoms() =>
        TenantPermissions.DemoVisitorPermissions
            .Where(p => !TenantPermissions.All.Contains(p))
            .Should().BeEmpty();

    [Fact]
    public void DemoVisitorPermissions_AreNarrowerThanTheAdminSeedRole()
    {
        // Documents the deliberate divergence: the demo used to reuse the admin role, which
        // carries the member-management atoms above.
        var admin = TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Admin];
        TenantPermissions.DemoVisitorPermissions.Should().BeSubsetOf(admin);
        TenantPermissions.DemoVisitorPermissions.Should().NotBeEquivalentTo(admin);
    }
}
