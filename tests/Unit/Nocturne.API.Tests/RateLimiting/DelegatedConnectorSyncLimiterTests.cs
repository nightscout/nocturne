using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.RateLimiting;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.RateLimiting;

/// <summary>
/// A delegated credential may start <see cref="DelegatedConnectorSyncLimiter.PermitLimit"/> syncs an
/// hour for each tenant and connector; the person's own sign-in is not limited. The window is an
/// hour, so every case here runs inside one window and none needs time to pass.
/// </summary>
public class DelegatedConnectorSyncLimiterTests : IDisposable
{
    private readonly DelegatedConnectorSyncLimiter _limiter = new();

    public void Dispose()
    {
        _limiter.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(nameof(ServicesController.TriggerConnectorSync))]
    [InlineData(nameof(ServicesController.ResetConnectorCursor))]
    public void BothSyncActions_CarryTheLimit(string action)
    {
        typeof(ServicesController).GetMethod(action)!
            .GetCustomAttribute<LimitDelegatedConnectorSyncAttribute>()
            .Should().NotBeNull();
    }

    [Fact]
    public async Task TheSeventhDelegatedSyncInAnHour_IsRefusedWith429()
    {
        var tenant = Guid.CreateVersion7();

        for (var i = 0; i < DelegatedConnectorSyncLimiter.PermitLimit; i++)
            (await RunAsync(AuthType.OAuthAccessToken, tenant, "dexcom")).Should().BeNull($"sync {i + 1} is allowed");

        var refused = await RunAsync(AuthType.OAuthAccessToken, tenant, "Dexcom");

        refused.Should().NotBeNull("the allowance is keyed on the registered connector id, not the spelling");
        var http = await ExecuteAsync(refused!);
        int.Parse(http.Response.Headers.RetryAfter.ToString()).Should().BeInRange(1, 3600);
        await http.Response.ShouldBeTheRateLimitProblemAsync();
    }

    [Fact]
    public async Task AnInteractiveSession_IsNotLimited()
    {
        var tenant = Guid.CreateVersion7();

        for (var i = 0; i < DelegatedConnectorSyncLimiter.PermitLimit * 2; i++)
            (await RunAsync(AuthType.SessionCookie, tenant, "dexcom")).Should().BeNull();
    }

    [Fact]
    public async Task AnotherConnectorOrTenant_HasItsOwnAllowance()
    {
        var tenant = Guid.CreateVersion7();
        for (var i = 0; i < DelegatedConnectorSyncLimiter.PermitLimit; i++)
            await RunAsync(AuthType.DirectGrant, tenant, "dexcom");

        (await RunAsync(AuthType.DirectGrant, tenant, "dexcom")).Should().NotBeNull();
        (await RunAsync(AuthType.DirectGrant, tenant, "glooko")).Should().BeNull();
        (await RunAsync(AuthType.DirectGrant, Guid.CreateVersion7(), "dexcom")).Should().BeNull();
    }

    [Theory]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.SessionCookie)]
    public async Task AnUnknownConnector_Is404(AuthType authType)
    {
        var result = await RunAsync(authType, Guid.CreateVersion7(), "notaconnector");

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task UnknownConnectors_TakeNoLease()
    {
        var tenant = Guid.CreateVersion7();

        for (var i = 0; i < 50; i++)
            (await RunAsync(AuthType.OAuthAccessToken, tenant, $"random-{i}"))
                .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        for (var i = 0; i < 50; i++)
            (await RunAsync(AuthType.OAuthAccessToken, tenant, "gloo\u212Ao"))
                .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);

        for (var i = 0; i < DelegatedConnectorSyncLimiter.PermitLimit; i++)
        {
            (await RunAsync(AuthType.OAuthAccessToken, tenant, "dexcom"))
                .Should().BeNull("an unknown id spends nothing from a real connector's allowance");
            (await RunAsync(AuthType.OAuthAccessToken, tenant, "glooko"))
                .Should().BeNull("a spelling no executor answers to is not that connector");
        }
    }

    /// <returns>The filter's short-circuit result, or null when it let the action run.</returns>
    private async Task<IActionResult?> RunAsync(AuthType authType, Guid tenantId, string connectorId)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(a => a.TenantId).Returns(tenantId);

        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(_limiter)
                .AddSingleton(tenantAccessor.Object)
                .AddSingleton(Executor("dexcom"))
                .AddSingleton(Executor("glooko"))
                .BuildServiceProvider(),
        };
        http.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = authType,
            SubjectId = Guid.CreateVersion7(),
        };

        var routeData = new RouteData();
        routeData.Values["id"] = connectorId;
        var context = new ActionExecutingContext(
            new ActionContext(http, routeData, new ActionDescriptor()),
            [],
            new Dictionary<string, object?>(),
            controller: new SyncController { ProblemDetailsFactory = new EchoingProblemDetailsFactory() });

        var ran = false;
        await new LimitDelegatedConnectorSyncAttribute().OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller: new object()));
        });

        (context.Result is null).Should().Be(ran);
        return context.Result;
    }

    private static IConnectorSyncExecutor Executor(string connectorId) =>
        Mock.Of<IConnectorSyncExecutor>(e => e.ConnectorId == connectorId);

    private sealed class SyncController : ControllerBase;

    private static async Task<HttpContext> ExecuteAsync(IActionResult result)
    {
        var http = new DefaultHttpContext { RequestServices = TestRequestServices.Build(clamped: false) };
        http.Response.Body = new MemoryStream();
        await result.ExecuteResultAsync(new ActionContext(http, new RouteData(), new ActionDescriptor()));
        return http;
    }
}
