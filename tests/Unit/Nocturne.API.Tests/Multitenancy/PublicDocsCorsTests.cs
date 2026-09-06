using FluentAssertions;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// Covers the CORS split in the request pipeline: the documentation paths (<c>/openapi</c>,
/// <c>/scalar</c>) are served to any origin, while everything else stays on the credentialed
/// base-domain policy. Docs sites are hosted off the base domain (getnocturne.dev embeds the
/// Scalar reference), so without the any-origin policy the browser blocks the spec fetch and
/// the reference renders empty. Which origins the credentialed default policy itself admits
/// is covered by <see cref="CorsOriginPolicyTests"/>.
/// </summary>
public sealed class PublicDocsCorsTests : IClassFixture<AuthenticationTestFactory>
{
    private const string ForeignOrigin = "https://getnocturne.dev";

    private readonly AuthenticationTestFactory _factory;

    public PublicDocsCorsTests(AuthenticationTestFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    [Theory]
    [InlineData("/openapi/nocturne.json")]      // spec fetched by the embedded reference
    [InlineData("/scalar/mermaid-loader.js")]   // wwwroot asset, loaded as a crossorigin module
    public async Task Documentation_paths_are_readable_from_any_origin(string path)
    {
        var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", ForeignOrigin);

        var response = await client.SendAsync(request, CancellationToken.None);

        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle()
            .Which.Should().Be("*");
        // AllowAnyOrigin and credentials are mutually exclusive per the CORS spec; the
        // any-origin policy must never grant credentials.
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    [Theory]
    [InlineData("/api/v1/status")]
    [InlineData("/api/v1/entries")]
    public async Task Api_paths_never_get_the_any_origin_policy(string path)
    {
        var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", ForeignOrigin);

        var response = await client.SendAsync(request, CancellationToken.None);

        // Tenant data stays on the credentialed base-domain policy. A wildcard here would
        // hand every origin read access to the API, so the any-origin policy must not leak
        // past the documentation paths.
        response.Headers.Contains("Access-Control-Allow-Origin")
            .Should().BeFalse(
                "the any-origin documentation policy must not apply to {0}", path);
    }
}
