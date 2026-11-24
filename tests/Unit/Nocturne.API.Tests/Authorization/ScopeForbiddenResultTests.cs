using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// The refusal a caller missing a scope reads: one ProblemDetails naming the scopes on a V4 route,
/// with a <c>code</c> and the <c>scopes</c>, whichever mechanism refused, and on a Nightscout
/// action the bare 403 those actions answered before.
/// </summary>
public class ScopeForbiddenResultTests
{
    private const string V4Path = "/api/v4/member-invites";

    [Fact]
    public void AttributeOnAV4Route_NamesTheScope()
    {
        Refuse(new RequireScopeAttribute(Scope.MembersManage), V4Path, Scope.GlucoseRead)
            .Should().BeOfType<ScopeForbiddenResult>()
            .Which.Detail.Should().Be("This operation requires the 'members.manage' scope.");
    }

    [Fact]
    public void AttributeAcceptingAnyOfSeveralScopes_NamesThemAll()
    {
        Refuse(new RequireScopeAttribute(Scope.MembersInvite, Scope.MembersManage), V4Path, Scope.GlucoseRead)
            .Should().BeOfType<ScopeForbiddenResult>()
            .Which.Detail.Should().Be("This operation requires one of these scopes: 'members.invite', 'members.manage'.");
    }

    [Fact]
    public void AttributeRequiringEveryScope_NamesThemAllAsRequired()
    {
        Refuse(new RequireScopeAttribute(true, Scope.FullAccess, Scope.AlertsReadWrite), V4Path, Scope.AlertsReadWrite)
            .Should().BeOfType<ScopeForbiddenResult>()
            .Which.Detail.Should().Be("This operation requires all of these scopes: '*', 'alerts.readwrite'.");
    }

    [Fact]
    public void AuthenticatedCallerWithNoScopesOnAV4Route_ReadsTheSameRefusal()
    {
        Refuse(new RequireScopeAttribute(Scope.AuditRead), V4Path)
            .Should().BeOfType<ScopeForbiddenResult>()
            .Which.Scopes.Should().Equal(Scope.AuditRead);
    }

    [Theory]
    [InlineData("/api/v1/entries")]
    [InlineData("/api/v2/properties")]
    [InlineData("/api/v3/entries")]
    public void AttributeOnANightscoutRoute_KeepsTheBareForbid(string path)
    {
        Refuse(new RequireScopeAttribute(Scope.GlucoseReadWrite), path, Scope.GlucoseRead)
            .Should().BeOfType<ForbidResult>()
            .Which.AuthenticationSchemes.Should().BeEmpty();
        Refuse(new RequireScopeAttribute(Scope.GlucoseReadWrite), path)
            .Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [InlineData("/api/v1/entries")]
    [InlineData("/api/v3/entries")]
    [InlineData(V4Path)]
    public void UnauthenticatedCaller_StillReads401(string path)
    {
        Refuse(new RequireScopeAttribute(Scope.GlucoseReadWrite), path, authenticated: false, Scope.GlucoseRead)
            .Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public void DeclaredWriteScopeOnAV4Route_NamesTheDeclaredScope()
    {
        var httpContext = Context(V4Path, authenticated: true, Scope.GlucoseRead);
        var context = new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            [],
            new Dictionary<string, object?>(),
            new GlucoseWrites());

        new RequireDeclaredWriteScopeAttribute().OnActionExecuting(context);

        context.Result.Should().BeOfType<ScopeForbiddenResult>()
            .Which.Detail.Should().Be("This operation requires the 'glucose.readwrite' scope.");
    }

    [Fact]
    public async Task TheAttributeAndAPerRecordGuard_AnswerTheSameBody()
    {
        var fromAttribute = Refuse(new RequireScopeAttribute(Scope.SleepReadWrite), V4Path, Scope.GlucoseRead);
        var fromGuard = new GlucoseWrites
        {
            ControllerContext = new ControllerContext { HttpContext = Context(V4Path, authenticated: true) },
        }.ForbiddenForScope(Scope.SleepReadWrite);

        var attributeBody = await Execute(fromAttribute!, V4Path);
        var guardBody = await Execute(fromGuard, V4Path);

        attributeBody.Status.Should().Be(StatusCodes.Status403Forbidden);
        attributeBody.ContentType.Should().StartWith("application/problem+json");
        attributeBody.Problem.GetProperty("title").GetString().Should().Be("Forbidden");
        attributeBody.Problem.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status403Forbidden);
        attributeBody.Problem.GetProperty("detail").GetString()
            .Should().Be("This operation requires the 'sleep.readwrite' scope.");
        attributeBody.Problem.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        attributeBody.Problem.TryGetProperty("traceId", out _).Should().BeTrue();

        attributeBody.Problem.GetProperty("code").GetString().Should().Be("missing_scope");
        attributeBody.Problem.GetProperty("scopes").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(Scope.SleepReadWrite);

        Without(guardBody.Problem, "traceId").Should().Be(Without(attributeBody.Problem, "traceId"));
        guardBody.Status.Should().Be(attributeBody.Status);
        guardBody.ContentType.Should().Be(attributeBody.ContentType);
    }

