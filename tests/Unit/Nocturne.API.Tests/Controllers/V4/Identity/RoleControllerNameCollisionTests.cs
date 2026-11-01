using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

public sealed class RoleControllerNameCollisionTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Mock<ITenantRoleService> _roles = new();

    private RoleController BuildController()
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(_tenantId);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>([Scope.RolesManage, "glucose.read"]);

        return new RoleController(_roles.Object, tenantAccessor.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            ProblemDetailsFactory = new EchoingProblemDetailsFactory(),
        };
    }

    private static void ShouldBeProblem(IActionResult result, SaveRoleResult expected)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var details = problem.Value.Should().BeAssignableTo<ProblemDetails>().Subject;
        details.Detail.Should().Be(expected.ErrorDescription);
        details.Title.Should().Be(expected.ErrorCode);
    }

    [Theory]
    [InlineData(SaveRoleResult.NameTaken)]
    [InlineData(SaveRoleResult.NameReserved)]
    public async Task CreateRole_whenTheNameIsRefused_isAConflictProblem(string code)
    {
        var refusal = code == SaveRoleResult.NameTaken ? SaveRoleResult.Taken : SaveRoleResult.Reserved;
        _roles
            .Setup(s => s.CreateRoleAsync(
                _tenantId, "Owner", null, It.IsAny<List<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(refusal);

        var result = await BuildController()
            .CreateRole(new CreateRoleRequest("Owner", null, ["glucose.read"]), CancellationToken.None);

        ShouldBeProblem(result, refusal);
    }

    [Theory]
    [InlineData(SaveRoleResult.NameTaken)]
    [InlineData(SaveRoleResult.NameReserved)]
    public async Task UpdateRole_whenTheNameIsRefused_isAConflictProblem(string code)
    {
        var refusal = code == SaveRoleResult.NameTaken ? SaveRoleResult.Taken : SaveRoleResult.Reserved;
        var roleId = Guid.CreateVersion7();
        _roles
            .Setup(s => s.UpdateRoleAsync(
                _tenantId, roleId, "Day Shift", null, It.IsAny<List<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(refusal);

        var result = await BuildController()
            .UpdateRole(roleId, new UpdateRoleRequest("Day Shift", null, ["glucose.read"]), CancellationToken.None);

        ShouldBeProblem(result, refusal);
    }

    [Fact]
    public async Task CreateRole_whenSaved_isCreated()
    {
        var role = new TenantRoleDto(Guid.CreateVersion7(), "Night Shift", "night-shift", null, ["glucose.read"], false, 0, DateTime.UtcNow, true);
        _roles
            .Setup(s => s.CreateRoleAsync(
                _tenantId, "Night Shift", null, It.IsAny<List<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SaveRoleResult.Saved(role));

        var result = await BuildController()
            .CreateRole(new CreateRoleRequest("Night Shift", null, ["glucose.read"]), CancellationToken.None);

        var created = result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        created.Value.Should().Be(role);
    }

    [Fact]
    public async Task UpdateRole_whenTheRoleIsUnknown_isNotFound()
    {
        _roles
            .Setup(s => s.UpdateRoleAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SaveRoleResult?)null);

        var result = await BuildController()
            .UpdateRole(Guid.CreateVersion7(), new UpdateRoleRequest("x", null, ["glucose.read"]), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }
}
