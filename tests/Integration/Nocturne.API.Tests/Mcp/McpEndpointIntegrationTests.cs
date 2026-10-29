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
        var tokenA = await OAuthTokenAsync(_slugA, _sessionA, "glucose.read treatments.read");
        var tokenB = await OAuthTokenAsync(_slugB, _sessionB, "glucose.read");

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
        var tokenA = await OAuthTokenAsync(_slugA, _sessionA, "glucose.read");

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
        issuer.Should().Be($"http://{_slugA}.{_baseDomain}");
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
    public async Task Authorize_FlagsTheConsentPage_WhenTheResourceIsThisMcpServer()
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

        (await ConsentLocationAsync($"http://{_slugA}.{_baseDomain}/mcp")).Should().Contain("&mcp=1");
        (await ConsentLocationAsync(null)).Should().StartWith("/oauth/consent").And.NotContain("mcp=1");
        (await ConsentLocationAsync($"http://{_slugB}.{_baseDomain}/mcp")).Should().NotContain("mcp=1");
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
        var token = await OAuthTokenAsync(_slugA, _sessionA, "treatments.read");

        using var client = McpClient(_slugA, token);
        var response = await CallToolAsync(client, "get_current_status");

        response.TryGetProperty("error", out var error).Should().BeTrue(response.ToString());
        error.GetProperty("message").GetString().Should().Contain("glucose.read");
    }

    [Fact]
    public async Task TokenWithNoOfferedScope_Is403()
    {
        var token = await OAuthTokenAsync(_slugA, _sessionA, "food.read");

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

        var token = await OAuthTokenAsync(_slugA, _sessionA, "glucose.read");
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
        var token = await OAuthTokenAsync(_slugA, _sessionA, "glucose.read");
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

    private async Task<string> OAuthTokenAsync(string slug, string session, string scope)
    {
        using var client = AuthTestHelpers.CreateTenantBearerClient(Fixture, slug, _baseDomain, session);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(client, scope: scope);
        return (await AuthTestHelpers.ExecutePkceFlowAsync(client, clientId, scope: scope)).AccessToken;
    }
}
