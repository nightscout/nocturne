using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.ClientDevices;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// <c>DELETE /api/v4/account/connected-apps/{grantId}</c> authorizes against the grant's owner,
/// through the same ownership-scoped lookup as the OAuth grants API.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectedAppsControllerTests
{
    private readonly Mock<IOAuthGrantService> _grantService = new();
    private readonly Mock<IClientDeviceService> _deviceService = new();
    private readonly Guid _callerSubjectId = Guid.CreateVersion7();

    [Fact]
    public async Task Revoke_a_grant_the_caller_does_not_own_is_not_found_and_is_not_revoked()
    {
        var grantId = Guid.CreateVersion7();
        GrantFor(grantId, grant: null);

        var result = await CreateController().Revoke(grantId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        _grantService.Verify(
            s => s.RevokeGrantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Revoke_the_callers_own_app_grant_is_revoked()
    {
        var grantId = Guid.CreateVersion7();
        GrantFor(grantId, new OAuthGrantInfo
        {
            Id = grantId,
            SubjectId = _callerSubjectId,
            GrantType = OAuthGrantTypes.App,
        });

        var result = await CreateController().Revoke(grantId, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _grantService.Verify(
            s => s.RevokeGrantAsync(grantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A guest link records the data owner's subject id, so the owner reaches their own guest
    /// grant through this route; only app grants are connected apps.
    /// </summary>
    [Fact]
    public async Task Revoke_a_guest_grant_is_not_found_and_is_not_revoked()
    {
        var grantId = Guid.CreateVersion7();
        GrantFor(grantId, new OAuthGrantInfo
        {
            Id = grantId,
            SubjectId = _callerSubjectId,
            GrantType = OAuthGrantTypes.Guest,
        });

        var result = await CreateController().Revoke(grantId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        _grantService.Verify(
            s => s.RevokeGrantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task List_reports_each_apps_paired_device_count_from_one_grouped_lookup()
    {
        var grantA = Guid.CreateVersion7();
        var grantB = Guid.CreateVersion7();
        _grantService
            .Setup(s => s.GetGrantsForSubjectAsync(_callerSubjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new OAuthGrantInfo
                {
                    Id = grantA,
                    SubjectId = _callerSubjectId,
                    GrantType = OAuthGrantTypes.App,
                    ClientDisplayName = "Prelude",
                },
                new OAuthGrantInfo
                {
                    Id = grantB,
                    SubjectId = _callerSubjectId,
                    GrantType = OAuthGrantTypes.App,
                    ClientDisplayName = "Companion",
                },
            ]);
        var controller = CreateController();
        _deviceService
            .Setup(s => s.GetDeviceCountsByGrantAsync(
                It.Is<IReadOnlyCollection<Guid>>(ids =>
                    ids.Count == 2 && ids.Contains(grantA) && ids.Contains(grantB)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, int> { [grantA] = 3 });

        var result = await controller.List(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var apps = ok.Value.Should().BeAssignableTo<List<ConnectedAppDto>>().Subject;
        apps.Single(a => a.GrantId == grantA).DeviceCount.Should().Be(3);
        apps.Single(a => a.GrantId == grantB).DeviceCount.Should().Be(0);
        _deviceService.Verify(
            s => s.GetDeviceCountsByGrantAsync(
                It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private void GrantFor(Guid grantId, OAuthGrantInfo? grant) => _grantService
        .Setup(s => s.GetGrantForSubjectAsync(
            grantId, _callerSubjectId, It.IsAny<CancellationToken>()))
        .ReturnsAsync(grant);

    private ConnectedAppsController CreateController()
    {
        _deviceService
            .Setup(s => s.GetDeviceCountsByGrantAsync(
                It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, int>());

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _callerSubjectId,
        };

        return new ConnectedAppsController(
            _grantService.Object,
            Mock.Of<IOAuthTokenService>(),
            _deviceService.Object,
            NullLogger<ConnectedAppsController>.Instance
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
