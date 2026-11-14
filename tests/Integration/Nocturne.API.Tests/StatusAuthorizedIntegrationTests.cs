using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// <c>status.json?token=</c> answers <c>authorized</c> from the same exchange as
/// <c>/api/v2/authorization/request</c>, in the host's tenant only, and never from a cache.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class StatusAuthorizedIntegrationTests : ApiIntegrationTestBase
{
    private Guid _tenantId;
    private Guid _subjectId;
    private string _token = null!;

    public StatusAuthorizedIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        using var client = CreateAuthenticatedClient();
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = await OpenAsync();
        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_subjectId, _token) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Status Dashboard");
    }

    [Fact]
    public async Task AValidToken_AnswersTheJwtTheV2ExchangeIssues()
    {
        using var anonymous = Anonymous();

        var status = await anonymous.GetAsync($"/api/v1/status.json?token={_token}");
        var exchange = await anonymous.GetAsync($"/api/v2/authorization/request/{_token}");

        var statusBody = await status.Content.ReadAsStringAsync();
        var exchangeBody = await exchange.Content.ReadAsStringAsync();
        status.StatusCode.Should().Be(HttpStatusCode.OK, statusBody);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK, exchangeBody);

        var authorized = JsonSerializer.Deserialize<JsonElement>(statusBody).GetProperty("authorized");
        var exchanged = JsonSerializer.Deserialize<JsonElement>(exchangeBody);

        authorized.GetProperty("sub").GetString().Should().Be("Status Dashboard");
        authorized.GetProperty("sub").GetString().Should().Be(exchanged.GetProperty("sub").GetString());
        authorized.GetProperty("permissionGroups").GetRawText()
            .Should().Be(exchanged.GetProperty("permissionGroups").GetRawText())
            .And.Be("""[["*"]]""");
        authorized.GetProperty("iat").GetInt64().Should().BeGreaterThan(0);
        authorized.GetProperty("exp").GetInt64().Should().BeGreaterThan(authorized.GetProperty("iat").GetInt64());

        IdentityClaims(authorized.GetProperty("token").GetString()!)
            .Should().Equal(IdentityClaims(exchanged.GetProperty("token").GetString()!));
    }

    [Fact]
    public async Task AValidToken_IsMarkedNoStore()
    {
        using var anonymous = Anonymous();

        var status = await anonymous.GetAsync($"/api/v1/status.json?token={_token}");

        status.StatusCode.Should().Be(HttpStatusCode.OK);
        status.Headers.CacheControl!.NoStore.Should().BeTrue();
        status.Headers.CacheControl.Public.Should().BeFalse();
    }

    /// <summary>
    /// A stored response would still carry the JWT once the grant is revoked, and would answer a
    /// token-less request on the same path with it if the key ignored the query.
    /// </summary>
    [Fact]
    public async Task AnAuthorizedResponse_IsNotServedAgainFromACache()
    {
        using var anonymous = Anonymous();
        var url = $"/api/v1/status.json?token={_token}";

        (await AuthorizedAsync(anonymous, url)).ValueKind.Should().Be(JsonValueKind.Object);
        (await AuthorizedAsync(anonymous, "/api/v1/status.json")).ValueKind.Should().Be(JsonValueKind.Null);

        await using (var conn = await OpenAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @tenant, false);
                UPDATE oauth_grants SET revoked_at = now() WHERE subject_id = @subject;
                """;
            cmd.Parameters.AddWithValue("tenant", _tenantId.ToString());
            cmd.Parameters.AddWithValue("subject", _subjectId);
            await cmd.ExecuteNonQueryAsync();
        }

        (await AuthorizedAsync(anonymous, url)).ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?token=not-a-token")]
    public async Task AnAbsentOrInvalidToken_AnswersTheNormalStatusWithAuthorizedNull(string query)
    {
        using var anonymous = Anonymous();

        (await AuthorizedAsync(anonymous, "/api/v1/status.json" + query))
            .ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task AnotherTenantsToken_IsNotAuthorizedOnThisHost_AsTheV2ExchangeRefusesIt()
    {
        var slug = $"status-b-{Guid.NewGuid():N}"[..20];
        var otherTenant = await AuthTestHelpers.SeedTenantAsync(Fixture, slug, "Status B");
        await using var conn = await OpenAsync();
        var (_, otherToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, otherTenant, "Status B User");

        using var thisHost = Anonymous();
        (await thisHost.GetAsync($"/api/v2/authorization/request/{otherToken}"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AuthorizedAsync(thisHost, $"/api/v1/status.json?token={otherToken}"))
            .ValueKind.Should().Be(JsonValueKind.Null);

        using var ownHost = AuthTestHelpers.CreateTenantClient(Fixture, slug, AuthTestHelpers.GetBaseDomain(ApiClient));
        (await AuthorizedAsync(ownHost, $"/api/v1/status.json?token={otherToken}"))
            .GetProperty("sub").GetString().Should().Be("Status B User");
    }

    private HttpClient Anonymous() => Fixture.CreateHttpClient("nocturne-api", "http");

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<JsonElement> AuthorizedAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("authorized");
    }

    /// <summary>The JWT's claims, less those that differ between two tokens minted moments apart.</summary>
    private static List<KeyValuePair<string, string>> IdentityClaims(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var claims = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));

        return claims.EnumerateObject()
            .Where(c => c.Name is not ("iat" or "exp" or "nbf" or "jti"))
            .Select(c => KeyValuePair.Create(c.Name, c.Value.GetRawText()))
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .ToList();
    }
}
