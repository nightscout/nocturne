using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.ClientDevices;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.ClientDevices;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.ClientDevices;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.ClientDevices;

[Trait("Category", "Unit")]
public class ClientDevicesControllerTests
{
    private readonly Mock<IClientDeviceService> _service = new();
    private readonly ClientDevicesController _controller;

    public ClientDevicesControllerTests()
    {
        _controller = new ClientDevicesController(
            _service.Object, new Mock<ILogger<ClientDevicesController>>().Object);

        var context = new DefaultHttpContext();
        context.SetAuthContext(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.OAuthAccessToken,
            SubjectId = Guid.CreateVersion7(),
            TokenId = Guid.CreateVersion7(),
        });
        context.SetGrantedScopes(new HashSet<string> { Scope.DeviceNotify });
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    [Fact]
    public async Task Register_passes_the_credentials_grant_to_the_service()
    {
        var subjectId = _controller.HttpContext.GetSubjectId()!.Value;
        var grantId = _controller.HttpContext.GetAuthContext()!.TokenId;
        var request = new RegisterDeviceRequest
        {
            InstallId = "install-1",
            Kind = DeviceKinds.Companion,
            Capabilities = [DeviceCapabilities.Notify],
        };

        _service
            .Setup(s => s.RegisterAsync(
                subjectId, request, It.IsAny<IReadOnlySet<string>>(), grantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientDeviceDto { Id = Guid.CreateVersion7(), InstallId = request.InstallId });

        var result = await _controller.Register(request, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        _service.Verify(s => s.RegisterAsync(
            subjectId, request, It.IsAny<IReadOnlySet<string>>(), grantId, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
