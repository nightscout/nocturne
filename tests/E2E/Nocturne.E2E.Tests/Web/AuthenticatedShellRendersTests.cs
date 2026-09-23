using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Nocturne.E2E.Tests.Fixtures;
using Xunit;

namespace Nocturne.E2E.Tests.Web;

/// <summary>
/// Renders the signed-in shell through the real web server.
/// </summary>
/// <remarks>
/// Every other suite exercises the app below the rendering layer: the unit tests
/// call components in isolation, the API tests never render a page, and
/// <c>pnpm build</c> compiles the app without rendering one. A build that
/// succeeds can still throw on the first server render — the wuchale rollout did
/// exactly that, and the only page anyone had rendered by hand was the login
/// page, which sits outside the authenticated layout and so carries no sidebar.
/// This asks the running server for a page behind the session and refuses a body
/// that is SvelteKit's error shell instead of the app.
/// </remarks>
[Collection("e2e")]
[Trait("Category", "E2E")]
public class AuthenticatedShellRendersTests
{
    private readonly AppHostFixture _fixture;
    private readonly DevSeedClient _seed;

    public AuthenticatedShellRendersTests(AppHostFixture fixture)
    {
        _fixture = fixture;
        _seed = new DevSeedClient(fixture);
    }

    [Fact]
    public async Task Dashboard_RendersTheShell_ForASignedInSession()
    {
        var ctx = await _seed.SeedTenantAsync();

        // Addressed by the tenant's own name, with the connection sent to the web
        // server's port. The name is what the app resolves a tenant from, and the
        // port is where it listens: the app host derives one from the gateway and
        // publishes the other on the resource.
        var web = new Uri(_fixture.WebBaseUrl);
        using var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(IPAddress.Loopback, web.Port, token);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        using var browser = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://{ctx.Slug}.{_fixture.TenantHostSuffix}"),
        };

        // The dev login GET is the browser-session entry point.
        var login = await browser.GetAsync(
            $"/api/v4/dev-only/auth/login?tenant={ctx.Slug}&username={ctx.Username}");
        ((int)login.StatusCode).Should().BeInRange(200, 399,
            "the dev login should issue a session rather than refuse it");

        // Carried by hand: the session cookies are Secure, and the web server is
        // published over plain HTTP here, so a cookie jar would hold them back.
        // A browser reaching the same server through the TLS gateway sends them.
        var session = string.Join("; ", login.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';')[0]));
        session.Should().NotBeEmpty("the dev login should set session cookies");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", session);
        var page = await browser.SendAsync(request);
        var body = await page.Content.ReadAsStringAsync();

        page.StatusCode.Should().Be(HttpStatusCode.OK,
            "a signed-in dashboard request must render, not redirect or fall through "
            + "to the error shell");

        // The shell, not the error page. Both assertions earn their place: a
        // server-side render failure answers 500 with a body that still parses
        // as HTML, so only the sidebar marker proves the app rendered.
        body.Should().NotContain("An error occurred while processing your request",
            "that body is SvelteKit's error shell, which means the render threw");
        body.Should().Contain("data-slot=\"sidebar-wrapper\"",
            "the authenticated layout renders the sidebar provider's wrapper");
    }
}
