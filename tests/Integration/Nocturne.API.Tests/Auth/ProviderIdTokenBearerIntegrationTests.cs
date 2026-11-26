using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// An identity provider's ID token is not an API credential. Sent as <c>Authorization: Bearer</c> it
/// is answered as no credential is, whoever its identity is linked to, and it creates no subject.
/// </summary>
/// <remarks>
/// A provider on the loopback publishes the discovery document and the signing key, and each token is
/// checked against them before it is sent, so the refusal is of a token the provider vouches for.
/// </remarks>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class ProviderIdTokenBearerIntegrationTests : ApiIntegrationTestBase
{
    private const string KeyId = "provider-id-token-key";

    private readonly RSA _rsa = RSA.Create(2048);
    private WebApplication _idp = null!;
    private string _issuer = null!;
    private string _clientId = null!;
    private Guid _providerId;
    private Guid _tenantId;
    private Guid _admin;

    public ProviderIdTokenBearerIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await StartIdpAsync($"/{Guid.NewGuid():N}");

        _providerId = Guid.CreateVersion7();
        _clientId = $"client-{_providerId:N}";
        await using var conn = await OpenPostgresAsync();
        await conn.ExecuteAsync("""
            INSERT INTO oidc_providers (id, name, issuer_url, client_id, is_enabled, display_order, scopes, claim_mappings, default_roles, created_at, updated_at)
            VALUES (@provider, 'ID token IdP', @issuer, @client, true, 0, '["openid"]'::jsonb, '{}'::jsonb, '[]'::jsonb, now(), now());
            """,
            ("provider", _providerId), ("issuer", _issuer), ("client", _clientId));

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_admin, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "id-token-admin");
        await conn.ExecuteAsync("""
            UPDATE subjects SET is_platform_admin = true WHERE id = @subject;
            INSERT INTO subject_oidc_identities (id, subject_id, provider_id, oidc_subject_id, issuer, linked_at)
            VALUES (gen_random_uuid(), @subject, @provider, 'id-token-admin', @issuer, now());
            """,
            ("subject", _admin), ("provider", _providerId), ("issuer", _issuer));
    }

    public override async Task DisposeAsync()
    {
        await using (var conn = await OpenPostgresAsync())
        {
            await conn.ExecuteAsync("""
                UPDATE subjects SET is_platform_admin = false WHERE id = @admin;
                DELETE FROM subject_oidc_identities WHERE provider_id = @provider;
                DELETE FROM oidc_providers WHERE id = @provider;
                """,
                ("admin", _admin), ("provider", _providerId));
        }

        await _idp.DisposeAsync();
        _rsa.Dispose();
        await base.DisposeAsync();
    }

    /// <param name="withOAuthClaims">
    /// A provider token carrying <c>scope</c> and <c>client_id</c> reaches the OAuth access token
    /// handler; one without them reaches the session bearer handler.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_provider_id_token_sent_as_a_bearer_is_no_credential(bool withOAuthClaims)
    {
        var routes = new[]
        {
            "/api/v1/entries/current",
            "/api/auth/direct-grants",
            "/api/v4/admin/tenants",
        };

        using (var session = AuthTestHelpers.CreateBearerClient(
                   Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, _admin)))
        {
            foreach (var route in routes)
            {
                (await session.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.OK,
                    "the subject's own sign-in reaches {0}", route);
            }
        }

        var linked = await ValidIdTokenAsync("id-token-admin", withOAuthClaims);
        using (var client = AuthTestHelpers.CreateBearerClient(Fixture, linked))
        {
            foreach (var route in routes)
            {
                (await client.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                    "a provider ID token is not a credential for {0}", route);
            }
        }

        var stranger = $"id-token-stranger-{withOAuthClaims}";
        using (var client = AuthTestHelpers.CreateBearerClient(Fixture, await ValidIdTokenAsync(stranger, withOAuthClaims)))
        {
            (await client.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using (var apex = Fixture.CreateApexClient())
        {
            apex.DefaultRequestHeaders.Add("Authorization", $"Bearer {await ValidIdTokenAsync(stranger, withOAuthClaims)}");
            (await apex.GetAsync("/api/v4/me/tenants")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        await using var conn = await OpenPostgresAsync();
        (await conn.ScalarAsync<long>("SELECT count(*) FROM subject_oidc_identities WHERE provider_id = @provider;",
                ("provider", _providerId)))
            .Should().Be(1, "only the identity the test linked exists");
        (await conn.ScalarAsync<long>("SELECT count(*) FROM subjects WHERE name = @name;", ("name", stranger)))
            .Should().Be(0);
    }

    /// <summary>
    /// Mints an ID token and validates it against the provider's published discovery document and
    /// keys, with the provider row's client id as the audience.
    /// </summary>
    private async Task<string> ValidIdTokenAsync(string sub, bool withOAuthClaims)
    {
        List<Claim> claims = [new("sub", sub), new("name", sub)];
        if (withOAuthClaims)
        {
            claims.Add(new Claim("scope", "openid profile"));
            claims.Add(new Claim("client_id", _clientId));
        }

        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: _issuer,
            audience: _clientId,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256)));

        var configuration = await new ConfigurationManager<OpenIdConnectConfiguration>(
                _issuer + "/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = false })
            .GetConfigurationAsync(CancellationToken.None);
        new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidIssuer = configuration.Issuer,
            ValidAudience = _clientId,
            IssuerSigningKeys = configuration.SigningKeys,
        }, out _);

        return token;
    }

    private async Task StartIdpAsync(string path)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        _idp = builder.Build();

        var parameters = _rsa.ExportParameters(includePrivateParameters: false);
        var keys = "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"" + KeyId + "\",\"n\":\""
                   + Base64UrlEncoder.Encode(parameters.Modulus) + "\",\"e\":\""
                   + Base64UrlEncoder.Encode(parameters.Exponent) + "\"}]}";
        _idp.MapGet(path + "/.well-known/openid-configuration", () => Results.Content(
            "{\"issuer\":\"" + _issuer + "\",\"jwks_uri\":\"" + _issuer + "/jwks\"}", "application/json"));
        _idp.MapGet(path + "/jwks", () => Results.Content(keys, "application/json"));
        await _idp.StartAsync();

        var bound = new Uri(_idp.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single());
        _issuer = $"http://localhost:{bound.Port}{path}";
    }
}
