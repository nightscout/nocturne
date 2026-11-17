using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

public sealed class RoleControllerGetRolesGateTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Mock<ITenantRoleService> _roles = new();

    private static TenantRoleDto Role(string slug, params string[] permissions) =>
        new(Guid.CreateVersion7(), slug, slug, null, [.. permissions], false, 0, DateTime.UtcNow, true);

    private RoleController BuildController(params string[] grantedScopes)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(_tenantId);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);

        return new RoleController(_roles.Object, tenantAccessor.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Fact]
    public async Task GetRoles_asInviteOnlyCaller_returnsTheAssignableRolesOnly()
    {
        var viewer = Role("viewer", "glucose.read");
        _roles
            .Setup(s => s.GetAssignableRolesAsync(
                _tenantId,
                It.Is<IReadOnlyCollection<string>>(g => g.Contains(Scope.MembersInvite) && g.Contains("glucose.read")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([viewer]);

        var result = await BuildController(Scope.MembersInvite, "glucose.read").GetRoles(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeEquivalentTo(new List<TenantRoleDto> { viewer });
        _roles.Verify(
            s => s.GetRolesAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetRoles_asMembersManageCaller_returnsTheAssignableRoles()
    {
        var viewer = Role("viewer", "glucose.read");
        _roles
            .Setup(s => s.GetAssignableRolesAsync(
                _tenantId, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([viewer]);

        var result = await BuildController(Scope.MembersManage).GetRoles(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetRoles_asRolesManageCaller_returnsEveryRole()
    {
        var all = new List<TenantRoleDto> { Role("owner", "*"), Role("viewer", "glucose.read") };
        _roles
            .Setup(s => s.GetRolesAsync(
                _tenantId,
                It.Is<IReadOnlyCollection<string>>(g => g.Contains(Scope.RolesManage)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(all);

        var result = await BuildController(Scope.RolesManage).GetRoles(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeEquivalentTo(all);
        _roles.Verify(
            s => s.GetAssignableRolesAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(Scope.SharingManage)]
    [InlineData("glucose.read")]
    public async Task GetRoles_withoutAnyOfTheThreeScopes_isForbidden(string scope)
    {
        var result = await BuildController(scope).GetRoles(CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task CreateRole_asInviteOnlyCaller_isForbidden()
    {
        var result = await BuildController(Scope.MembersInvite, Scope.MembersManage)
            .CreateRole(new CreateRoleRequest("x", null, []), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }
}