    [Fact]
    public async Task ARefusalAcceptingAnyOfSeveralScopes_ListsThemAllUnderScopes()
    {
        var refusal = Refuse(new RequireScopeAttribute(Scope.MembersInvite, Scope.MembersManage), V4Path, Scope.GlucoseRead);

        var body = await Execute(refusal!, V4Path);

        body.Problem.GetProperty("code").GetString().Should().Be("missing_scope");
        body.Problem.GetProperty("scopes").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(Scope.MembersInvite, Scope.MembersManage);
    }

    [Fact]
    public async Task APerRecordGuardOnAV1Route_CarriesNoCodeOrScopes()
    {
        const string v1Path = "/api/v1/activity";
        var refusal = new GlucoseWrites
        {
            ControllerContext = new ControllerContext { HttpContext = Context(v1Path, authenticated: true) },
        }.ForbiddenForScope(Scope.SleepReadWrite);

        var body = await Execute(refusal, v1Path);

        body.Problem.GetProperty("detail").GetString()
            .Should().Be("This operation requires the 'sleep.readwrite' scope.");
        body.Problem.TryGetProperty("code", out _).Should().BeFalse();
        body.Problem.TryGetProperty("scopes", out _).Should().BeFalse();
    }

    /// <summary>
    /// <c>/pebble</c> is a Nightscout V1 endpoint with no version in its path, so it is told apart by
    /// its <see cref="NightscoutEndpointAttribute"/>.
    /// </summary>
    [Fact]
    public void Pebble_KeepsTheBareForbid()
    {
        ActionGate.Evaluate(
                typeof(PebbleController),
                nameof(PebbleController.GetPebbleData),
                new HashSet<string> { Scope.TreatmentsRead },
                new AuthContext { IsAuthenticated = true })
            .Should().BeOfType<ForbidResult>()
            .Which.AuthenticationSchemes.Should().BeEmpty();
    }

    /// <summary>
    /// <see cref="ScopeForbiddenResult.For"/> tells a Nightscout action by its path or its
    /// <see cref="NightscoutEndpointAttribute"/>, so a V1, V2 or V3 action on any other path that
    /// carries neither would answer the V4 body.
    /// </summary>
    [Fact]
    public void EveryScopeGatedNightscoutAction_IsRecognisedAsNightscout()
    {
        string[] legacy = ["Nocturne.API.Controllers.V1", "Nocturne.API.Controllers.V2", "Nocturne.API.Controllers.V3"];

        var actions = ControllerActionReflection.GetControllers()
            .Where(c => legacy.Contains(c.Namespace))
            .SelectMany(c => ControllerActionReflection.GetActionMethods(c).Select(a => (Controller: c, Action: a)))
            .Where(x => x.Action.GetCustomAttributes(inherit: true)
                .Concat(x.Controller.GetCustomAttributes(inherit: true))
                .Any(a => a is RequireScopeAttribute or RequireDeclaredWriteScopeAttribute))
            .ToList();

        actions.Should().Contain(x => x.Controller == typeof(PebbleController),
            "the scan must reach the one Nightscout action outside a versioned path");

        var unrecognised = actions
            .Where(x => x.Action.GetCustomAttributes(typeof(NightscoutEndpointAttribute), inherit: true).Length == 0
                        && ControllerActionReflection.GetRoutes(x.Controller, x.Action)
                            .Any(route => NightscoutApiPath.Version(route) is null))
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToList();

        unrecognised.Should().BeEmpty(
            "a Nightscout action off the /api/v1-3 paths must carry [NightscoutEndpoint]: "
            + string.Join("; ", unrecognised));
    }

    private static IActionResult? Refuse(RequireScopeAttribute attribute, string path, params string[] grantedScopes) =>
        Refuse(attribute, path, authenticated: true, grantedScopes);

    private static IActionResult? Refuse(
        RequireScopeAttribute attribute, string path, bool authenticated, params string[] grantedScopes)
    {
        var context = new AuthorizationFilterContext(
            new ActionContext(Context(path, authenticated, grantedScopes), new RouteData(), new ActionDescriptor()),
            []);
        attribute.OnAuthorization(context);
        return context.Result;
    }

    private static DefaultHttpContext Context(string path, bool authenticated, params string[] grantedScopes)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddMvc().Services.BuildServiceProvider(),
        };
        httpContext.Request.Path = path;
        if (authenticated)
            httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        return httpContext;
    }

    private static async Task<(int Status, string? ContentType, JsonElement Problem)> Execute(IActionResult result, string path)
    {
        var httpContext = Context(path, authenticated: true);
        httpContext.Response.Body = new MemoryStream();

        await result.ExecuteResultAsync(new ActionContext(httpContext, new RouteData(), new ActionDescriptor()));

        httpContext.Response.Body.Position = 0;
        var problem = await JsonSerializer.DeserializeAsync<JsonElement>(httpContext.Response.Body);
        return (httpContext.Response.StatusCode, httpContext.Response.ContentType, problem);
    }

    private static string Without(JsonElement problem, string member) =>
        JsonSerializer.Serialize(problem.EnumerateObject()
            .Where(p => p.Name != member)
            .ToDictionary(p => p.Name, p => p.Value));

    private sealed class GlucoseWrites : ControllerBase, IWriteScopedController
    {
        public string WriteScope => Scope.GlucoseReadWrite;
    }
}
