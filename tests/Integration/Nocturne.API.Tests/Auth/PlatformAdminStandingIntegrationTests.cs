using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
/// A subject's platform-admin standing reaches the platform-admin endpoints only through a credential
/// that came from the person's own sign-in. An identity provider's ID token sent as a bearer did not,
/// so it is answered as a member who is not a platform admin is answered.
/// </summary>
/// <remarks>
/// A provider on the loopback serves the discovery document and the signing keys, so each token goes
/// through the real handler chain, the membership gate and the role check.
/// </remarks>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class PlatformAdminStandingIntegrationTests : ApiIntegrationTestBase
{
    private const string KeyId = "standing-idp-key";

    private static readonly Regex TraceId = new("\"traceId\":\"[^\"]*\"", RegexOptions.Compiled);

    private readonly RSA _rsa = RSA.Create(2048);
    private WebApplication _idp = null!;
    private string _issuer = null!;
    private string _clientId = null!;
    private Guid _providerId;
    private Guid _tenantId;
    private Guid _admin;
    private Guid _member;

    public PlatformAdminStandingIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
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
            VALUES (@provider, 'Standing IdP', @issuer, @client, true, 0, '["openid"]'::jsonb, '{}'::jsonb, '[]'::jsonb, now(), now());
            """,
            ("provider", _providerId), ("issuer", _issuer), ("client", _clientId));

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        _admin = await SeedLinkedMemberAsync(conn, "standing-admin");
        _member = await SeedLinkedMemberAsync(conn, "standing-member");
        await ExecuteAsync(conn, "UPDATE subjects SET is_platform_admin = true WHERE id = @id;", ("id", _admin));
    }

    public override async Task DisposeAsync()
    {
        await using (var conn = await OpenAsync())
        {
            await ExecuteAsync(conn, """
                UPDATE subjects SET is_platform_admin = false WHERE id IN (@admin, @member);
                DELETE FROM subject_oidc_identities WHERE provider_id = @provider;
                DELETE FROM oidc_providers WHERE id = @provider;
                """,
                ("admin", _admin), ("member", _member), ("provider", _providerId));
        }

        await _idp.DisposeAsync();
        _rsa.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task A_platform_admins_provider_bearer_token_cannot_mint_a_direct_grant_on_a_tenant()
    {
        var path = $"/api/v4/admin/tenants/{_tenantId}/direct-grants";
        object Body() => new { subjectId = _member, label = "standing", scopes = new[] { "glucose.read" } };

        var asMember = await SendAsync(IdToken("standing-member"), c => c.PostAsJsonAsync(path, Body()));
        var asAdmin = await SendAsync(IdToken("standing-admin"), c => c.PostAsJsonAsync(path, Body()));
        var asAdminSession = await SendAsync(await SessionAsync(_admin), c => c.PostAsJsonAsync(path, Body()));

        asMember.Status.Should().Be(HttpStatusCode.Forbidden);
        asAdmin.Should().Be(asMember);
        asAdminSession.Status.Should().Be(HttpStatusCode.OK, "the subject is a platform admin when signed in");
    }

    [Fact]
    public async Task A_platform_admins_provider_bearer_token_cannot_make_a_subject_a_platform_admin()
    {
        var path = $"/api/v4/admin/subjects/{_member}/platform-admin";
        object Body() => new { isPlatformAdmin = true };

        var asMember = await SendAsync(IdToken("standing-member"), c => c.PutAsJsonAsync(path, Body()));
        var asAdmin = await SendAsync(IdToken("standing-admin"), c => c.PutAsJsonAsync(path, Body()));

        asMember.Status.Should().Be(HttpStatusCode.Forbidden);
        asAdmin.Should().Be(asMember);
        (await IsPlatformAdminAsync(_member)).Should().BeFalse();

        var selfPath = $"/api/v4/admin/subjects/{_admin}/platform-admin";
        var asAdminSession = await SendAsync(await SessionAsync(_admin), c => c.PutAsJsonAsync(selfPath, Body()));
        asAdminSession.Status.Should().Be(HttpStatusCode.NoContent, "the subject is a platform admin when signed in");
    }

    [Fact]
    public async Task A_platform_admins_provider_bearer_token_cannot_mint_a_platform_access_grant()
    {
        var path = $"/api/auth/platform-access?tenant=standing-{Guid.NewGuid():N}";

        var asMember = await SendAsync(IdToken("standing-member"), c => c.GetAsync(path));
        var asAdmin = await SendAsync(IdToken("standing-admin"), c => c.GetAsync(path));
        var asAdminSession = await SendAsync(await SessionAsync(_admin), c => c.GetAsync(path));

        asMember.Status.Should().Be(HttpStatusCode.Forbidden);
        asAdmin.Should().Be(asMember);
        asAdminSession.Status.Should().Be(HttpStatusCode.NotFound,
            "a signed-in platform admin passes the check and reaches the slug lookup");
    }

    [Fact]
    public async Task A_platform_admins_provider_bearer_token_still_reads_the_tenants_data()
    {
        var read = await SendAsync(IdToken("standing-admin"), c => c.GetAsync("/api/v1/entries/current"));

        read.Status.Should().Be(HttpStatusCode.OK);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string bearer, Func<HttpClient, Task<HttpResponseMessage>> send)
    {
        using var client = Fixture.CreateHttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {bearer}");
        using var response = await send(client);
        var body = TraceId.Replace(await response.Content.ReadAsStringAsync(), string.Empty);
        return (response.StatusCode, body);
    }

    private Task<string> SessionAsync(Guid subjectId) => AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);

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

    private async Task<Guid> SeedLinkedMemberAsync(NpgsqlConnection conn, string sub)
    {
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, sub);
        await ExecuteAsync(conn, """
            INSERT INTO subject_oidc_identities (id, subject_id, provider_id, oidc_subject_id, issuer, linked_at)
            VALUES (gen_random_uuid(), @subject, @provider, @sub, @issuer, now());
            """,
            ("subject", subjectId), ("provider", _providerId), ("sub", sub), ("issuer", _issuer));
        return subjectId;
    }

    private async Task<bool> IsPlatformAdminAsync(Guid subjectId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_platform_admin FROM subjects WHERE id = @id;";
        cmd.Parameters.AddWithValue("id", subjectId);
        return (bool)(await cmd.ExecuteScalarAsync())!;
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
}
