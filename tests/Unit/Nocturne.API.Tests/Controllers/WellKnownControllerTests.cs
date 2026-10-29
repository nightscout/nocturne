using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Controllers;
using Nocturne.API.Mcp;
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

    private readonly AuthenticationTestFactory _factory;
    private readonly HttpClient _client;

    public WellKnownControllerTests(AuthenticationTestFactory factory)
    {
        _factory = factory;
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

    [Fact]
    public async Task Protected_resource_metadata_names_this_hosts_mcp_endpoint_and_read_scopes()
    {
        using var metadata = await GetDiscoveryDocumentAsync("/.well-known/oauth-protected-resource");
        var root = metadata.RootElement;

        var resource = new Uri(root.GetProperty("resource").GetString()!);
        resource.AbsolutePath.Should().Be("/mcp");
        resource.Authority.Should().Be(_client.BaseAddress!.Authority);
        root.GetProperty("bearer_methods_supported").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("header");
        root.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(McpScopes.Offered)
            .And.OnlyContain(scope => scope!.EndsWith(".read"));
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    public async Task Protected_resource_names_the_issuer_the_authorization_server_reports(string path)
    {
        using var resource = await GetDiscoveryDocumentAsync(path);
        using var server = await GetDiscoveryDocumentAsync("/.well-known/oauth-authorization-server");

        var issuer = server.RootElement.GetProperty("issuer").GetString();
        resource.RootElement.GetProperty("authorization_servers").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal(issuer);
        new Uri(issuer!).GetLeftPart(UriPartial.Authority).Should().Be(issuer,
            "RFC 8414: the issuer is the origin the metadata was fetched from");
        new Uri(server.RootElement.GetProperty("registration_endpoint").GetString()!)
            .GetLeftPart(UriPartial.Authority).Should().Be(issuer,
                "a client registers with the host it will be authorized by");
    }

    [Fact]
    public async Task Mcp_switched_off_serves_neither_the_endpoint_nor_its_metadata()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services.Configure<McpOptions>(options => options.Enabled = false)));
        using var client = factory.CreateClient();

        (await client.GetAsync("/.well-known/oauth-protected-resource"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<McpEndpointMetadata>() is not null)
            .Should().BeEmpty();
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Should().Contain(e => e.Metadata.GetMetadata<McpEndpointMetadata>() != null,
                "the probe above would pass if the marker were never attached");
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
