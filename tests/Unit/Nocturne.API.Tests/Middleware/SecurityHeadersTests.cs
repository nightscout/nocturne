using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nocturne.API.Extensions;

namespace Nocturne.API.Tests.Middleware;

public class SecurityHeadersTests
{
    [Fact]
    public async Task AnOrdinaryResponse_CarriesTheHeaders()
    {
        var response = await SendAsync("/ok");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertHeaders(response, "strict-origin-when-cross-origin");
    }

    [Fact]
    public async Task AnErrorTheExceptionHandlerRewrites_StillCarriesTheHeaders()
    {
        var response = await SendAsync("/throws");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        AssertHeaders(response, "strict-origin-when-cross-origin");
    }

    [Fact]
    public async Task AReferrerPolicyTheEndpointChose_IsKept()
    {
        var response = await SendAsync("/no-referrer");

        AssertHeaders(response, "no-referrer");
    }

    private static void AssertHeaders(HttpResponseMessage response, string referrerPolicy)
    {
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Equal(referrerPolicy);
    }

    private static async Task<HttpResponseMessage> SendAsync(string path)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddProblemDetails())
                .Configure(app =>
                {
                    app.UseSecurityHeaders();
                    app.UseExceptionHandler();
                    app.Run(context => context.Request.Path.Value switch
                    {
                        "/throws" => throw new InvalidOperationException("boom"),
                        "/no-referrer" => WriteWithReferrerPolicy(context),
                        _ => context.Response.WriteAsync("ok"),
                    });
                }))
            .StartAsync();

        return await host.GetTestClient().GetAsync(path);
    }

    private static Task WriteWithReferrerPolicy(HttpContext context)
    {
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        return context.Response.WriteAsync("ok");
    }
}
