using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Controllers;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Covers the OIDC discovery documents and the key set they point relying parties at.
/// </summary>
public sealed class WellKnownControllerTests : IClassFixture<AuthenticationTestFactory>
{
    private static readonly JsonSerializerOptions CaseInsensitive =
        new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _client;

    public WellKnownControllerTests(AuthenticationTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/.well-known/oauth-authorization-server")]
    public async Task Advertised_jwks_uri_serves_the_key_set(string discoveryPath)
    {
        var jwksUri = await GetAdvertisedJwksUriAsync(discoveryPath);
        jwksUri.Should().EndWith("/.well-known/jwks.json");

        var response = await _client.GetAsync(new Uri(jwksUri).PathAndQuery);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var keySet = await response.Content.ReadFromJsonAsync<JsonWebKeySet>(CaseInsensitive);
        keySet!.Keys.Should().ContainSingle().Which.Alg.Should().Be("HS256");
    }

    private async Task<string> GetAdvertisedJwksUriAsync(string discoveryPath)
    {
        var response = await _client.GetAsync(discoveryPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document
            .RootElement.EnumerateObject()
            .Single(property =>
                property.Name.Replace("_", string.Empty)
                    .Equals("jwksuri", StringComparison.OrdinalIgnoreCase)
            )
            .Value.GetString()!;
    }
}
