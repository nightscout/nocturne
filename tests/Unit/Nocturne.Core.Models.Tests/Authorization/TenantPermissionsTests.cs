using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

[Trait("Category", "Unit")]
public class TenantPermissionsTests
{
    [Fact]
    public void Satisfies_AuditManage_ImpliesAuditRead()
    {
        TenantPermissions.Satisfies(TenantPermissions.AuditManage, TenantPermissions.AuditRead)
            .Should().BeTrue("audit.manage should imply audit.read");
    }

    [Fact]
    public void Satisfies_AuditRead_DoesNotImplyAuditManage()
    {
        TenantPermissions.Satisfies(TenantPermissions.AuditRead, TenantPermissions.AuditManage)
            .Should().BeFalse("audit.read should NOT imply audit.manage");
    }

    [Fact]
    public void Satisfies_Superuser_ImpliesAuditRead()
    {
        TenantPermissions.Satisfies(TenantPermissions.Superuser, TenantPermissions.AuditRead)
            .Should().BeTrue("superuser (*) should satisfy audit.read");
    }

    [Fact]
    public void Satisfies_Superuser_ImpliesAuditManage()
    {
        TenantPermissions.Satisfies(TenantPermissions.Superuser, TenantPermissions.AuditManage)
            .Should().BeTrue("superuser (*) should satisfy audit.manage");
    }

    [Fact]
    public void SeedRoles_Admin_HasAuditRead()
    {
        TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Admin]
            .Should().Contain(TenantPermissions.AuditRead,
                "the admin seed role should include audit.read");
    }

    [Fact]
    public void SeedRoles_Admin_DoesNotHaveAuditManage()
    {
        TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Admin]
            .Should().NotContain(TenantPermissions.AuditManage,
                "the admin seed role should not include audit.manage (owner-only)");
    }

    [Fact]
    public void SeedRoles_Owner_HasSuperuser()
    {
        TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Owner]
            .Should().Contain(TenantPermissions.Superuser,
                "the owner seed role should include superuser");
    }

    [Fact]
    public void HasPermission_WithAuditManageInSet_SatisfiesAuditRead()
    {
        var permissions = new HashSet<string> { TenantPermissions.AuditManage };

        TenantPermissions.HasPermission(permissions, TenantPermissions.AuditRead)
            .Should().BeTrue("a set containing audit.manage should satisfy audit.read");
    }

    [Fact]
    public void HasPermission_WithOnlyAuditReadInSet_DoesNotSatisfyAuditManage()
    {
        var permissions = new HashSet<string> { TenantPermissions.AuditRead };

        TenantPermissions.HasPermission(permissions, TenantPermissions.AuditManage)
            .Should().BeFalse("a set containing only audit.read should not satisfy audit.manage");
    }

    [Fact]
    public void All_ContainsAuditPermissions()
    {
        TenantPermissions.All.Should().Contain(TenantPermissions.AuditRead);
        TenantPermissions.All.Should().Contain(TenantPermissions.AuditManage);
    }

    [Fact]
    public void All_ContainsDeviceCapabilityPermissions()
    {
        TenantPermissions.All.Should().Contain(TenantPermissions.DeviceNotify);
        TenantPermissions.All.Should().Contain(TenantPermissions.DeviceActuate);
    }

    [Theory]
    [InlineData(TenantPermissions.SeedRoles.Admin)]
    [InlineData(TenantPermissions.SeedRoles.Caretaker)]
    [InlineData(TenantPermissions.SeedRoles.Clinician)]
    [InlineData(TenantPermissions.SeedRoles.Viewer)]
    public void SeedRoles_HumanMemberRoles_HaveDeviceCapabilityGrants(string roleSlug)
    {
        // Device scopes drive the member's own client devices (Companion/Prelude), not the
        // patient record, so every authenticated human role gets them.
        TenantPermissions.SeedRolePermissions[roleSlug]
            .Should().Contain([TenantPermissions.DeviceNotify, TenantPermissions.DeviceActuate]);
    }

    [Fact]
    public void SeedRoles_Denied_HasNoDeviceCapabilityGrants()
    {
        TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Denied]
            .Should().BeEmpty();
    }

    [Fact]
    public void ValidateGrant_AllowsNothingBeingGranted()
    {
        TenantPermissions.ValidateGrant(null, []).Should().BeNull();
        TenantPermissions.ValidateGrant([], []).Should().BeNull();
    }

    [Fact]
    public void ValidateGrant_RefusesSuperuser_ToAGranterHoldingEveryOtherPermission()
    {
        // Holding every named atom is not the same as holding "*": * additionally satisfies
        // atoms added in later versions, so it is only ever grantable by another superuser.
        TenantPermissions.ValidateGrant([TenantPermissions.Superuser], TenantPermissions.All)!.Description.Should().Contain("Cannot grant '*'");
    }

    [Fact]
    public void ValidateGrant_AllowsSuperuser_FromASuperuser()
    {
        TenantPermissions.ValidateGrant([TenantPermissions.Superuser], [TenantPermissions.Superuser])
            .Should().BeNull();
    }

    [Fact]
    public void ValidateGrant_RefusesAnUnknownPermission_EvenFromASuperuser()
    {
        TenantPermissions.ValidateGrant(["glucose.destroy"], [TenantPermissions.Superuser])!.Description.Should().Contain("not a known permission");
    }

    [Theory]
    [InlineData("GLUCOSE.READ")]
    [InlineData("glucose.read ")]
    [InlineData("")]
    public void ValidateGrant_RefusesAPermissionThatIsNotAnExactAtom(string requested)
    {
        // Comparison is exact and ordinal, so a case or whitespace variant is unknown rather
        // than silently equivalent to the atom it resembles.
        TenantPermissions.ValidateGrant([requested], [TenantPermissions.Superuser])!.Description.Should().Contain("not a known permission");
    }

    [Fact]
    public void ValidateGrant_AllowsTheReadTierOfAPermissionHeldAsReadWrite()
    {
        TenantPermissions.ValidateGrant(
            [TenantPermissions.GlucoseRead], [TenantPermissions.GlucoseReadWrite])
            .Should().BeNull();
    }

    [Fact]
    public void ValidateGrant_RefusesTheWriteTierOfAPermissionHeldAsReadOnly()
    {
        TenantPermissions.ValidateGrant(
            [TenantPermissions.GlucoseReadWrite], [TenantPermissions.GlucoseRead])!.Description.Should().Contain("Cannot grant 'glucose.readwrite'");
    }

    [Fact]
    public void ValidateGrant_RefusesTheWholeSetWhenAnyMemberExceedsTheGranter()
    {
        TenantPermissions.ValidateGrant(
            [TenantPermissions.GlucoseRead, TenantPermissions.AuditManage],
            [TenantPermissions.GlucoseRead, TenantPermissions.AuditRead])!.Description.Should().Contain("Cannot grant 'audit.manage'");
    }
}
