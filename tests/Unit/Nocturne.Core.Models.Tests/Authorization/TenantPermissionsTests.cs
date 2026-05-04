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
}
