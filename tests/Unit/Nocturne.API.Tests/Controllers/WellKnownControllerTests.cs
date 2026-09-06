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

        var memberNames = MemberNamesOf(discovery);

        memberNames.Should().Contain(SpecifiedMemberNames);
        memberNames
            .Should()
            .OnlyContain(
                name => name == name.ToLowerInvariant(),
                "discovery members are lower snake_case, so no camelCase name is recognisable"
            );
    }

    [Theory]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/.well-known/oauth-authorization-server")]
    public async Task Discovery_document_omits_the_metadata_it_does_not_advertise(
        string discoveryPath
    )
    {
        using var discovery = await GetDiscoveryDocumentAsync(discoveryPath);

        discovery
            .RootElement.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.Null)
            .Select(property => property.Name)
            .Should()
            .BeEmpty("absent discovery metadata is omitted, never advertised as null");
    }

    [Fact]
    public async Task Openid_configuration_omits_the_endpoints_the_local_provider_lacks()
    {
        using var discovery = await GetDiscoveryDocumentAsync(
            "/.well-known/openid-configuration"
        );

        var memberNames = MemberNamesOf(discovery);

        memberNames.Should().NotContain("registration_endpoint");
        memberNames.Should().NotContain("end_session_endpoint");
    }

    [Fact]
    public async Task Oauth_metadata_omits_the_unadvertised_introspection_endpoint()
    {
        using var discovery = await GetDiscoveryDocumentAsync(
            "/.well-known/oauth-authorization-server"
        );

        MemberNamesOf(discovery).Should().NotContain("introspection_endpoint");
    }

    [Fact]
    public async Task Oauth_metadata_still_advertises_the_endpoints_it_does_serve()
    {
        using var discovery = await GetDiscoveryDocumentAsync(
            "/.well-known/oauth-authorization-server"
        );

        MemberNamesOf(discovery)
            .Should()
            .Contain(
                ["device_authorization_endpoint", "revocation_endpoint", "registration_endpoint"]
            );
    }

    [Fact]
    public async Task Openid_configuration_still_advertises_its_userinfo_endpoint()
    {
        using var discovery = await GetDiscoveryDocumentAsync(
            "/.well-known/openid-configuration"
        );

        MemberNamesOf(discovery).Should().Contain("userinfo_endpoint");
    }

    [Fact]
    public void Populated_optional_metadata_survives_serialisation()
    {
        var openIdMembers = MemberNamesOf(
            new OpenIdConfiguration
            {
                UserinfoEndpoint = "https://example.test/auth/userinfo",
                RegistrationEndpoint = "https://example.test/api/oauth/register",
                EndSessionEndpoint = "https://example.test/api/oauth/logout",
                ServiceDocumentation = "https://example.test/docs",
            }
        );

        openIdMembers
            .Should()
            .Contain(
                [
                    "userinfo_endpoint",
                    "registration_endpoint",
                    "end_session_endpoint",
                    "service_documentation",
                ]
            );

        var oauthMembers = MemberNamesOf(
            new OAuthAuthorizationServerMetadata
            {
                DeviceAuthorizationEndpoint = "https://example.test/api/oauth/device",
                RevocationEndpoint = "https://example.test/api/oauth/revoke",
                IntrospectionEndpoint = "https://example.test/api/oauth/introspect",
                RegistrationEndpoint = "https://example.test/api/oauth/register",
                ServiceDocumentation = "https://example.test/docs",
            }
        );

        oauthMembers
            .Should()
            .Contain(
                [
                    "device_authorization_endpoint",
                    "revocation_endpoint",
                    "introspection_endpoint",
                    "registration_endpoint",
                    "service_documentation",
                ]
            );
    }

    private static string[] MemberNamesOf(JsonDocument document) =>
        document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

    private static string[] MemberNamesOf<T>(T model)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(model));

        return MemberNamesOf(document);
    }

    private async Task<JsonDocument> GetDiscoveryDocumentAsync(string discoveryPath)
    {
        var response = await _client.GetAsync(discoveryPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
