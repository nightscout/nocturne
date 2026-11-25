using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.RateLimiting;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.RateLimiting;

/// <summary>
/// One tenant's guest code activations share <see cref="GuestActivationLimiter.PermitLimit"/>
/// attempts per window. The window is ten minutes, so every case here runs inside one window and
/// none needs time to pass.
/// </summary>
public class GuestActivationLimiterTests : IDisposable
{
    private readonly GuestActivationLimiter _limiter = new();

    public void Dispose()
    {
        _limiter.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheActivationAction_CarriesTheLimit()
    {
        typeof(GuestLinkController).GetMethod(nameof(GuestLinkController.ActivateGuestLink))!
            .GetCustomAttribute<LimitGuestActivationsPerTenantAttribute>()
            .Should().NotBeNull();
    }

    [Fact]
    public async Task TheAttemptPastTheTenantsAllowance_IsRefusedWith429()
    {
        var tenant = Guid.CreateVersion7();

        for (var i = 0; i < GuestActivationLimiter.PermitLimit; i++)
            (await RunAsync(tenant)).Should().BeNull($"attempt {i + 1} is allowed");

        var refused = await RunAsync(tenant);

        refused.Should().NotBeNull();
        var http = new DefaultHttpContext { RequestServices = TestRequestServices.Build(clamped: false) };
        http.Response.Body = new MemoryStream();
        await refused!.ExecuteResultAsync(new ActionContext(http, new RouteData(), new ActionDescriptor()));
        int.Parse(http.Response.Headers.RetryAfter.ToString())
            .Should().BeInRange(1, (int)GuestActivationLimiter.Window.TotalSeconds);
        await http.Response.ShouldBeTheRateLimitProblemAsync();
    }

    [Fact]
    public async Task AnotherTenant_HasItsOwnAllowance()
    {
        var tenant = Guid.CreateVersion7();
        for (var i = 0; i < GuestActivationLimiter.PermitLimit; i++)
            await RunAsync(tenant);

        (await RunAsync(tenant)).Should().NotBeNull();
        (await RunAsync(Guid.CreateVersion7())).Should().BeNull();
    }

    /// <returns>The filter's short-circuit result, or null when it let the action run.</returns>
    private async Task<IActionResult?> RunAsync(Guid tenantId)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(_limiter)
                .AddSingleton(Mock.Of<ITenantAccessor>(a => a.TenantId == tenantId))
                .BuildServiceProvider(),
        };
        var context = new ActionExecutingContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            [],
            new Dictionary<string, object?>(),
            controller: new object());

        var ran = false;
        await new LimitGuestActivationsPerTenantAttribute().OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller: new object()));
        });

        (context.Result is null).Should().Be(ran);
        return context.Result;
    }
}
