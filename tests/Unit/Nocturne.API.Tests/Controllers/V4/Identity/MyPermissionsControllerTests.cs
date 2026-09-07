using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// The frontend hides the reports an anonymous share cannot load, which means the share has to be
/// able to read back its own granted categories. These pin the two halves of that: the endpoint is
/// reachable without authentication, and it answers with the scopes the request resolved to.
/// </summary>
public class MyPermissionsControllerTests
{
    private static MyPermissionsController ControllerWithScopes(params string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] =
            (IReadOnlySet<string>)new HashSet<string>(grantedScopes);

        return new MyPermissionsController
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Fact]
    public void Controller_CarriesNoAuthenticationRequirement()
    {
        // A public share is deliberately IsAuthenticated: false, so [Authorize] 401s it and the
        // share view can never learn which categories it may read.
        typeof(MyPermissionsController).GetCustomAttributes(inherit: true)
            .OfType<IAuthorizeData>()
            .Should().BeEmpty();
    }

    [Fact]
    public void Controller_StaysUnderTheDefaultDenyFallback()
    {
        // No [AllowAnonymous] either: the fallback policy's non-empty permission trie is what
        // separates a share (which has one) from a visitor with no grant at all.
        typeof(MyPermissionsController).GetCustomAttributes(inherit: true)
            .OfType<AllowAnonymousAttribute>()
            .Should().BeEmpty();
    }

    [Fact]
    public void GetMyPermissions_ReturnsTheSharesGrantedCategories()
    {
        var controller = ControllerWithScopes(
            Scope.GlucoseRead,
            Scope.ReportsRead);

        var result = controller.GetMyPermissions().Result as OkObjectResult;

        result.Should().NotBeNull();
        result!.Value.Should().BeEquivalentTo(
            new[] { Scope.GlucoseRead, Scope.ReportsRead });
    }

    [Fact]
    public void GetMyPermissions_ReturnsEmpty_WhenNothingResolved()
    {
        var controller = ControllerWithScopes();

        var result = controller.GetMyPermissions().Result as OkObjectResult;

        result.Should().NotBeNull();
        result!.Value.Should().BeEquivalentTo(System.Array.Empty<string>());
    }
}
