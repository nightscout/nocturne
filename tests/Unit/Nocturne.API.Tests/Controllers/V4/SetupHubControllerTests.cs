using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.SetupHub;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

public class SetupHubControllerTests
{
    private static readonly SetupHubStatus Hub = new([], 0, 0, "rev", false);

    private static (SetupHubController, Mock<ISetupHubService>) Build(params string[] grantedScopes)
    {
        var service = new Mock<ISetupHubService>(MockBehavior.Strict);
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        var controller = new SetupHubController(service.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        return (controller, service);
    }

    [Theory]
    [InlineData(Scope.TenantSettings)]
    [InlineData(Scope.GlucoseRead)]
    public async Task EveryEndpoint_RefusesAMemberWhoIsNotAnOwner(string scope)
    {
        var (controller, _) = Build(scope);

        (await controller.GetSetupHub(CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await controller.SetSetupHubItemState(
                SetupHubItemKey.Alerts,
                new SetSetupHubItemStateRequest { State = SetupHubItemState.NotForMe },
                CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
        (await controller.DismissSetupStrip(new DismissSetupStripRequest { Revision = "rev" }, CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetSetupHub_ReturnsTheOwnersHub()
    {
        var (controller, service) = Build(Scope.FullAccess);
        service.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Hub);

        var result = await controller.GetSetupHub(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(Hub);
    }

    [Fact]
    public async Task SetState_RefusesDoneWithoutAskingTheService()
    {
        var (controller, _) = Build(Scope.FullAccess);

        var result = await controller.SetSetupHubItemState(
            SetupHubItemKey.Alerts, new SetSetupHubItemStateRequest { State = SetupHubItemState.Done }, CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task SetState_MapsAnUnlistedItemTo404_AndADoneItemTo409()
    {
        var (controller, service) = Build(Scope.FullAccess);
        service.Setup(s => s.SetStateAsync(SetupHubItemKey.ConnectData, SetupHubItemState.NotForMe, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());
        service.Setup(s => s.SetStateAsync(SetupHubItemKey.Devices, SetupHubItemState.NotForMe, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException());
        var notForMe = new SetSetupHubItemStateRequest { State = SetupHubItemState.NotForMe };

        (await controller.SetSetupHubItemState(SetupHubItemKey.ConnectData, notForMe, CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();
        (await controller.SetSetupHubItemState(SetupHubItemKey.Devices, notForMe, CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
    }
}
