using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// An identity provider's ID token sent as <c>Authorization: Bearer</c> authenticates the subject the
/// identity is already linked to, and nothing more: it is not the person's own sign-in, so it cannot
/// manage sign-in factors or credentials, and it never creates a subject.
/// </summary>
/// <remarks>
/// A provider on the loopback serves the discovery document and the signing keys, so each token goes
/// through the real handler chain, the membership gate and the action filters.
/// </remarks>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class ProviderBearerTokenIntegrationTests : ApiIntegrationTestBase
{
    private const string KeyId = "bearer-idp-key";

    private readonly RSA _rsa = RSA.Create(2048);
    private WebApplication _idp = null!;
    private string _issuer = null!;
    private string _clientId = null!;
    private Guid _providerId;

    public ProviderBearerTokenIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var port = FreeLoopbackPort();
        _issuer = $"http://localhost:{port}";
        await StartIdpAsync(port);

        _providerId = Guid.CreateVersion7();
        _clientId = $"client-{_providerId:N}";
        await using var conn = await OpenAsync();
        await ExecuteAsync(conn, """
            INSERT INTO oidc_providers (id, name, issuer_url, client_id, is_enabled, display_order, scopes, claim_mappings, default_roles, created_at, updated_at)
            VALUES (@provider, 'Bearer IdP', @issuer, @client, true, 0, '["openid"]'::jsonb, '{}'::jsonb, '[]'::jsonb, now(), now());
            """,
            ("provider", _providerId), ("issuer", _issuer), ("client", _clientId));
    }

    public override async Task DisposeAsync()
    {
        await using (var conn = await OpenAsync())
        {
            await ExecuteAsync(conn, """
                DELETE FROM totp_credentials WHERE subject_id IN
                    (SELECT subject_id FROM subject_oidc_identities WHERE provider_id = @provider);
                DELETE FROM subject_oidc_identities WHERE provider_id = @provider;
                DELETE FROM oidc_providers WHERE id = @provider;
                """,
                ("provider", _providerId));
        }

        await _idp.DisposeAsync();
        _rsa.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task A_provider_bearer_token_is_not_a_sign_in_for_a_subject_holding_an_authenticator()
    {
        var subjectId = await SeedLinkedMemberAsync("enrolled", withAuthenticator: true);

        using var client = AuthTestHelpers.CreateBearerClient(Fixture, IdToken("enrolled"));

        var data = await client.GetAsync("/api/v1/entries/current");
        data.StatusCode.Should().Be(HttpStatusCode.OK, "the token authenticates {0}", subjectId);
        (await client.GetAsync("/api/auth/direct-grants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/oauth/grants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/auth/direct-grants", new { label = "minted", scopes = new[] { "*" } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync("/api/auth/totp/setup", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_provider_bearer_token_reads_data_as_the_member_its_identity_is_linked_to()
    {
        await SeedLinkedMemberAsync("member", withAuthenticator: false);

        using var client = AuthTestHelpers.CreateBearerClient(Fixture, IdToken("member"));

        (await client.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/auth/direct-grants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_first_time_identity_on_a_tenant_host_is_refused_and_leaves_no_subject_or_identity_link()
    {
        using var client = AuthTestHelpers.CreateBearerClient(Fixture, IdToken("stranger"));

        (await client.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await IdentityLinkCountAsync("stranger")).Should().Be(0);
        (await SubjectsNamedAsync("stranger")).Should().Be(0);
    }

    [Fact]
    public async Task A_first_time_identity_on_the_apex_is_refused_and_leaves_no_subject_or_identity_link()
    {
        using var client = Fixture.CreateApexClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {IdToken("apex-stranger")}");

        (await client.GetAsync("/api/v4/me/tenants")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await IdentityLinkCountAsync("apex-stranger")).Should().Be(0);
        (await SubjectsNamedAsync("apex-stranger")).Should().Be(0);
    }

    private string IdToken(string sub)
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = KeyId };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: _issuer,
            audience: _clientId,
            claims: [new Claim("sub", sub), new Claim("name", sub)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
    }

    private async Task<Guid> SeedLinkedMemberAsync(string sub, bool withAuthenticator)
    {
        await using var conn = await OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, $"Bearer {sub}");
        await ExecuteAsync(conn, """
            INSERT INTO subject_oidc_identities (id, subject_id, provider_id, oidc_subject_id, issuer, linked_at)
            VALUES (gen_random_uuid(), @subject, @provider, @sub, @issuer, now());
            """,
            ("subject", subjectId), ("provider", _providerId), ("sub", sub), ("issuer", _issuer));
        if (withAuthenticator)
        {
            await ExecuteAsync(conn, """
                INSERT INTO totp_credentials (id, subject_id, secret_key, created_at)
                VALUES (gen_random_uuid(), @subject, '\x00'::bytea, now());
                """,
                ("subject", subjectId));
        }

        return subjectId;
    }

    private async Task<long> IdentityLinkCountAsync(string sub)
    {
        await using var conn = await OpenAsync();
        return await ScalarAsync<long>(conn,
            "SELECT count(*) FROM subject_oidc_identities WHERE provider_id = @provider AND oidc_subject_id = @sub;",
            ("provider", _providerId), ("sub", sub));
    }

    private async Task<long> SubjectsNamedAsync(string name)
    {
        await using var conn = await OpenAsync();
        return await ScalarAsync<long>(conn, "SELECT count(*) FROM subjects WHERE name = @name;", ("name", name));
    }

    private async Task StartIdpAsync(int port)
    {
        var parameters = _rsa.ExportParameters(includePrivateParameters: false);
        var keys = "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"" + KeyId + "\",\"n\":\""
                   + Base64UrlEncoder.Encode(parameters.Modulus) + "\",\"e\":\""
                   + Base64UrlEncoder.Encode(parameters.Exponent) + "\"}]}";
        var discovery = "{\"issuer\":\"" + _issuer + "\",\"jwks_uri\":\"" + _issuer + "/jwks\"}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        _idp = builder.Build();
        _idp.MapGet("/.well-known/openid-configuration", () => Results.Content(discovery, "application/json"));
        _idp.MapGet("/jwks", () => Results.Content(keys, "application/json"));
        await _idp.StartAsync();
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
