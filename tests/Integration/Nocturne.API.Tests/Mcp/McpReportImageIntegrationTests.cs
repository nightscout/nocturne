using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Mcp;

/// <summary>
/// <c>render_report_image</c> and the presets' <c>asImage</c> over <c>/mcp</c> on a seeded tenant, with
/// the web app's image route played by <see cref="ReportImageWebStub"/>.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class McpReportImageIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private string _slug = null!;
    private string _baseDomain = null!;
    private string _session = null!;
    private Guid _tenantId;

    private static object GlucoseSpec => new
    {
        version = 1,
        range = new { hours = 6 },
        panels = new object[]
        {
            new { id = "line", dataset = new { name = "glucose_series", markers = new[] { "bolus", "carbs" } }, primitive = "time_series", bindings = new { x = "time", y = new[] { "glucose" } } },
            new { id = "figures", dataset = new { name = "glucose_statistics" }, primitive = "stat_tiles" },
        },
    };

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        using (var provision = CreateAuthenticatedClient())
            (await provision.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT slug FROM tenants WHERE id = @id;";
            cmd.Parameters.AddWithValue("id", _tenantId);
            _slug = (string)(await cmd.ExecuteScalarAsync())!;
        }
        _baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);
        var (subject, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "MCP images");
        _session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subject);
        Fixture.ReportImageWeb.Down = false;
        await SeedAsync();
    }

    [Fact]
    public async Task Render_report_image_returns_the_summary_then_one_png_per_chart_panel_and_no_view()
    {
        var sentBefore = Fixture.ReportImageWeb.Received.Count;
        using var client = McpClient(await McpTokenAsync("glucose.read treatments.read"));

        var call = await CallToolAsync(client, "render_report_image", new { spec = GlucoseSpec, width = 800, height = 400, theme = "dark" });

        call.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
        var result = call.GetProperty("result");
        result.TryGetProperty("structuredContent", out _).Should().BeFalse("an image result is not a view");
        var content = result.GetProperty("content").EnumerateArray().ToList();
        content.Select(c => c.GetProperty("type").GetString()).Should().Equal("text", "image");
        content[1].GetProperty("mimeType").GetString().Should().Be("image/png");
        Convert.FromBase64String(content[1].GetProperty("data").GetString()!).Should().Equal(ReportImageWebStub.Png);
        var text = content[0].GetProperty("text").GetString()!;
        Log(text);
        text.Should().Contain("[line]").And.Contain("[figures]").And.Contain("Image 1: panel line, 800 by 400 pixels at ratio 1, dark theme.");

        var sent = Fixture.ReportImageWeb.Received.Skip(sentBefore).Should().ContainSingle("stat tiles have no image").Subject;
        sent.GetProperty("panelId").GetString().Should().Be("line");
        (sent.GetProperty("width").GetInt32(), sent.GetProperty("height").GetInt32(), sent.GetProperty("theme").GetString(), sent.GetProperty("devicePixelRatio").GetInt32())
            .Should().Be((800, 400, "dark", 1));
        var panel = sent.GetProperty("result").GetProperty("panels").EnumerateArray().Should().ContainSingle().Subject;
        panel.GetProperty("primitive").GetString().Should().Be("time_series");
        panel.GetProperty("rows").GetArrayLength().Should().BeGreaterThan(50);
        panel.GetProperty("markers").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_glucose_only_token_still_gets_the_glucose_panel_without_treatments()
    {
        var sentBefore = Fixture.ReportImageWeb.Received.Count;
        using var client = McpClient(await McpTokenAsync("glucose.read"));

        var call = await CallToolAsync(client, "render_report_image", new { spec = GlucoseSpec, panelId = "line" });

        call.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
        call.GetProperty("result").GetProperty("content").EnumerateArray().Select(c => c.GetProperty("type").GetString())
            .Should().Equal("text", "image");
        var panel = Fixture.ReportImageWeb.Received.Skip(sentBefore).Should().ContainSingle().Subject
            .GetProperty("result").GetProperty("panels")[0];
        panel.GetProperty("status").GetString().Should().Be("ok");
        (panel.TryGetProperty("markers", out var markers) ? markers.GetArrayLength() : 0).Should().Be(0, "markers need treatments.read");
        ToolText(call).Should().Contain("Markers omitted: bolus, carbs");
    }

    [Fact]
    public async Task The_web_app_being_down_is_a_tool_error_with_the_reason_not_a_server_error()
    {
        using var client = McpClient(await McpTokenAsync("glucose.read treatments.read"));
        Fixture.ReportImageWeb.Down = true;
        try
        {
            var call = await CallToolAsync(client, "render_report_image", new { spec = GlucoseSpec });

            call.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
            var result = call.GetProperty("result");
            result.GetProperty("isError").GetBoolean().Should().BeTrue();
            ToolText(call).Should().Contain("[line]")
                .And.EndWith("The images could not be drawn: the web app that draws images did not answer. The report's text is above.");
        }
        finally
        {
            Fixture.ReportImageWeb.Down = false;
        }
    }

    [Fact]
    public async Task Invalid_image_arguments_and_a_spec_with_no_chart_are_refused_before_anything_is_read()
    {
        var sentBefore = Fixture.ReportImageWeb.Received.Count;
        using var client = McpClient(await McpTokenAsync("glucose.read"));

        var tooWide = await CallToolAsync(client, "render_report_image", new { spec = GlucoseSpec, width = 5000, devicePixelRatio = 3 });
        var tiles = await CallToolAsync(client, "render_report_image", new { spec = GlucoseSpec, panelId = "figures" });

        tooWide.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(tooWide).Should().Contain("width: Send a whole number from 320 to 2400.").And.Contain("devicePixelRatio: Send 1 or 2.");
        ToolText(tiles).Should().Contain("panelId: Panel 'figures' is drawn as stat_tiles, which has no image.");
        Fixture.ReportImageWeb.Received.Count.Should().Be(sentBefore);
    }

    [Fact]
    public async Task The_presets_draw_their_chart_panels_as_images_when_asked()
    {
        using var client = McpClient(await McpTokenAsync("glucose.read treatments.read"));

        var agp = await CallToolAsync(client, "show_agp", new { days = 7, asImage = true });
        var glucose = await CallToolAsync(client, "show_glucose", new { hours = 6, asImage = true });
        var treatments = await CallToolAsync(client, "show_treatments", new { hours = 24, asImage = true });
        var comparison = await CallToolAsync(client, "compare_periods", new { preset = "last7-prior7", asImage = true });

        static IEnumerable<string?> Types(JsonElement call) =>
            call.GetProperty("result").GetProperty("content").EnumerateArray().Select(c => c.GetProperty("type").GetString());
        Types(agp).Should().Equal("text", "image", "image");
        Types(glucose).Should().Equal("text", "image");
        Types(treatments).Should().Equal("text", "image");
        Types(comparison).Should().Equal("text", "image");
        foreach (var call in new[] { agp, glucose, treatments, comparison })
            call.GetProperty("result").TryGetProperty("structuredContent", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Render_report_image_writes_the_audit_row_render_report_writes_for_the_same_spec()
    {
        var since = DateTime.UtcNow;
        await SetReadAuditAsync(true);
        try
        {
            using var client = McpClient(await McpTokenAsync("glucose.read treatments.read"));
            await CallToolAsync(client, "render_report", new { spec = GlucoseSpec });
            await CallToolAsync(client, "render_report_image", new { spec = GlucoseSpec });

            var rows = await AuditAsync(since, 2);
            rows.Should().ContainKeys("MCP render_report", "MCP render_report_image");
            rows["MCP render_report_image"].Should().Be(rows["MCP render_report"]);
            rows["MCP render_report_image"].Entities.Should().Be("glucose,treatments");
        }
        finally
        {
            await SetReadAuditAsync(false);
        }
    }

    private async Task SetReadAuditAsync(bool enabled)
    {
        using var session = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slug, _baseDomain, _session);
        var response = await session.PutAsJsonAsync("/api/v4/audit/config", new { readAuditEnabled = enabled });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The MCP read-audit rows since <paramref name="since"/>, by endpoint, once <paramref name="expected"/> are written.</summary>
    private async Task<Dictionary<string, (string? Entities, int Count, int Status)>> AuditAsync(DateTime since, int expected)
    {
        var rows = new Dictionary<string, (string? Entities, int Count, int Status)>();
        for (var attempt = 0; attempt < 50 && rows.Count < expected; attempt++)
        {
            await Task.Delay(100);
            rows.Clear();
            await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @t, false);
                SELECT endpoint, entity_type, record_count, status_code FROM read_access_log
                WHERE endpoint LIKE 'MCP %' AND created_at >= @since ORDER BY created_at;
                """;
            cmd.Parameters.AddWithValue("t", _tenantId.ToString());
            cmd.Parameters.AddWithValue("since", since);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            while (await reader.ReadAsync())
                rows[reader.GetString(0)] = (reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2), reader.GetInt32(3));
        }
        return rows;
    }

    /// <summary>Eight days of glucose; a 45 g lunch with 4.5 U at 06:00 UTC daily and one in the last hours.</summary>
    private async Task SeedAsync()
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(new TenantContext(_tenantId, _slug, "MCP", true, false));
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();

        var today = DateTime.UtcNow.Date;
        var i = 0;
        for (var t = today.AddDays(-8); t < DateTime.UtcNow; t = t.AddMinutes(5), i++)
            context.SensorGlucose.Add(new SensorGlucoseEntity { Id = Guid.NewGuid(), Timestamp = t, Mgdl = 60 + Math.Abs(i % 80 - 40) * 5, DataSource = "test" });
        foreach (var meal in Enumerable.Range(0, 8).Select(d => today.AddDays(-d).AddHours(6)).Append(DateTime.UtcNow.AddHours(-2)))
        {
            if (meal > DateTime.UtcNow)
                continue;
            context.CarbIntakes.Add(new CarbIntakeEntity { Id = Guid.NewGuid(), Timestamp = meal, Carbs = 45, DataSource = "test" });
            context.Boluses.Add(new BolusEntity { Id = Guid.NewGuid(), Timestamp = meal, Insulin = 4.5, BolusKind = "Manual", DataSource = "test" });
        }
        await context.SaveChangesAsync();
    }

    private HttpClient McpClient(string token)
    {
        var client = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slug, _baseDomain, token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        return client;
    }

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string name, object arguments)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } });
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var json = text.TrimStart().StartsWith('{')
            ? text
            : text.Split('\n').Select(l => l.TrimEnd('\r')).Last(l => l.StartsWith("data:"))["data:".Length..];
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static string ToolText(JsonElement rpc) =>
        string.Join("\n", rpc.GetProperty("result").GetProperty("content").EnumerateArray()
            .Where(c => c.GetProperty("type").GetString() == "text").Select(c => c.GetProperty("text").GetString()));

    /// <summary>The flow an MCP client runs, as <c>McpEndpointIntegrationTests</c> runs it.</summary>
    private async Task<string> McpTokenAsync(string scope)
    {
        const string redirectUri = "http://localhost:9999/callback";
        var resource = $"http://{_slug}.{_baseDomain}/mcp";
        using var session = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slug, _baseDomain, _session);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(session, scope: scope);
        var (verifier, challenge) = AuthTestHelpers.GeneratePkceChallenge();

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var noRedirect = Fixture.CreateHttpClient(handler);
        noRedirect.DefaultRequestHeaders.Host = $"{_slug}.{_baseDomain}";
        noRedirect.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _session);
        var approval = await noRedirect.PostAsync("/api/oauth/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["redirect_uri"] = redirectUri, ["scope"] = scope, ["code_challenge"] = challenge,
            ["approved"] = "true", ["resource"] = resource,
        }));
        var code = System.Web.HttpUtility.ParseQueryString(approval.Headers.Location!.Query)["code"]!;
        var token = await session.PostAsync("/api/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId, ["resource"] = resource,
        }));
        var body = await token.Content.ReadAsStringAsync();
        token.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("access_token").GetString()!;
    }
}
