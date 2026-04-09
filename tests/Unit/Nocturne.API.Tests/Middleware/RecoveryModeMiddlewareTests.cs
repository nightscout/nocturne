using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Authorization;
using Nocturne.API.Middleware;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

public class RecoveryModeMiddlewareTests
{
    [Fact]
    public async Task MultiTenant_RecoveryEnabled_PassesThrough()
    {
        var state = new RecoveryModeState { IsEnabled = true };
        var config = Options.Create(new MultitenancyConfiguration { BaseDomain = "nocturnecgm.com" });

        var nextCalled = false;
        var mw = new RecoveryModeMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<RecoveryModeMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/status";
        ctx.Response.Body = new MemoryStream();

        await mw.InvokeAsync(ctx, state, config);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().NotBe(503);
    }

    [Fact]
    public async Task MultiTenant_EnvVarOverride_StillBlocks()
    {
        var state = new RecoveryModeState { IsEnabled = true };
        var config = Options.Create(new MultitenancyConfiguration { BaseDomain = "nocturnecgm.com" });

        Environment.SetEnvironmentVariable("NOCTURNE_RECOVERY_MODE", "true");
        try
        {
            var mw = new RecoveryModeMiddleware(
                _ => Task.CompletedTask,
                NullLogger<RecoveryModeMiddleware>.Instance);

            var ctx = new DefaultHttpContext();
            ctx.Request.Path = "/api/status";
            ctx.Response.Body = new MemoryStream();

            await mw.InvokeAsync(ctx, state, config);

            ctx.Response.StatusCode.Should().Be(503);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOCTURNE_RECOVERY_MODE", null);
        }
    }

    [Fact]
    public async Task SingleTenant_RecoveryEnabled_Blocks()
    {
        var state = new RecoveryModeState { IsEnabled = true };
        var config = Options.Create(new MultitenancyConfiguration());

        var mw = new RecoveryModeMiddleware(
            _ => Task.CompletedTask,
            NullLogger<RecoveryModeMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/status";
        ctx.Response.Body = new MemoryStream();

        await mw.InvokeAsync(ctx, state, config);

        ctx.Response.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task SingleTenant_RecoveryEnabled_AttributeEndpoint_CallsNext()
    {
        var state = new RecoveryModeState { IsEnabled = true };
        var config = Options.Create(new MultitenancyConfiguration());

        var nextCalled = false;
        var mw = new RecoveryModeMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<RecoveryModeMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/status";
        ctx.Response.Body = new MemoryStream();
        ctx.SetEndpoint(new Endpoint(
            requestDelegate: _ => Task.CompletedTask,
            metadata: new EndpointMetadataCollection(new AllowDuringSetupAttribute()),
            displayName: "test-endpoint"));

        await mw.InvokeAsync(ctx, state, config);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().NotBe(503);
    }
}
