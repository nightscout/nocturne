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

    /// <summary>
    /// Member names every relying party looks for, spelled as OpenID Connect Discovery 1.0 and
    /// RFC 8414 spell them. Both documents carry all of these.
    /// </summary>
    private static readonly string[] SpecifiedMemberNames =
    [
        "issuer",
        "authorization_endpoint",
        "token_endpoint",
        "jwks_uri",
        "scopes_supported",
        "response_types_supported",
        "grant_types_supported",
        "token_endpoint_auth_methods_supported",
        "code_challenge_methods_supported",
        "service_documentation",
    ];

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
        using var discovery = await GetDiscoveryDocumentAsync(discoveryPath);

        discovery.RootElement.TryGetProperty("jwks_uri", out var advertised).Should().BeTrue();
        var jwksUri = advertised.GetString()!;
        jwksUri.Should().EndWith("/.well-known/jwks.json");

        var response = await _client.GetAsync(new Uri(jwksUri).PathAndQuery);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var keySet = await response.Content.ReadFromJsonAsync<JsonWebKeySet>(CaseInsensitive);
        keySet!.Keys.Should().ContainSingle().Which.Alg.Should().Be("HS256");
    }

    [Theory]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/.well-known/oauth-authorization-server")]
    public async Task Discovery_document_spells_its_members_the_way_the_specs_do(
        string discoveryPath
    )
    {
        using var discovery = await GetDiscoveryDocumentAsync(discoveryPath);

        var memberNames = discovery
            .RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        memberNames.Should().Contain(SpecifiedMemberNames);
        memberNames
            .Should()
            .OnlyContain(
                name => name == name.ToLowerInvariant(),
                "discovery members are lower snake_case, so no camelCase name is recognisable"
            );
    }

    private async Task<JsonDocument> GetDiscoveryDocumentAsync(string discoveryPath)
    {
        var response = await _client.GetAsync(discoveryPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
