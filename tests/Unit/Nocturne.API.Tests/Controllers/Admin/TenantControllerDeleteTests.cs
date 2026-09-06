using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Controllers.Admin;

public class TenantControllerDeleteTests
{
    private readonly Mock<ITenantService> _tenantService = new();
    private readonly Mock<ITenantRoleService> _roleService = new();
    private readonly TenantController _controller;

    public TenantControllerDeleteTests()
    {
        _controller = new TenantController(_tenantService.Object, _roleService.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task Delete_ReturnsNoContent_WhenTenantExists()
    {
        var id = Guid.NewGuid();

        var result = await _controller.Delete(id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _tenantService.Verify(s => s.DeleteAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ITenantService.DeleteAsync throws for an unknown id. Unhandled, that surfaced as a 500,
    // so a caller tearing an account down (nocturne-cloud's retention purge) could never finish
    // one whose tenant had already been removed out of band — it retried and failed every sweep.
    [Fact]
    public async Task Delete_Returns404_WhenTenantIsAlreadyGone()
    {
        var id = Guid.NewGuid();
        _tenantService
            .Setup(s => s.DeleteAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException($"Tenant {id} not found"));

        var result = await _controller.Delete(id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_DoesNotSwallowOtherFailures()
    {
        var id = Guid.NewGuid();
        _tenantService
            .Setup(s => s.DeleteAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is on fire"));

        var act = () => _controller.Delete(id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
