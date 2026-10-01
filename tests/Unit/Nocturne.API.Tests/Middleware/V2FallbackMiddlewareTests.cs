using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Middleware;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// Which <c>/api/v2</c> paths are served as v1, decided against a known set of v2 routes. The
/// pipeline-level behaviour (ordering after the <c>.json</c> strip, the real endpoints) is pinned in
/// <see cref="ApiErrorEnvelopeTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public class V2FallbackMiddlewareTests
{
    private static readonly string[] V2Routes =
    [
        "api/v2/properties/{*path}",
        "api/v2/ddata",
        "api/v2/authorization/subjects/{id}",
    ];

    private static async Task<(PathString Path, bool NextCalled)> Run(string path)
    {
        var nextCalled = false;
        var endpoints = new DefaultEndpointDataSource(V2Routes.Select(route =>
            (Endpoint)new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse(route),
                order: 0,
                EndpointMetadataCollection.Empty,
                route)));

        var middleware = new V2FallbackMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            endpoints,
            NullLogger<V2FallbackMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        return (context.Request.Path, nextCalled);
    }

    [Theory]
    [InlineData("/api/v2/entries", "/api/v1/entries")]
    [InlineData("/api/v2/entries/current", "/api/v1/entries/current")]
    [InlineData("/api/v2/treatments", "/api/v1/treatments")]
    [InlineData("/API/V2/entries", "/api/v1/entries")]
    public async Task APathV2DoesNotAnswer_IsServedAsV1(string path, string expected)
    {
        var (rewritten, nextCalled) = await Run(path);

        rewritten.Value.Should().Be(expected);
        nextCalled.Should().BeTrue();
    }

    /// <summary>
    /// A v2 route Nocturne implements keeps the request, whatever form of its template it matches:
    /// a catch-all, a literal and a parameter.
    /// </summary>
    [Theory]
    [InlineData("/api/v2/properties")]
    [InlineData("/api/v2/properties/bgnow,iob,delta")]
    [InlineData("/api/v2/ddata")]
    [InlineData("/api/v2/authorization/subjects/abc123")]
    [InlineData("/API/V2/DDATA")]
    public async Task APathAV2RouteMatches_StaysWithV2(string path)
    {
        var (rewritten, nextCalled) = await Run(path);

        rewritten.Value.Should().Be(path);
        nextCalled.Should().BeTrue();
    }

    [Theory]
    [InlineData("/api/v1/entries")]
    [InlineData("/api/v3/entries")]
    [InlineData("/api/v4/entries")]
    [InlineData("/api/v2entries")]
    [InlineData("/api/v2")]
    [InlineData("/api/v2/")]
    [InlineData("/")]
    public async Task APathOutsideV2Resources_IsLeftAlone(string path)
    {
        var (rewritten, nextCalled) = await Run(path);

        rewritten.Value.Should().Be(path);
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task TheQueryString_IsCarriedOverUnchanged()
    {
        var nextCalled = false;
        var middleware = new V2FallbackMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new DefaultEndpointDataSource(),
            NullLogger<V2FallbackMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v2/entries";
        context.Request.QueryString = new QueryString("?count=200&find[date][$gte]=1790000000");

        await middleware.InvokeAsync(context);

        context.Request.Path.Value.Should().Be("/api/v1/entries");
        context.Request.QueryString.Value.Should().Be("?count=200&find[date][$gte]=1790000000");
        nextCalled.Should().BeTrue();
    }
}
