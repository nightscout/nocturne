using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Controllers.V4.AppBuilds;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Controllers.V4.AppBuilds;

/// <summary>
/// Runs the authorization filters <see cref="AppBuildsController"/> declares against each kind of
/// caller. Only the person's own signed-in session with <see cref="Scope.TenantSettings"/> passes.
/// </summary>
[Trait("Category", "Unit")]
public class AppBuildsControllerAuthorizationTests
{
    private static readonly Guid DemoSubject = Guid.NewGuid();

    [Fact]
    public async Task An_owner_session_passes()
    {
        (await Authorize(Caller(AuthType.SessionCookie, [Scope.TenantSettings]))).Should().BeNull();
    }

    [Fact]
    public async Task A_member_without_tenant_settings_is_refused()
    {
        (await Authorize(Caller(AuthType.SessionCookie, [Scope.GlucoseRead, Scope.TreatmentsReadWrite])))
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task A_public_share_is_refused()
    {
        var share = new DefaultHttpContext { RequestServices = Services(share: true) };
        share.Items["AuthContext"] = new AuthContext { IsAuthenticated = false };
        share.Items["GrantedScopes"] = (IReadOnlySet<string>)Scope.PublicShareScopes.ToHashSet(StringComparer.Ordinal);

        (await Authorize(share)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_history_clamped_caller_is_refused_even_with_tenant_settings()
    {
        (await Authorize(Caller(AuthType.SessionCookie, [Scope.TenantSettings], clamped: true)))
            .Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.ApiKey)]
    public async Task A_delegated_credential_is_refused_even_with_tenant_settings(AuthType authType)
    {
        (await Authorize(Caller(authType, [Scope.TenantSettings]))).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task The_demo_visitor_is_refused()
    {
        (await Authorize(Caller(AuthType.SessionCookie, [Scope.TenantSettings], subject: DemoSubject)))
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public void No_action_opts_out_of_the_controller_filters()
    {
        typeof(AppBuildsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Should().NotContain(method => method.GetCustomAttributes<AllowAnonymousAttribute>().Any(),
                "the installation callback is authenticated by the session as well as the signed state");
    }

    private static async Task<IActionResult?> Authorize(HttpContext httpContext)
    {
        var filters = typeof(AppBuildsController).GetCustomAttributes(inherit: true).OfType<IFilterMetadata>().ToList();
        filters.Should().NotBeEmpty();
        var context = new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()), filters);

        foreach (var filter in filters)
        {
            if (filter is IAuthorizationFilter sync) sync.OnAuthorization(context);
            else if (filter is IAsyncAuthorizationFilter async) await async.OnAuthorizationAsync(context);
            if (context.Result is not null) return context.Result;
        }

        return null;
    }

    private static HttpContext Caller(AuthType authType, string[] scopes, bool clamped = false, Guid? subject = null)
    {
        var httpContext = new DefaultHttpContext { RequestServices = Services(clamped: clamped) };
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = authType,
            SubjectId = subject ?? Guid.NewGuid(),
        };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)scopes.ToHashSet(StringComparer.Ordinal);
        return httpContext;
    }

    private static IServiceProvider Services(bool clamped = false, bool share = false)
    {
        var readContext = new CategoryReadContext();
        if (share) readContext.MarkShare();
        if (clamped) readContext.ClampMemberHistory();

        var subjects = new Mock<ISubjectRepository>();
        subjects.Setup(s => s.IsDemoSubjectAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => id == DemoSubject);

        return new ServiceCollection()
            .AddSingleton<ICategoryReadContext>(readContext)
            .AddSingleton(subjects.Object)
            .BuildServiceProvider();
    }
}
