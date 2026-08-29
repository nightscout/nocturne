using System.Collections.Generic;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Extensions;
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

    /// <summary>
    /// A request as the pipeline presents it to this middleware: routed (UseRouting has run), and
    /// optionally resolved through a share token.
    /// </summary>
    private static DefaultHttpContext Request(
        string path, bool shareAccess = false, bool allowAnonymous = false, bool routed = true)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Path = path;

        if (routed)
        {
            var metadata = allowAnonymous
                ? new EndpointMetadataCollection(new AllowAnonymousAttribute())
                : EndpointMetadataCollection.Empty;
            ctx.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, path));
        }

        if (shareAccess)
        {
            ctx.SetShareAccess();
        }

        return ctx;
    }

    [Fact]
    public async Task Share_host_reads_the_status_document_under_lockdown()
    {
        // The link is dead without this: the web layer learns a tenant grants anonymous read from
        // /api/v4/status alone, and it is [AllowAnonymous], so lockdown was the only thing 401ing it.
        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = Request("/api/v4/status", shareAccess: true, allowAnonymous: true);

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("/api/v4/entries")]
    [InlineData("/api/v4/me/permissions")]
    public async Task Share_host_reads_the_shared_data_under_lockdown(string path)
    {
        // These carry no [AllowAnonymous], so the default-deny fallback policy re-derives the
        // share's PublicShareScopes trie for each of them -- lockdown adds nothing this gate needs
        // to keep, and keeping it revoked a grant the tenant owner published deliberately.
        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = Request(path, shareAccess: true);

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("/api/auth/passkey/login/options")]
    [InlineData("/api/auth/passkey/recovery-mode/complete")]
    [InlineData("/api/auth/totp/login")]
    [InlineData("/api/v4/guest-links/activate")]
    [InlineData("/api/v4/member-invites/tok/info")]
    [InlineData("/hubs/data/negotiate")]
    public async Task Share_host_stays_locked_down_on_the_anonymous_surface(string path)
    {
        // [AllowAnonymous] suppresses authorization evaluation, so nothing downstream narrows these
        // to the share's scopes and this gate is their only gate. A share link is a public URL: if
        // holding one exempted its bearer here, the operator's lockdown would leave the passkey
        // ceremony and the token oracles open on the one host anybody can reach.
        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = Request(path, shareAccess: true, allowAnonymous: true);

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Share_host_stays_locked_down_on_a_request_that_routed_nowhere()
    {
        // No endpoint means no metadata to read, so the exemption cannot establish that anything
        // downstream would re-authorize. Fail closed rather than assume.
        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = Request("/api/v4/entries", shareAccess: true, routed: false);

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Theory]
    [InlineData("/api/v4/status", true)]
    [InlineData("/api/v4/entries", false)]
    public async Task Bare_tenant_host_is_still_denied_under_lockdown(string path, bool allowAnonymous)
    {
        // Without a resolved share token the lockdown applies to both halves of the surface.
        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = Request(path, allowAnonymous: allowAnonymous);

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }
}
