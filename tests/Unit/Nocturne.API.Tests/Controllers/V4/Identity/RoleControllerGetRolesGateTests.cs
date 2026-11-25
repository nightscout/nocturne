using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

public sealed class RoleControllerGetRolesGateTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Mock<ITenantRoleService> _roles = new();

    public RoleControllerGetRolesGateTests()
    {
        _roles
            .Setup(s => s.GetRoleHoldersAsync(It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoleHolders([], [], [], new HashSet<Guid>()));
    }

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

        var result = await BuildController(Scope.MembersInvite, "glucose.read").GetRoles(Mock.Of<ITenantMembershipRepository>(), TimeProvider.System, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeEquivalentTo(new List<TenantRoleDto> { viewer with { CanGiveToTeam = true } });
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

        var result = await BuildController(Scope.MembersManage).GetRoles(Mock.Of<ITenantMembershipRepository>(), TimeProvider.System, CancellationToken.None);

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

        var result = await BuildController(Scope.RolesManage).GetRoles(Mock.Of<ITenantMembershipRepository>(), TimeProvider.System, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeEquivalentTo(
            new List<TenantRoleDto> { all[0], all[1] with { CanGiveToTeam = true } });
        _roles.Verify(
            s => s.GetAssignableRolesAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(Scope.SharingManage)]
    [InlineData("glucose.read")]
    public void GetRoles_withoutAnyOfTheThreeScopes_isForbidden(string scope)
    {
        Gate(nameof(RoleController.GetRoles), scope)
            .ShouldBeScopeRefusal(Scope.RolesManage, Scope.MembersInvite, Scope.MembersManage)
            .Detail.Should().Be(
                "This operation requires one of these scopes: 'roles.manage', 'members.invite', 'members.manage'.");
    }

    [Fact]
    public void CreateRole_asInviteOnlyCaller_isForbidden()
    {
        Gate(nameof(RoleController.CreateRole), Scope.MembersInvite, Scope.MembersManage)
            .ShouldBeScopeRefusal(Scope.RolesManage);
    }

    private static IActionResult? Gate(string action, params string[] grantedScopes) =>
        ActionGate.Evaluate(
            typeof(RoleController),
            action,
            new HashSet<string>(grantedScopes),
            new AuthContext { IsAuthenticated = true });

    [Theory]
    [InlineData(RoleSeeds.Owner, false)]
    [InlineData(RoleSeeds.Admin, true)]
    public async Task GetRoles_tellsANonOwnerThatARoleATeamHoldsMayOnlyBeNarrowed(string callerRole, bool onlyOwnersMayWiden)
    {
        var held = Role("nights", "glucose.read");
        var free = Role("days", "glucose.read");
        var caller = Guid.CreateVersion7();
        _roles.Setup(s => s.GetRolesAsync(_tenantId, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([held, free]);
        _roles.Setup(s => s.GetRoleHoldersAsync(It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoleHolders([], [], [], new HashSet<Guid> { held.Id }));
        var members = new Mock<ITenantMembershipRepository>();
        members.Setup(m => m.FindMemberAsync(_tenantId, caller, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantMemberRecord(
                Guid.CreateVersion7(), _tenantId, caller, "Sam", null, true, false, false, false, false,
                [new TenantMemberRoleRecord(Guid.CreateVersion7(), callerRole, callerRole, RoleSeeds.Permissions[callerRole])],
                null, null, null, false, null, null, null, DateTime.UtcNow, []));
        var controller = BuildController(Scope.RolesManage);
        controller.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true, AuthType = AuthType.SessionCookie, SubjectId = caller,
        };

        var result = await controller.GetRoles(members.Object, TimeProvider.System, CancellationToken.None);

        var roles = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<List<TenantRoleDto>>().Subject;
        roles.Single(r => r.Id == held.Id).Should().Match<TenantRoleDto>(r => r.HeldByTeam && r.OnlyOwnersMayWiden == onlyOwnersMayWiden);
        roles.Single(r => r.Id == free.Id).Should().Match<TenantRoleDto>(r => !r.HeldByTeam && !r.OnlyOwnersMayWiden);
    }

    [Theory]
    [InlineData(RoleSeeds.Owner, false)]
    [InlineData(RoleSeeds.Admin, true)]
    public async Task GetRoles_tellsWhatACallerMayChooseForARoleATeamHolds(string callerRole, bool sendsCoveredPermissions)
    {
        var held = Role("nights", [.. RoleSeeds.Permissions[RoleSeeds.Caretaker]]);
        var free = Role("days", Scope.GlucoseRead);
        var caller = Guid.CreateVersion7();
        _roles.Setup(s => s.GetRolesAsync(_tenantId, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([held, free]);
        _roles.Setup(s => s.GetRoleHoldersAsync(It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoleHolders([], [], [], new HashSet<Guid> { held.Id }));
        var members = new Mock<ITenantMembershipRepository>();
        members.Setup(m => m.FindMemberAsync(_tenantId, caller, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantMemberRecord(
                Guid.CreateVersion7(), _tenantId, caller, "Sam", null, true, false, false, false, false,
                [new TenantMemberRoleRecord(Guid.CreateVersion7(), callerRole, callerRole, RoleSeeds.Permissions[callerRole])],
                null, null, null, false, null, null, null, DateTime.UtcNow, []));
        var controller = BuildController(Scope.RolesManage);
        controller.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true, AuthType = AuthType.SessionCookie, SubjectId = caller,
        };

        var result = await controller.GetRoles(members.Object, TimeProvider.System, CancellationToken.None);

        var roles = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<List<TenantRoleDto>>().Subject;
        var heldRole = roles.Single(r => r.Id == held.Id);
        heldRole.TeamRefusedPermissions.Should().BeEquivalentTo(Scope.AdministrativePermissions);
        if (sendsCoveredPermissions)
        {
            heldRole.CoveredPermissions.Should().Contain([Scope.TreatmentsRead, Scope.TreatmentsReadWrite, Scope.AlertsRead])
                .And.NotContain(Scope.GlucoseReadWrite);
        }
        else
        {
            heldRole.CoveredPermissions.Should().BeNull();
        }

        var freeRole = roles.Single(r => r.Id == free.Id);
        freeRole.TeamRefusedPermissions.Should().BeEmpty();
        freeRole.CoveredPermissions.Should().BeNull();
    }

    [Fact]
    public async Task GetRoles_saysWhichRolesTheCallerMayGiveToATeam()
    {
        var data = Role("days", Scope.GlucoseRead, Scope.TreatmentsReadWrite);
        var runsTheSite = Role("helpers", Scope.GlucoseRead, Scope.MembersInvite);
        var empty = Role("nothing");
        var beyondCaller = Role("nights", Scope.GlucoseRead) with { CanGrant = false };
        _roles.Setup(s => s.GetRolesAsync(_tenantId, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([data, runsTheSite, empty, beyondCaller]);

        var result = await BuildController(Scope.RolesManage).GetRoles(Mock.Of<ITenantMembershipRepository>(), TimeProvider.System, CancellationToken.None);

        var roles = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<List<TenantRoleDto>>().Subject;
        roles.Where(r => r.CanGiveToTeam).Select(r => r.Id).Should().Equal(data.Id);
    }
}
