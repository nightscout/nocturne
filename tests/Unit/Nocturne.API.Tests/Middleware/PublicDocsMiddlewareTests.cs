using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Nocturne.API.Middleware;
using Nocturne.API.Tests.Services.Docs;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// The documentation branch answers before tenant resolution and authentication, so it is the
/// only thing standing between an anonymous request and the reference.
/// </summary>
public class PublicDocsMiddlewareTests : IDisposable
{
    private readonly DocsTenantFixture _fixture = new();

    [Theory]
    [InlineData("/scalar")]
    [InlineData("/openapi/nocturne.json")]
    public async Task InvokeAsync_ServesTheEndpoint_ForATenantThatOptedIn(string path)
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false);

        var (context, served) = await InvokeAsync(path, "rhys.nocturne.run");

        served.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>
    /// Gating only the reference would leave the specs it renders from public, which is most of
    /// the same exposure — so both hang off the flag.
    /// </summary>
    [Theory]
    [InlineData("/scalar")]
    [InlineData("/openapi/nocturne.json")]
    public async Task InvokeAsync_Answers404_ForATenantThatHasNotOptedIn(string path)
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false, allowPublicDocs: false);

        var (context, served) = await InvokeAsync(path, "rhys.nocturne.run");

        served.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// A fresh install has no tenants, and its apex answers 503 setup_required — so the reference
    /// has to be served without one.
    /// </summary>
    [Theory]
    [InlineData("/scalar")]
    [InlineData("/openapi/nocturne.json")]
    public async Task InvokeAsync_ServesTheEndpoint_OnABareInstance(string path)
    {
        var (context, served) = await InvokeAsync(path, DocsTenantFixture.BaseDomain);

        served.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_PassesNonDocumentationPathsDownThePipeline()
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false, allowPublicDocs: false);

        var context = DocsTenantFixture.BuildContext("rhys.nocturne.run", path: "/api/v4/status");
        SetEndpoint(context, _ => Task.CompletedTask);

        var continued = false;
        await new PublicDocsMiddleware(_ => { continued = true; return Task.CompletedTask; })
            .InvokeAsync(context, _fixture.BuildProvider());

        continued.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_PassesADocumentationPathWithNoEndpointDownThePipeline()
    {
        _fixture.SeedTenant("rhys", isDemo: false, withDemoMember: false, allowPublicDocs: false);

        var context = DocsTenantFixture.BuildContext("rhys.nocturne.run", path: "/scalar");

        var continued = false;
        await new PublicDocsMiddleware(_ => { continued = true; return Task.CompletedTask; })
            .InvokeAsync(context, _fixture.BuildProvider());

        continued.Should().BeTrue();
    }

    private async Task<(HttpContext Context, bool Served)> InvokeAsync(string path, string host)
    {
        var context = DocsTenantFixture.BuildContext(host, path: path);

        var served = false;
        SetEndpoint(context, _ => { served = true; return Task.CompletedTask; });

        await new PublicDocsMiddleware(_ => Task.FromException(new InvalidOperationException(
                "a documentation path with an endpoint is answered by the branch, not passed on")))
            .InvokeAsync(context, _fixture.BuildProvider());

        return (context, served);
    }

    private static void SetEndpoint(HttpContext context, RequestDelegate handler) =>
        context.SetEndpoint(new Endpoint(handler, EndpointMetadataCollection.Empty, "docs"));

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }
}
