using System.Collections.Generic;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Middleware;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// Verifies <see cref="SiteSecurityMiddleware"/> route gating under site lockdown
/// (<c>Security:RequireAuthentication=true</c>).
/// </summary>
public sealed class SiteSecurityMiddlewareTests
{
    private static IConfiguration Lockdown() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:RequireAuthentication"] = "true",
        })
        .Build();

    private static SiteSecurityMiddleware Build(RequestDelegate next) => new(
        next, NullLogger<SiteSecurityMiddleware>.Instance, Lockdown());

    [Fact]
    public async Task Tls_authorize_stays_public_under_lockdown()
    {
        // Caddy's on-demand "ask" call is unauthenticated and internal; if lockdown
        // blocked it, no tenant-subdomain certificate could ever be issued.
        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/v4/platform/tls-authorize";

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("/api/v4/platform/tls-authorizeanything")]
    [InlineData("/api/v4/platform/tls-authorize-evil")]
    [InlineData("/api/v4/platform/tls-authorize/../entries")]
    public async Task Paths_that_only_share_the_tls_authorize_prefix_are_not_allowlisted(string path)
    {
        // The allowlist entry matches the exact route; a StartsWith would let any path with
        // this prefix through lockdown.
        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Path = path;

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Protected_route_is_denied_under_lockdown_when_unauthenticated()
    {
        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Path = "/api/v4/entries";

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }
}
