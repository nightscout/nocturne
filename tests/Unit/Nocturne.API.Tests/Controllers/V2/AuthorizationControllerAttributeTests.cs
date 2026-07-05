using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Nocturne.API.Controllers.V2;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V2;

/// <summary>
/// Guards the authorization attributes on <see cref="AuthorizationController"/>. The token-exchange
/// endpoint is the credential bootstrap for NSClientV3/AAPS: the access token in the URL is the
/// caller's only credential, so the endpoint must be reachable anonymously. The class carries a
/// <c>[Authorize]</c> fallback, so a missing <c>[AllowAnonymous]</c> silently 401s every uploader
/// before the token is ever validated.
/// </summary>
public class AuthorizationControllerAttributeTests
{
    [Fact]
    public void GenerateJwtFromAccessToken_IsAllowAnonymous()
    {
        var method = typeof(AuthorizationController)
            .GetMethod(nameof(AuthorizationController.GenerateJwtFromAccessToken));

        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void Controller_RetainsClassLevelAuthorize()
    {
        // The anonymous exception must be scoped to the one bootstrap endpoint; the controller as a
        // whole (subject/role management, permissions) must still require authentication.
        var authorize = typeof(AuthorizationController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
    }
}
