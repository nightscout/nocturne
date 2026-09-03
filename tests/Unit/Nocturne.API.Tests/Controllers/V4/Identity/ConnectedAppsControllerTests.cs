using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Auth;
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

    private void GrantFor(Guid grantId, OAuthGrantInfo? grant) => _grantService
        .Setup(s => s.GetGrantForSubjectAsync(
            grantId, _callerSubjectId, It.IsAny<CancellationToken>()))
        .ReturnsAsync(grant);

    private ConnectedAppsController CreateController()
    {
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
            NullLogger<ConnectedAppsController>.Instance
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
