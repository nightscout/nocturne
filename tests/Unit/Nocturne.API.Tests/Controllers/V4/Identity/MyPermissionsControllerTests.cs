using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;
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
    private static MyPermissionsController ControllerWithScopes(params string[] grantedScopes) =>
        Controller(new CategoryReadContext(), grantedScopes);

    private static MyPermissionsController Controller(
        ICategoryReadContext categoryReadContext, params string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] =
            (IReadOnlySet<string>)new HashSet<string>(grantedScopes);

        return new MyPermissionsController(categoryReadContext)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static MyPermissionsResponse Answer(MyPermissionsController controller)
    {
        var result = controller.GetMyPermissions().Result as OkObjectResult;
        result.Should().NotBeNull();
        return result!.Value.Should().BeOfType<MyPermissionsResponse>().Subject;
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

        Answer(controller).Scopes.Should().BeEquivalentTo(
            new[] { Scope.GlucoseRead, Scope.ReportsRead });
    }

    [Fact]
    public void GetMyPermissions_ReturnsEmpty_WhenNothingResolved()
    {
        var controller = ControllerWithScopes();

        Answer(controller).Scopes.Should().BeEmpty();
    }

    [Fact]
    public void GetMyPermissions_ReportsAClampedMember()
    {
        var category = new CategoryReadContext();
        category.ClampMemberHistory();

        Answer(Controller(category, Scope.GlucoseRead)).LimitTo24Hours.Should().BeTrue();
    }

    [Fact]
    public void GetMyPermissions_ReportsAShareWithoutFullHistory()
    {
        var category = new CategoryReadContext();
        category.MarkShare();

        Answer(Controller(category, Scope.GlucoseRead)).LimitTo24Hours.Should().BeTrue();
    }

    [Fact]
    public void GetMyPermissions_ReportsAnUnclampedViewer()
    {
        var member = new CategoryReadContext();
        var fullHistoryShare = new CategoryReadContext();
        fullHistoryShare.MarkShare();
        fullHistoryShare.SetFullHistory(true);

        Answer(Controller(member, Scope.GlucoseRead)).LimitTo24Hours.Should().BeFalse();
        Answer(Controller(fullHistoryShare, Scope.GlucoseRead)).LimitTo24Hours.Should().BeFalse();
    }
}
