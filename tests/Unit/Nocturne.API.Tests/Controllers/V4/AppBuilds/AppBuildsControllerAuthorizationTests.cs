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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_public_share_is_refused_as_unauthenticated(bool fullHistory)
    {
        (await Authorize(Share(fullHistory))).Should().BeOfType<UnauthorizedResult>(
            "a share is not authenticated, and tenant.settings is not a read requirement");
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
    public async Task A_signed_out_callback_returns_to_the_web_page_marked_signed_out()
    {
        var anonymous = new DefaultHttpContext { RequestServices = Services() };
        anonymous.Items["AuthContext"] = new AuthContext { IsAuthenticated = false };

        (await CallbackResult(anonymous)).Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/settings/app-builds?error=signed_out");
    }

    [Fact]
    public async Task A_forbidden_callback_returns_to_the_web_page_marked_forbidden()
    {
        (await CallbackResult(Caller(AuthType.SessionCookie, [Scope.GlucoseRead]))).Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/settings/app-builds?error=forbidden");
    }

    [Fact]
    public void Only_the_callback_skips_the_framework_challenge_and_it_turns_refusals_into_redirects()
    {
        var anonymous = typeof(AppBuildsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<AllowAnonymousAttribute>().Any())
            .ToList();

        anonymous.Should().ContainSingle().Which.Name.Should().Be(nameof(AppBuildsController.CompleteAppBuildLink));
        anonymous[0].GetCustomAttribute<RedirectRefusedCallbackAttribute>().Should().NotBeNull(
            "the controller's own filters still require the session there; only their answer changes");
    }

    private static async Task<IActionResult?> Authorize(HttpContext httpContext)
    {
        var filters = ControllerFilters();
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

    private static async Task<IActionResult?> CallbackResult(HttpContext httpContext)
    {
        var refused = await Authorize(httpContext);
        refused.Should().NotBeNull();

        var context = new ResultExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()), [], refused!, controller: new object());
        new RedirectRefusedCallbackAttribute().OnResultExecuting(context);
        return context.Result;
    }

    private static List<IFilterMetadata> ControllerFilters()
    {
        var filters = typeof(AppBuildsController).GetCustomAttributes(inherit: true).OfType<IFilterMetadata>().ToList();
        filters.Should().NotBeEmpty();
        return filters;
    }

    private static HttpContext Share(bool fullHistory)
    {
        var share = new DefaultHttpContext { RequestServices = Services(share: true, fullHistory: fullHistory) };
        share.Items["AuthContext"] = new AuthContext { IsAuthenticated = false };
        share.Items["GrantedScopes"] = (IReadOnlySet<string>)Scope.PublicShareScopes.ToHashSet(StringComparer.Ordinal);
        return share;
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

    private static IServiceProvider Services(bool clamped = false, bool share = false, bool fullHistory = false)
    {
        var readContext = new CategoryReadContext();
        if (share) readContext.MarkShare();
        if (fullHistory) readContext.SetFullHistory(true);
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
