using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Mcp;

/// <summary>
/// <c>/mcp</c> end to end: the tool runs on the request's own tenant and credential, and the gate
/// refuses what it should, with the challenge an MCP client follows.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class McpEndpointIntegrationTests : ApiIntegrationTestBase
{
    private const int TenantAGlucose = 111;
    private const int TenantBGlucose = 222;

    private string _baseDomain = null!;
    private string _slugA = null!;
    private string _slugB = null!;
    private Guid _tenantAId;
    private string _sessionA = null!;
    private string _sessionB = null!;

    public McpEndpointIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        using (var provision = CreateAuthenticatedClient())
            (await provision.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();

        _tenantAId = await AuthTestHelpers.GetTenantIdAsync(conn);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT slug FROM tenants WHERE id = @id;";
            cmd.Parameters.AddWithValue("id", _tenantAId);
            _slugA = (string)(await cmd.ExecuteScalarAsync())!;
        }

        _slugB = $"mcp-b-{Guid.NewGuid():N}"[..18];
        var tenantBId = await AuthTestHelpers.SeedTenantAsync(Fixture, _slugB, "MCP Tenant B");
        _baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);

        var (subjectA, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantAId, "MCP User A");
        var (subjectB, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantBId, "MCP User B");
        _sessionA = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectA);
        _sessionB = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectB);

        await PostGlucoseAsync(_slugA, _sessionA, TenantAGlucose);
        await PostGlucoseAsync(_slugB, _sessionB, TenantBGlucose);
    }

    [Fact]
    public async Task GetCurrentStatus_ReadsTheRequestHostsTenantOnly()
    {
        var tokenA = await McpTokenAsync(_slugA, _sessionA, "glucose.read treatments.read");
        var tokenB = await McpTokenAsync(_slugB, _sessionB, "glucose.read");

        using var a = McpClient(_slugA, tokenA);
        var resultA = await CallToolAsync(a, "get_current_status");
        var textA = ToolText(resultA);
        Log(textA);
        textA.Should().Contain($"{TenantAGlucose} mg/dL").And.NotContain($"{TenantBGlucose}");

        using var b = McpClient(_slugB, tokenB);
        var textB = ToolText(await CallToolAsync(b, "get_current_status"));
        textB.Should().Contain($"{TenantBGlucose} mg/dL").And.NotContain($"{TenantAGlucose}");
        textB.Should().Contain("insulin and carbs on board", "tenant B's token holds no treatments.read");
    }

    [Fact]
    public async Task TokenOfOneTenant_IsRefusedOnAnotherTenantsHost()
    {
        var tokenA = await McpTokenAsync(_slugA, _sessionA, "glucose.read");

        using var client = McpClient(_slugB, tokenA);
        var response = await PostRpcAsync(client, "tools/call", new { name = "get_current_status" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoToken_Is401WithTheProtectedResourceChallenge()
    {
        using var client = AuthTestHelpers.CreateTenantClient(Fixture, _slugA, _baseDomain);
        var response = await PostRpcAsync(client, "tools/list", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        challenge.Should().Be(
            $"Bearer resource_metadata=\"http://{_slugA}.{_baseDomain}/.well-known/oauth-protected-resource\"");

        var metadata = await client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");
        metadata.GetProperty("resource").GetString().Should().Be($"http://{_slugA}.{_baseDomain}/mcp");

        var server = await client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        var issuer = server.GetProperty("issuer").GetString();
        issuer.Should().Be($"http://{_slugA}.{_baseDomain}", "a tenant host is its own authorization server");
        metadata.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(issuer);
    }

    [Fact]
    public async Task Registration_AcceptsTheMetadataAnMcpClientSends()
    {
        using var client = AuthTestHelpers.CreateTenantClient(Fixture, _slugA, _baseDomain);
        var response = await client.PostAsJsonAsync("/api/oauth/register", new
        {
            client_name = "Claude",
            redirect_uris = new[] { "https://claude.ai/api/mcp/auth_callback" },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
            application_type = "web",
            scope = "glucose.read treatments.read",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Authorize_CarriesTheResourceToConsent_WhenItIsThisMcpServer()
    {
        using var session = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slugA, _baseDomain, _sessionA);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(session, scope: "glucose.read");
        var (_, challenge) = AuthTestHelpers.GeneratePkceChallenge();

        async Task<string> ConsentLocationAsync(string? resource)
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var noRedirect = AuthTestHelpers.CreateAuthenticatedTenantClient(
                Fixture, handler, _slugA, _baseDomain, _sessionA);
            noRedirect.DefaultRequestHeaders.Remove("api-secret");
            var url = "/api/oauth/authorize?response_type=code&scope=glucose.read"
                + $"&client_id={Uri.EscapeDataString(clientId)}"
                + $"&redirect_uri={Uri.EscapeDataString("http://localhost:9999/callback")}"
                + $"&code_challenge={challenge}&code_challenge_method=S256"
                + (resource is null ? "" : $"&resource={Uri.EscapeDataString(resource)}");
            var response = await noRedirect.GetAsync(url);
            return response.Headers.Location?.ToString() ?? $"(no redirect: {(int)response.StatusCode})";
        }

        var mcpUrl = $"http://{_slugA}.{_baseDomain}/mcp";
        (await ConsentLocationAsync(mcpUrl)).Should().Contain($"&resource={Uri.EscapeDataString(mcpUrl)}");
        (await ConsentLocationAsync(null)).Should().StartWith("/oauth/consent").And.NotContain("resource=");
        (await ConsentLocationAsync($"http://{_slugB}.{_baseDomain}/mcp")).Should().Be("(no redirect: 400)");
    }

    [Fact]
    public async Task SessionToken_IsRefused()
    {
        using var client = McpClient(_slugA, _sessionA);
        var response = await PostRpcAsync(client, "tools/list", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TokenWithoutGlucoseRead_GetsAnErrorNamingTheScope()
    {
        var token = await McpTokenAsync(_slugA, _sessionA, "treatments.read");

        using var client = McpClient(_slugA, token);
        var response = await CallToolAsync(client, "get_current_status");

        response.TryGetProperty("error", out var error).Should().BeTrue(response.ToString());
        error.GetProperty("message").GetString().Should().Contain("glucose.read");
    }

    [Fact]
    public async Task TokenWithNoOfferedScope_Is403()
    {
        var token = await McpTokenAsync(_slugA, _sessionA, "food.read");

        using var client = McpClient(_slugA, token);
        var response = await PostRpcAsync(client, "tools/list", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("insufficient_scope");
    }

    [Fact]
    public async Task ShareHost_IsRefused()
    {
        string shareHost;
        using (var scope = Fixture.Services.CreateScope())
        {
            var link = await scope.ServiceProvider.GetRequiredService<IShareLinkService>().RotateAsync(_tenantAId);
            shareHost = new Uri(link.Url!).Host;
        }

        var token = await McpTokenAsync(_slugA, _sessionA, "glucose.read");
        using var anonymous = Fixture.CreateHttpClient("nocturne-api", "http");
        anonymous.DefaultRequestHeaders.Host = $"{shareHost.Split('.')[0]}.share.{_baseDomain}";
        using var withToken = Fixture.CreateHttpClient("nocturne-api", "http");
        withToken.DefaultRequestHeaders.Host = anonymous.DefaultRequestHeaders.Host;
        withToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await PostRpcAsync(anonymous, "tools/list", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostRpcAsync(withToken, "tools/call", new { name = "get_current_status" }))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ToolsList_AnnouncesTheView_AndShowGlucoseReturnsChartData()
    {
        var token = await McpTokenAsync(_slugA, _sessionA, "glucose.read");
        using var client = McpClient(_slugA, token);

        var list = await ReadRpcAsync(await PostRpcAsync(client, "tools/list", new { }));
        var showGlucose = list.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "show_glucose");
        showGlucose.GetProperty("_meta").GetProperty("ui").GetProperty("resourceUri").GetString()
            .Should().Be("ui://nocturne/report");

        var call = await CallToolAsync(client, "show_glucose", new { hours = 3 });
        var result = call.GetProperty("result");
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("kind").GetString().Should().Be("glucose");
        structured.GetProperty("data").GetProperty("glucoseData").EnumerateArray()
            .Select(p => p.GetProperty("sgv").GetDouble()).Should().Contain(TenantAGlucose);
        ToolText(call).Should().Contain("Tracks omitted: insulin, carbs and basal");
    }

    private HttpClient McpClient(string slug, string token)
    {
        var client = AuthTestHelpers.CreateTenantBearerClient(Fixture, slug, _baseDomain, token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        return client;
    }

    private static Task<HttpResponseMessage> PostRpcAsync(HttpClient client, string method, object @params)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params });
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string name, object? arguments = null) =>
        await ReadRpcAsync(await PostRpcAsync(client, "tools/call", new { name, arguments = arguments ?? new { } }));

    /// <summary>The JSON-RPC message of a response, whether sent as JSON or as one SSE event.</summary>
    private static async Task<JsonElement> ReadRpcAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = body.TrimStart().StartsWith('{')
            ? body
            : body.Split('\n').Select(l => l.TrimEnd('\r')).Last(l => l.StartsWith("data:"))["data:".Length..];
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static string ToolText(JsonElement rpc) =>
        string.Join("\n", rpc.GetProperty("result").GetProperty("content").EnumerateArray()
            .Select(c => c.GetProperty("text").GetString()));

    private async Task PostGlucoseAsync(string slug, string session, int sgv)
    {
        using var client = AuthTestHelpers.CreateTenantBearerClient(Fixture, slug, _baseDomain, session);
        var now = DateTimeOffset.UtcNow.AddMinutes(-2);
        var response = await client.PostAsJsonAsync("/api/v1/entries", new[]
        {
            new { type = "sgv", sgv, direction = "Flat", date = now.ToUnixTimeMilliseconds(), dateString = now.UtcDateTime.ToString("o") },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task McpToken_IsRefusedByTheApi_BeforeAndAfterRefresh()
    {
        var (access, refresh) = await McpTokenPairAsync(_slugA, _sessionA, "glucose.read");
        using (var api = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slugA, _baseDomain, access))
        {
            (await api.GetAsync("/api/v4/entries")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await api.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var anonymous = AuthTestHelpers.CreateTenantClient(Fixture, _slugA, _baseDomain);
        var refreshed = await anonymous.PostAsync("/api/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
        }));
        var body = await refreshed.Content.ReadAsStringAsync();
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var renewed = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("access_token").GetString()!;

        using (var api = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slugA, _baseDomain, renewed))
            (await api.GetAsync("/api/v4/entries")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var mcp = McpClient(_slugA, renewed);
        ToolText(await CallToolAsync(mcp, "get_current_status")).Should().Contain($"{TenantAGlucose} mg/dL");
    }

    [Fact]
    public async Task ApiToken_IsRefusedWithTheReason()
    {
        var token = await ApiTokenAsync(_slugA, _sessionA, "glucose.read");

        using var client = McpClient(_slugA, token);
        var response = await PostRpcAsync(client, "tools/list", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain($"not issued for http://{_slugA}.{_baseDomain}/mcp");
    }

    [Fact]
    public async Task ExchangedDirectGrantJwt_IsRefused()
    {
        using var session = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slugA, _baseDomain, _sessionA);
        var minted = await session.PostAsJsonAsync("/api/auth/direct-grants", new { label = "key", scopes = new[] { "glucose.read" } });
        var mintedBody = await minted.Content.ReadAsStringAsync();
        minted.StatusCode.Should().Be(HttpStatusCode.OK, mintedBody);
        var grantToken = JsonSerializer.Deserialize<JsonElement>(mintedBody).GetProperty("token").GetString()!;

        using var anonymous = AuthTestHelpers.CreateTenantClient(Fixture, _slugA, _baseDomain);
        var exchange = await anonymous.GetAsync($"/api/v2/authorization/request/{grantToken}");
        var exchangeBody = await exchange.Content.ReadAsStringAsync();
        exchange.StatusCode.Should().Be(HttpStatusCode.OK, exchangeBody);
        var jwt = JsonSerializer.Deserialize<JsonElement>(exchangeBody).GetProperty("token").GetString()!;

        using var client = McpClient(_slugA, jwt);
        (await PostRpcAsync(client, "tools/call", new { name = "get_current_status" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("https://api.example.test/")]
    [InlineData("urn:other")]
    public async Task AnotherResource_IsRefusedWithInvalidTarget(string other)
    {
        using var session = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slugA, _baseDomain, _sessionA);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(session, scope: "glucose.read");
        var (_, challenge) = AuthTestHelpers.GeneratePkceChallenge();

        var response = await session.GetAsync("/api/oauth/authorize?response_type=code&scope=glucose.read"
            + $"&client_id={Uri.EscapeDataString(clientId)}"
            + $"&redirect_uri={Uri.EscapeDataString("http://localhost:9999/callback")}"
            + $"&code_challenge={challenge}&code_challenge_method=S256&resource={Uri.EscapeDataString(other)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("error").GetString().Should().Be("invalid_target");
    }

    [Fact]
    public async Task ReadwriteOnlyRequest_ForThisMcpServer_IsRefused()
    {
        using var session = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slugA, _baseDomain, _sessionA);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(session, scope: "glucose.readwrite");
        var (_, challenge) = AuthTestHelpers.GeneratePkceChallenge();
        var mcpUrl = $"http://{_slugA}.{_baseDomain}/mcp";

        var authorize = await session.GetAsync("/api/oauth/authorize?response_type=code&scope=glucose.readwrite"
            + $"&client_id={Uri.EscapeDataString(clientId)}"
            + $"&redirect_uri={Uri.EscapeDataString("http://localhost:9999/callback")}"
            + $"&code_challenge={challenge}&code_challenge_method=S256&resource={Uri.EscapeDataString(mcpUrl)}");
        authorize.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await authorize.Content.ReadAsStringAsync()).Should().Contain("can only read data");

        var approve = await session.PostAsync("/api/oauth/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = "http://localhost:9999/callback",
            ["scope"] = "glucose.readwrite treatments.readwrite",
            ["code_challenge"] = challenge,
            ["approved"] = "true",
            ["resource"] = mcpUrl,
        }));
        approve.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await approve.Content.ReadAsStringAsync()).Should().Contain("can only read data");
    }

    [Fact]
    public async Task McpGrant_KeepsReadScopesOnly_WhenTheRequestMixesThem()
    {
        var (access, _) = await McpTokenPairAsync(_slugA, _sessionA, "glucose.readwrite treatments.read");
        var payload = access.Split('.')[1];
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=').Replace('-', '+').Replace('_', '/');
        var claims = JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(payload));

        claims.GetProperty("scope").GetString().Should().Be("treatments.read");
        claims.GetProperty("aud").GetString().Should().Be($"http://{_slugA}.{_baseDomain}/mcp");
    }

    [Fact]
    public async Task AuthorizeResponse_CarriesTheIssuerTheMetadataReports()
    {
        using var client = AuthTestHelpers.CreateTenantClient(Fixture, _slugA, _baseDomain);
        var issuer = (await client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server"))
            .GetProperty("issuer").GetString();

        var location = await McpAuthorizeLocationAsync(_slugA, _sessionA, "glucose.read");

        System.Web.HttpUtility.ParseQueryString(location.Query)["iss"].Should().Be(issuer);
    }

    [Fact]
    public async Task EveryToolCall_WritesOneReadAuditRow_FailedCallsIncluded()
    {
        using (var session = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slugA, _baseDomain, _sessionA))
        {
            var enabled = await session.PutAsJsonAsync("/api/v4/audit/config", new { readAuditEnabled = true });
            enabled.StatusCode.Should().Be(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync());
        }

        var token = await McpTokenAsync(_slugA, _sessionA, "glucose.read");
        using var client = McpClient(_slugA, token);

        var tooLong = await CallToolAsync(client, "show_glucose", new { hours = 72, note = "not declared" });
        tooLong.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        await CallToolAsync(client, "show_glucose", new { hours = 3 });

        var rows = new List<(int Status, string? Parameters, string? Entities)>();
        for (var attempt = 0; attempt < 50 && rows.Count < 2; attempt++)
        {
            await Task.Delay(100);
            rows.Clear();
            await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @t, false);
                SELECT status_code, query_parameters::text, entity_type FROM read_access_log
                WHERE endpoint = 'MCP show_glucose' ORDER BY created_at;
                """;
            cmd.Parameters.AddWithValue("t", _tenantAId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            while (await reader.ReadAsync())
                rows.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        rows.Should().HaveCount(2);
        rows[0].Status.Should().Be(500);
        rows[0].Parameters.Should().Be("""{"hours": 72}""", "only the arguments the tool declares are kept");
        rows[1].Status.Should().Be(200);
        rows[1].Entities.Should().StartWith("glucose");
    }

    private async Task<string> McpTokenAsync(string slug, string session, string scope) =>
        (await McpTokenPairAsync(slug, session, scope)).Access;

    /// <summary>The flow an MCP client runs: authorize and token both name this host's /mcp.</summary>
    private async Task<(string Access, string Refresh)> McpTokenPairAsync(string slug, string session, string scope)
    {
        var (location, clientId, verifier) = await McpAuthorizeAsync(slug, session, scope);
        var code = System.Web.HttpUtility.ParseQueryString(location.Query)["code"]!;
        using var client = AuthTestHelpers.CreateTenantBearerClient(Fixture, slug, _baseDomain, session);
        var resource = $"http://{slug}.{_baseDomain}/mcp";
        const string redirectUri = McpRedirectUri;

        var token = await client.PostAsync("/api/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["resource"] = resource,
        }));
        var body = await token.Content.ReadAsStringAsync();
        token.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        return (json.GetProperty("access_token").GetString()!, json.GetProperty("refresh_token").GetString()!);
    }

    private const string McpRedirectUri = "http://localhost:9999/callback";

    private async Task<Uri> McpAuthorizeLocationAsync(string slug, string session, string scope) =>
        (await McpAuthorizeAsync(slug, session, scope)).Location;

    /// <summary>Registers a client and approves consent for this host's /mcp, as the consent page posts it.</summary>
    private async Task<(Uri Location, string ClientId, string Verifier)> McpAuthorizeAsync(
        string slug, string session, string scope)
    {
        const string redirectUri = McpRedirectUri;
        var resource = $"http://{slug}.{_baseDomain}/mcp";
        using var client = AuthTestHelpers.CreateTenantBearerClient(Fixture, slug, _baseDomain, session);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(client, scope: scope);
        var (verifier, challenge) = AuthTestHelpers.GeneratePkceChallenge();

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var noRedirect = Fixture.CreateHttpClient(handler);
        noRedirect.DefaultRequestHeaders.Host = $"{slug}.{_baseDomain}";
        noRedirect.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session);
        var approval = await noRedirect.PostAsync("/api/oauth/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["code_challenge"] = challenge,
            ["approved"] = "true",
            ["resource"] = resource,
        }));
        var location = approval.Headers.Location
            ?? throw new InvalidOperationException($"No redirect: {(int)approval.StatusCode} {await approval.Content.ReadAsStringAsync()}");
        return (location, clientId, verifier);
    }

    private async Task<string> ApiTokenAsync(string slug, string session, string scope)
    {
        using var client = AuthTestHelpers.CreateTenantBearerClient(Fixture, slug, _baseDomain, session);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(client, scope: scope);
        return (await AuthTestHelpers.ExecutePkceFlowAsync(client, clientId, scope: scope)).AccessToken;
    }
}
