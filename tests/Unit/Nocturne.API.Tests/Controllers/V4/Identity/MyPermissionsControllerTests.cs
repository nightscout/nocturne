using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
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
        var result = controller.GetMyPermissions().GetAwaiter().GetResult().Result as OkObjectResult;
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

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void GetMyPermissions_ReportsWhetherTheDemoGateRefusesTheCaller(
        bool isDemoSubject, bool refused)
    {
        // The demo visitor holds tenant.settings, so its scopes alone would have the web offer
        // connector controls that [DenyDemoSubject] refuses.
        using var db = TestDbContextFactory.CreateSqlite();
        var subjectId = Guid.CreateVersion7();
        using (var seed = db.CreateContext())
        {
            seed.Subjects.Add(new SubjectEntity
            {
                Id = subjectId,
                Name = "Subject",
                IsActive = true,
                IsDemoSubject = isDemoSubject,
            });
            seed.SaveChanges();
        }

        var controller = ControllerWithScopes(Scope.TenantSettings);
        var factory = new Mock<IDbContextFactory<NocturneDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => db.CreateContext());
        var httpContext = controller.ControllerContext.HttpContext;
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton(factory.Object)
            .BuildServiceProvider();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = subjectId,
        };

        var answer = Answer(controller);

        answer.RefusedAsDemoSubject.Should().Be(refused);
        answer.Scopes.Should().Contain(Scope.TenantSettings);
    }

    [Fact]
    public void GetMyPermissions_DoesNotReportASubjectlessCallerAsDemo()
    {
        // A public share has no subject; the demo gate defers to the endpoint for it.
        Answer(ControllerWithScopes(Scope.GlucoseRead)).RefusedAsDemoSubject.Should().BeFalse();
    }

    private static HttpContext SessionContext(PermissionTrie? trie, params string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<ICategoryReadContext>(new CategoryReadContext())
                .BuildServiceProvider(),
        };
        httpContext.Items["GrantedScopes"] =
            (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        if (trie is not null) httpContext.SetPermissionTrie(trie);
        return httpContext;
    }

    private static PermissionTrie Trie(params string[] permissions)
    {
        var trie = new PermissionTrie();
        trie.Add(permissions);
        return trie;
    }

    [Fact]
    public async Task TryBuildAsync_AnswersAsTheEndpointDoes_ForACallerTheFallbackAdmits()
    {
        var grant = await MyPermissionsController.TryBuildAsync(
            SessionContext(Trie(Scope.GlucoseRead), Scope.GlucoseRead));

        grant.Should().NotBeNull();
        grant!.Scopes.Should().BeEquivalentTo(new[] { Scope.GlucoseRead });
        grant.LimitTo24Hours.Should().BeFalse();
    }

    [Fact]
    public async Task TryBuildAsync_IsNull_WhereTheFallbackWouldRefuseTheEndpoint()
    {
        // The session carries the grant in place of a separate permissions call, so it must be
        // absent exactly where that call would have been refused, not an empty grant.
        (await MyPermissionsController.TryBuildAsync(SessionContext(null))).Should().BeNull();
        (await MyPermissionsController.TryBuildAsync(SessionContext(Trie()))).Should().BeNull();
    }
}
