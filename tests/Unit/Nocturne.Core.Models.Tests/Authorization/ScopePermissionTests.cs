using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

[Trait("Category", "Unit")]
public class ScopePermissionTests
{
    [Fact]
    public void Satisfies_AuditManage_ImpliesAuditRead()
    {
        Scope.Satisfies(Scope.AuditManage, Scope.AuditRead)
            .Should().BeTrue("audit.manage should imply audit.read");
    }

    [Fact]
    public void Satisfies_AuditRead_DoesNotImplyAuditManage()
    {
        Scope.Satisfies(Scope.AuditRead, Scope.AuditManage)
            .Should().BeFalse("audit.read should NOT imply audit.manage");
    }

    [Fact]
    public void Satisfies_Superuser_ImpliesAuditRead()
    {
        Scope.Satisfies(Scope.FullAccess, Scope.AuditRead)
            .Should().BeTrue("superuser (*) should satisfy audit.read");
    }

    [Fact]
    public void Satisfies_Superuser_ImpliesAuditManage()
    {
        Scope.Satisfies(Scope.FullAccess, Scope.AuditManage)
            .Should().BeTrue("superuser (*) should satisfy audit.manage");
    }

    [Fact]
    public void SeedRoles_Admin_HasAuditRead()
    {
        RoleSeeds.Permissions[RoleSeeds.Admin]
            .Should().Contain(Scope.AuditRead,
                "the admin seed role should include audit.read");
    }

    [Fact]
    public void SeedRoles_Admin_DoesNotHaveAuditManage()
    {
        RoleSeeds.Permissions[RoleSeeds.Admin]
            .Should().NotContain(Scope.AuditManage,
                "the admin seed role should not include audit.manage (owner-only)");
    }

    [Fact]
    public void SeedRoles_Owner_HasSuperuser()
    {
        RoleSeeds.Permissions[RoleSeeds.Owner]
            .Should().Contain(Scope.FullAccess,
                "the owner seed role should include superuser");
    }

    [Fact]
    public void HasPermission_WithAuditManageInSet_SatisfiesAuditRead()
    {
        var permissions = new HashSet<string> { Scope.AuditManage };

        Scope.Satisfies(permissions, Scope.AuditRead)
            .Should().BeTrue("a set containing audit.manage should satisfy audit.read");
    }

    [Fact]
    public void HasPermission_WithOnlyAuditReadInSet_DoesNotSatisfyAuditManage()
    {
        var permissions = new HashSet<string> { Scope.AuditRead };

        Scope.Satisfies(permissions, Scope.AuditManage)
            .Should().BeFalse("a set containing only audit.read should not satisfy audit.manage");
    }

    [Fact]
    public void All_ContainsAuditPermissions()
    {
        Scope.PermissionAtoms.Should().Contain(Scope.AuditRead);
        Scope.PermissionAtoms.Should().Contain(Scope.AuditManage);
    }

    [Fact]
    public void All_ContainsDeviceCapabilityPermissions()
    {
        Scope.PermissionAtoms.Should().Contain(Scope.DeviceNotify);
        Scope.PermissionAtoms.Should().Contain(Scope.DeviceActuate);
    }

    [Theory]
    [InlineData(RoleSeeds.Admin)]
    [InlineData(RoleSeeds.Caretaker)]
    [InlineData(RoleSeeds.Clinician)]
    [InlineData(RoleSeeds.Viewer)]
    public void SeedRoles_HumanMemberRoles_HaveDeviceCapabilityGrants(string roleSlug)
    {
        // Device scopes drive the member's own client devices (Companion/Prelude), not the
        // patient record, so every authenticated human role gets them.
        RoleSeeds.Permissions[roleSlug]
            .Should().Contain([Scope.DeviceNotify, Scope.DeviceActuate]);
    }

    [Fact]
    public void SeedRoles_Denied_HasNoDeviceCapabilityGrants()
    {
        RoleSeeds.Permissions[RoleSeeds.Denied]
            .Should().BeEmpty();
    }

    [Fact]
    public void ValidateGrant_AllowsNothingBeingGranted()
    {
        Scope.ValidateGrant(null, []).Should().BeNull();
        Scope.ValidateGrant([], []).Should().BeNull();
    }

    [Fact]
    public void ValidateGrant_RefusesSuperuser_ToAGranterHoldingEveryOtherPermission()
    {
        // Holding every named atom is not the same as holding "*": * additionally satisfies
        // atoms added in later versions, so it is only ever grantable by another superuser.
        Scope.ValidateGrant([Scope.FullAccess], Scope.PermissionAtoms)!.Description.Should().Contain("Cannot grant '*'");
    }

    [Fact]
    public void ValidateGrant_AllowsSuperuser_FromASuperuser()
    {
        Scope.ValidateGrant([Scope.FullAccess], [Scope.FullAccess])
            .Should().BeNull();
    }

    [Fact]
    public void ValidateGrant_RefusesAnUnknownPermission_EvenFromASuperuser()
    {
        Scope.ValidateGrant(["glucose.destroy"], [Scope.FullAccess])!.Description.Should().Contain("not a known permission");
    }

    [Theory]
    [InlineData("GLUCOSE.READ")]
    [InlineData("glucose.read ")]
    [InlineData("")]
    public void ValidateGrant_RefusesAPermissionThatIsNotAnExactAtom(string requested)
    {
        // Comparison is exact and ordinal, so a case or whitespace variant is unknown rather
        // than silently equivalent to the atom it resembles.
        Scope.ValidateGrant([requested], [Scope.FullAccess])!.Description.Should().Contain("not a known permission");
    }

    [Fact]
    public void ValidateGrant_AllowsTheReadTierOfAPermissionHeldAsReadWrite()
    {
        Scope.ValidateGrant(
            [Scope.GlucoseRead], [Scope.GlucoseReadWrite])
            .Should().BeNull();
    }

    [Fact]
    public void ValidateGrant_RefusesTheWriteTierOfAPermissionHeldAsReadOnly()
    {
        Scope.ValidateGrant(
            [Scope.GlucoseReadWrite], [Scope.GlucoseRead])!.Description.Should().Contain("Cannot grant 'glucose.readwrite'");
    }

    [Fact]
    public void ValidateGrant_RefusesTheWholeSetWhenAnyMemberExceedsTheGranter()
    {
        Scope.ValidateGrant(
            [Scope.GlucoseRead, Scope.AuditManage],
            [Scope.GlucoseRead, Scope.AuditRead])!.Description.Should().Contain("Cannot grant 'audit.manage'");
    }
}
