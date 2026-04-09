using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nocturne.API.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// End-to-end smoke test for the <see cref="AllowDuringSetupAttribute"/>
/// metadata flow. Spins up a minimal TestServer with UseRouting and asserts
/// that decorated endpoints surface the attribute through
/// <see cref="EndpointMetadataCollection"/>. Serves as the canonical example
/// of how <see cref="Nocturne.API.Middleware.TenantSetupMiddleware"/> and
/// <see cref="Nocturne.API.Middleware.RecoveryModeMiddleware"/> read endpoint
/// metadata.
/// </summary>
public class TenantSetupMiddlewareIntegrationTests
{
    private static IHost BuildHost(bool decorated)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddRouting());
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        var builder = endpoints.MapGet("/probe", () => "ok");
                        if (decorated)
                        {
                            builder.WithMetadata(new AllowDuringSetupAttribute());
                        }
                    });
                });
            })
            .Start();
    }

    [Fact]
    public async Task DecoratedEndpoint_ExposesAllowDuringSetupMetadata()
    {
        using var host = BuildHost(decorated: true);
        var server = host.GetTestServer();

        var context = await server.SendAsync(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/probe";
        });

        var endpoint = context.GetEndpoint();
        endpoint.Should().NotBeNull();
        endpoint!.Metadata.GetMetadata<AllowDuringSetupAttribute>().Should().NotBeNull();
    }

    [Fact]
    public async Task UndecoratedEndpoint_HasNoAllowDuringSetupMetadata()
    {
        using var host = BuildHost(decorated: false);
        var server = host.GetTestServer();

        var context = await server.SendAsync(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/probe";
        });

        var endpoint = context.GetEndpoint();
        endpoint.Should().NotBeNull();
        endpoint!.Metadata.GetMetadata<AllowDuringSetupAttribute>().Should().BeNull();
    }
}
