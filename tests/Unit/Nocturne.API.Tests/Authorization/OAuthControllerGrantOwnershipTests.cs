using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Models.OAuth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// <c>DELETE /api/oauth/grants/{id}</c> authorizes against the grant's owner. A grant owned by
/// another subject is answered the same way as one that does not exist, so the refusal never
/// confirms an id the caller may not touch.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "OAuth")]
public class OAuthControllerGrantOwnershipTests
{
    private readonly Mock<IOAuthGrantService> _grantService = new();
    private readonly Guid _callerSubjectId = Guid.CreateVersion7();

    [Fact]
    public async Task A_grant_the_caller_does_not_own_is_not_found_and_is_not_revoked()
    {
        var grantId = Guid.CreateVersion7();
        GrantFor(grantId, grant: null);

        var result = await CreateController().DeleteGrant(grantId);

        var refusal = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        refusal.Value.Should().BeOfType<OAuthError>().Which.Error.Should().Be("not_found");
        _grantService.Verify(
            s => s.RevokeGrantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task The_callers_own_grant_is_revoked()
    {
        var grantId = Guid.CreateVersion7();
        GrantFor(grantId, new OAuthGrantInfo { Id = grantId, SubjectId = _callerSubjectId });

        var result = await CreateController().DeleteGrant(grantId);

        result.Should().BeOfType<NoContentResult>();
        _grantService.Verify(
            s => s.RevokeGrantAsync(grantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    private void GrantFor(Guid grantId, OAuthGrantInfo? grant) => _grantService
        .Setup(s => s.GetGrantForSubjectAsync(
            grantId, _callerSubjectId, It.IsAny<CancellationToken>()))
        .ReturnsAsync(grant);

    private OAuthController CreateController()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _callerSubjectId,
        };

        return new OAuthController(
            Mock.Of<IOAuthClientService>(),
            _grantService.Object,
            Mock.Of<IOAuthTokenService>(),
            Mock.Of<IOAuthDeviceCodeService>(),
            Mock.Of<ISubjectService>(),
            Mock.Of<IJwtService>(),
            Mock.Of<IOAuthTokenRevocationCache>(),
            NullLogger<OAuthController>.Instance
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
