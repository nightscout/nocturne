using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Middleware;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

public class HttpsRequirementMiddlewareTests
{
    private readonly IConfiguration _defaultConfig;
    private readonly IConfiguration _allowHttpConfig;

    public HttpsRequirementMiddlewareTests()
    {
        _defaultConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        _allowHttpConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AllowHttp"] = "true"
            })
            .Build();
    }

    private static HttpsRequirementMiddleware CreateMiddleware(
        RequestDelegate next, IConfiguration config)
    {
        return new HttpsRequirementMiddleware(
            next,
            NullLogger<HttpsRequirementMiddleware>.Instance,
            config);
    }

    private static DefaultHttpContext CreateHttpContext(
        string method = "GET",
        string scheme = "http",
        string host = "localhost",
        string path = "/api/v1/entries",
        string? forwardedProto = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Scheme = scheme;
        ctx.Request.Host = new HostString(host);
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        if (forwardedProto != null)
            ctx.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        return ctx;
    }

    [Fact]
    public async Task HttpsRequest_PassesThrough()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; }, _defaultConfig);
        var ctx = CreateHttpContext(scheme: "https");

        await middleware.InvokeAsync(ctx);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task HttpRequest_WithForwardedProtoHttps_PassesThrough()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; }, _defaultConfig);
        var ctx = CreateHttpContext(scheme: "http", forwardedProto: "https");

        await middleware.InvokeAsync(ctx);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task HttpGetRequest_Returns301Redirect()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; }, _defaultConfig);
        var ctx = CreateHttpContext(method: "GET", host: "example.com", path: "/setup");

        await middleware.InvokeAsync(ctx);

        called.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(301);
        ctx.Response.Headers.Location.ToString().Should().Be("https://example.com/setup");
    }

    [Fact]
    public async Task HttpHeadRequest_Returns301Redirect()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; }, _defaultConfig);
        var ctx = CreateHttpContext(method: "HEAD", host: "example.com", path: "/api/v1/status");

        await middleware.InvokeAsync(ctx);

        called.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(301);
    }

    [Fact]
    public async Task HttpPostRequest_Returns400()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; }, _defaultConfig);
        var ctx = CreateHttpContext(method: "POST", host: "example.com", path: "/api/v4/setup/tenant");

        await middleware.InvokeAsync(ctx);

        called.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task HealthEndpoint_BypassesCheck()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; }, _defaultConfig);
        var ctx = CreateHttpContext(path: "/health");

        await middleware.InvokeAsync(ctx);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task AliveEndpoint_BypassesCheck()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; }, _defaultConfig);
        var ctx = CreateHttpContext(path: "/alive");

        await middleware.InvokeAsync(ctx);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task AllowHttpConfig_BypassesCheck()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; }, _allowHttpConfig);
        var ctx = CreateHttpContext();

        await middleware.InvokeAsync(ctx);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task HttpGetWithQueryString_PreservesQueryInRedirect()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask, _defaultConfig);
        var ctx = CreateHttpContext(method: "GET", host: "example.com", path: "/api/v1/entries");
        ctx.Request.QueryString = new QueryString("?count=10&token=abc");

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(301);
        ctx.Response.Headers.Location.ToString().Should().Be("https://example.com/api/v1/entries?count=10&token=abc");
    }
}
