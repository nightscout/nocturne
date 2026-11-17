using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// What a credential may do beyond reading and writing data: a member's keys stop with the
/// membership, a key cannot mint or widen keys, and only the person's own sign-in manages their
/// credentials and sign-in factors.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class CredentialAuthorityIntegrationTests : ApiIntegrationTestBase
{
    private Guid _tenantId;
    private Guid _ownerId;
    private Guid _memberId;
    private string _ownerSession = null!;
    private string _memberSession = null!;

    public CredentialAuthorityIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        using var client = CreateAuthenticatedClient();
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_ownerId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Authority Owner");
        (_memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Authority Member");
        _ownerSession = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, _ownerId);
        _memberSession = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, _memberId);
    }

    [Fact]
    public async Task RemovedMember_ApiSecret_IsDenied()
    {
        var token = await MintAsync(_memberSession, "glucose.read");
        using var uploader = ApiSecretClient(token);
        (await uploader.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var owner = AuthTestHelpers.CreateBearerClient(Fixture, _ownerSession);
        (await owner.DeleteAsync($"/api/v4/member-invites/members/{_memberId}"))
            .IsSuccessStatusCode.Should().BeTrue();

        (await uploader.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The membership row going is enough on its own, with the grant still live: the api-secret is
    /// held to membership exactly as the bearer form of the same token is.
    /// </summary>
    [Fact]
    public async Task ApiSecret_OfASubjectWithNoMembership_IsDenied()
    {
        var token = await MintAsync(_memberSession, "treatments.readwrite");

        await using (var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM tenant_members WHERE subject_id = @s AND tenant_id = @t;";
            cmd.Parameters.AddWithValue("s", _memberId);
            cmd.Parameters.AddWithValue("t", _tenantId);
            await cmd.ExecuteNonQueryAsync();
        }

        using var uploader = ApiSecretClient(token);
        (await uploader.GetAsync("/api/v1/treatments")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await uploader.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new { eventType = "Note", notes = "after removal", created_at = DateTime.UtcNow.ToString("o") },
        })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("api-secret")]
    public async Task NarrowKey_CannotManageCredentialsOrSignInFactors(string presentation)
    {
        var token = await MintAsync(_memberSession, "glucose.read");
        using var key = presentation == "bearer"
            ? AuthTestHelpers.CreateBearerClient(Fixture, token)
            : ApiSecretClient(token);

        (await key.PostAsJsonAsync("/api/auth/direct-grants", new { label = "wider", scopes = new[] { "*" } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.GetAsync("/api/auth/direct-grants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.GetAsync("/api/oauth/grants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.PostAsync("/api/auth/passkey/recovery/regenerate", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.PostAsJsonAsync("/api/auth/passkey/register/options", new { username = "second-device" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.DeleteAsync($"/api/auth/totp/{Guid.CreateVersion7()}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A grant with no scopes mints an exchange JWT with no scope claim. It must still read as the
    /// grant it came from, never as a session carrying the member's whole role.
    /// </summary>
    [Fact]
    public async Task ExchangeJwt_OfAGrantWithNoScopes_IsNotASession()
    {
        var (grantId, token) = await MintGrantAsync(_ownerSession, "glucose.read");
        using var owner = AuthTestHelpers.CreateBearerClient(Fixture, _ownerSession);

        (await owner.PatchAsJsonAsync($"/api/oauth/grants/{grantId}", new { scopes = Array.Empty<string>() }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A row left with no scopes by an older release.
        await using (var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @t, false);
                UPDATE oauth_grants SET scopes = ARRAY[]::text[] WHERE id = @g;
                """;
            cmd.Parameters.AddWithValue("t", _tenantId.ToString());
            cmd.Parameters.AddWithValue("g", grantId);
            await cmd.ExecuteNonQueryAsync();
        }

        using var anonymous = Fixture.CreateHttpClient("nocturne-api", "http");
        var exchange = await anonymous.GetAsync($"/api/v2/authorization/request/{token}");
        var body = await exchange.Content.ReadAsStringAsync();
        exchange.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var jwt = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("token").GetString()!;

        using var exchanged = AuthTestHelpers.CreateBearerClient(Fixture, jwt);
        (await exchanged.PostAsJsonAsync("/api/auth/direct-grants", new { label = "wider", scopes = new[] { "*" } }))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await exchanged.PostAsync("/api/auth/passkey/recovery/regenerate", null))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await exchanged.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Session_ManagesItsOwnCredentialsAndSignInFactors()
    {
        using var session = AuthTestHelpers.CreateBearerClient(Fixture, _memberSession);

        (await session.PostAsync("/api/auth/passkey/recovery/regenerate", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.PostAsync("/api/auth/totp/setup", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.GetAsync("/api/oauth/grants")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.GetAsync("/api/auth/direct-grants")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NightscoutSecret_SavedByAMember_KeepsAuthenticatingAfterTheyLeave()
    {
        const string secret = "integration-nightscout-secret";

        using var member = AuthTestHelpers.CreateBearerClient(Fixture, _memberSession);
        (await member.PutAsJsonAsync("/api/v4/connectors/config/nightscout/secrets", new { apiSecret = secret }))
            .IsSuccessStatusCode.Should().BeTrue();

        using var owner = AuthTestHelpers.CreateBearerClient(Fixture, _ownerSession);
        (await owner.DeleteAsync($"/api/v4/member-invites/members/{_memberId}"))
            .IsSuccessStatusCode.Should().BeTrue();

        using var uploader = ApiSecretClient(Sha1(secret));
        (await uploader.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.OK,
            "the site's secret belongs to the site, not to whoever saved it");
    }

    private async Task<string> MintAsync(string session, params string[] scopes) =>
        (await MintGrantAsync(session, scopes)).Token;

    private async Task<(Guid Id, string Token)> MintGrantAsync(string session, params string[] scopes)
    {
        using var client = AuthTestHelpers.CreateBearerClient(Fixture, session);
        var response = await client.PostAsJsonAsync("/api/auth/direct-grants", new { label = "key", scopes });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var created = JsonSerializer.Deserialize<JsonElement>(body);
        return (created.GetProperty("id").GetGuid(), created.GetProperty("token").GetString()!);
    }

    private HttpClient ApiSecretClient(string secret)
    {
        var client = Fixture.CreateHttpClient("nocturne-api", "http");
        client.DefaultRequestHeaders.Add("api-secret", secret);
        return client;
    }

    private static string Sha1(string value) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(value)));
}
