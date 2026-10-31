using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Mcp;

/// <summary>
/// The Phase 5 presets and text tools over <c>/mcp</c> on a seeded tenant: <c>show_agp</c>,
/// <c>show_treatments</c>, and the text tools, each with full and partial scopes.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class McpPresetIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private string _slug = null!;
    private string _baseDomain = null!;
    private string _session = null!;
    private Guid _tenantId;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        using (var provision = CreateAuthenticatedClient())
            (await provision.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        _tenantId = tenantId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT slug FROM tenants WHERE id = @id;";
            cmd.Parameters.AddWithValue("id", tenantId);
            _slug = (string)(await cmd.ExecuteScalarAsync())!;
        }
        _baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);
        var (subject, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "MCP presets");
        _session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subject);

        await SeedProfileAsync();
        await SeedAsync(tenantId);
    }

    [Fact]
    public async Task Show_agp_draws_the_figures_the_hourly_bands_and_the_band_bar()
    {
        using var client = McpClient(await McpTokenAsync("glucose.read treatments.read"));

        var call = await CallToolAsync(client, "show_agp", new { days = 14 });

        call.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
        var text = ToolText(call);
        Log(text);
        var panels = call.GetProperty("result").GetProperty("structuredContent").GetProperty("panels");
        panels.EnumerateArray().Select(p => (p.GetProperty("id").GetString(), p.GetProperty("primitive").GetString(), p.GetProperty("status").GetString()))
            .Should().Equal(("figures", "stat_tiles", "ok"), ("profile", "range_bands", "ok"), ("ranges", "bar", "ok"));
        panels[0].GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).Should().Contain(["tdd", "carbsPerDay"]);
        panels[1].GetProperty("rows").GetArrayLength().Should().Be(24);
        var ranges = panels[2];
        ranges.GetProperty("bindings").GetProperty("orientation").GetString().Should().Be("horizontal");
        ranges.GetProperty("bindings").GetProperty("stacked").GetBoolean().Should().BeTrue();
        ranges.GetProperty("bindings").GetProperty("y").EnumerateArray().Select(y => y.GetString())
            .Should().Equal("veryLow", "low", "inRange", "high", "veryHigh");
        var bands = ranges.GetProperty("rows").EnumerateArray().Should().ContainSingle().Subject;
        new[] { "veryLow", "low", "inRange", "high", "veryHigh" }.Sum(k => bands.GetProperty(k).GetDouble()).Should().BeApproximately(100, 0.5);
        ranges.GetProperty("fields").EnumerateArray().Where(f => f.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String)
            .Select(f => f.GetProperty("color").GetString())
            .Should().Equal("glucose-very-low", "glucose-low", "glucose-in-range", "glucose-high", "glucose-very-high");
        text.Should().Contain("Ambulatory glucose profile, last 14 days").And.Contain("[ranges] Time in each glucose range")
            .And.Contain("Insulin per day").And.NotContain("Fields omitted");
    }

    [Fact]
    public async Task A_glucose_only_token_gets_the_agp_without_the_insulin_tiles_and_the_summary_says_so()
    {
        using var client = McpClient(await McpTokenAsync("glucose.read"));

        var call = await CallToolAsync(client, "show_agp", new { days = 14 });

        call.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
        var figures = call.GetProperty("result").GetProperty("structuredContent").GetProperty("panels")[0];
        figures.GetProperty("status").GetString().Should().Be("ok");
        figures.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString())
            .Should().Equal("timeInRangePercent", "mean", "gmi", "cv", "timeBelow", "timeAbove");
        figures.GetProperty("rows")[0].TryGetProperty("tdd", out _).Should().BeFalse();
        ToolText(call).Should().Contain("Fields omitted: tdd, basalPercent, carbsPerDay (scope not granted).").And.NotContain("Insulin per day");
    }

    [Fact]
    public async Task Compare_periods_draws_both_periods_figures_with_the_change_and_degrades_without_treatments()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        string Day(int back) => today.AddDays(-back).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var arguments = new { aFrom = Day(7), aTo = Day(4), bFrom = Day(3), bTo = Day(0) };

        using var full = McpClient(await McpTokenAsync("glucose.read treatments.read"));
        var call = await CallToolAsync(full, "compare_periods", arguments);
        call.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
        var text = ToolText(call);
        Log(text);
        var panel = call.GetProperty("result").GetProperty("structuredContent").GetProperty("panels")[0];
        panel.GetProperty("status").GetString().Should().Be("ok");
        panel.GetProperty("primitive").GetString().Should().Be("comparison");
        var rows = panel.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("metric").GetString()!);
        rows["inRange"].GetProperty("a").ValueKind.Should().Be(JsonValueKind.Number);
        rows["inRange"].GetProperty("b").ValueKind.Should().Be(JsonValueKind.Number);
        rows["inRange"].GetProperty("color").GetString().Should().Be("glucose-in-range");
        rows["insulinPerDay"].GetProperty("a").GetDouble().Should().BeGreaterThan(0);
        rows.Keys.Should().Contain(["gmi", "cv", "mean", "carbsPerDay"]);
        panel.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "a").GetProperty("label").GetString()
            .Should().Be($"Period A, {Day(7)} to {Day(4)}");
        panel.GetProperty("link").GetString().Should().Contain("/reports/comparison?preset=custom&aFrom=" + Day(7));
        text.Should().Contain($"Period comparison: A is Period A, {Day(7)} to {Day(4)} (4 days)").And.Contain("In range: ");

        using var glucoseOnly = McpClient(await McpTokenAsync("glucose.read"));
        var degraded = await CallToolAsync(glucoseOnly, "compare_periods", new { preset = "last7-prior7" });
        degraded.TryGetProperty("error", out var degradedError).Should().BeFalse(degradedError.ToString());
        var degradedRows = degraded.GetProperty("result").GetProperty("structuredContent").GetProperty("panels")[0].GetProperty("rows")
            .EnumerateArray().Select(r => r.GetProperty("metric").GetString()).ToList();
        degradedRows.Should().Contain("inRange").And.NotContain("insulinPerDay").And.NotContain("carbsPerDay");
        ToolText(degraded).Should().Contain("Insulin and carb figures are not shown: they need the treatments.read scope");

        var refused = await CallToolAsync(full, "compare_periods", new { preset = "lastYear" });
        refused.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(refused).Should().Contain("preset: ");
    }

    [Fact]
    public async Task Loading_earlier_data_past_a_charts_limit_cuts_the_range_and_marks_the_panel_at_its_limit()
    {
        using var client = McpClient(await McpTokenAsync("glucose.read treatments.read"));
        var call = await CallToolAsync(client, "render_report", new
        {
            spec = new { version = 1, panels = new object[] { new { id = "chart", dataset = new { name = "glucose_series" }, primitive = "glucose_chart", range = new { hours = 24 } } } },
        });
        call.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
        var result = call.GetProperty("result").GetProperty("structuredContent");
        result.GetProperty("panels")[0].GetProperty("atRangeLimit").GetBoolean().Should().BeFalse();

        // The view's "Load earlier data" doubles the window backwards each press: 2, 4, then 8 days asked for.
        for (var press = 1; press <= 3; press++)
        {
            var window = result.GetProperty("panels")[0].GetProperty("window");
            var from = window.GetProperty("from").GetInt64();
            var to = window.GetProperty("to").GetInt64();
            var refined = await CallToolAsync(client, "refine_report", new
            {
                spec = result.GetProperty("spec"),
                patch = new
                {
                    panels = new[]
                    {
                        new
                        {
                            id = "chart",
                            range = new
                            {
                                from = DateTimeOffset.FromUnixTimeMilliseconds(from - (to - from)).ToString("O"),
                                to = DateTimeOffset.FromUnixTimeMilliseconds(to).ToString("O"),
                            },
                        },
                    },
                },
            });
            refined.TryGetProperty("error", out var refineError).Should().BeFalse(refineError.ToString());
            var refinedResult = refined.GetProperty("result");
            refinedResult.TryGetProperty("isError", out _).Should().BeFalse(ToolText(refined));
            result = refinedResult.GetProperty("structuredContent");
        }

        var panel = result.GetProperty("panels")[0];
        panel.GetProperty("status").GetString().Should().Be("ok");
        panel.GetProperty("atRangeLimit").GetBoolean().Should().BeTrue();
        panel.GetProperty("rangeLimit").GetString().Should().Be("This chart covers at most 3 days.");
        panel.GetProperty("summary").GetString().Should().Contain("Cut to the latest 3 days: the range asked for was longer.");
        var span = panel.GetProperty("window").GetProperty("to").GetInt64() - panel.GetProperty("window").GetProperty("from").GetInt64();
        span.Should().Be((long)TimeSpan.FromDays(3).TotalMilliseconds);
    }

    [Fact]
    public async Task Show_treatments_draws_markers_on_the_line_and_the_records_in_a_table()
    {
        using var client = McpClient(await McpTokenAsync("glucose.read treatments.read"));

        var call = await CallToolAsync(client, "show_treatments", new { hours = 72 });

        call.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
        var text = ToolText(call);
        Log(text);
        var panels = call.GetProperty("result").GetProperty("structuredContent").GetProperty("panels");
        var markers = panels[0].GetProperty("markers").EnumerateArray().ToList();
        markers.Should().HaveCountGreaterThanOrEqualTo(4, "two or three lunches in 72 hours, each with a bolus and carbs");
        markers.Select(m => m.GetProperty("kind").GetString()).Distinct().Should().BeEquivalentTo("bolus", "carbs");
        var rows = panels[1].GetProperty("rows").EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("type").GetString()).Should().Contain(["bolus", "carbs", "note"]);
        text.Should().Contain("Markers drawn:").And.Contain("Carb entries:").And.Contain("Notes: 1.").And.NotContain("Felt fine");
    }

    [Fact]
    public async Task Show_treatments_without_glucose_read_still_draws_the_table()
    {
        using var client = McpClient(await McpTokenAsync("treatments.read"));

        var call = await CallToolAsync(client, "show_treatments", new { hours = 72 });

        var panels = call.GetProperty("result").GetProperty("structuredContent").GetProperty("panels");
        panels[0].GetProperty("status").GetString().Should().Be("not_permitted");
        panels[1].GetProperty("status").GetString().Should().Be("ok");
        ToolText(call).Should().Contain("'glucose.read'");
    }

    [Fact]
    public async Task The_text_tools_answer_in_words_under_8_kB()
    {
        using var client = McpClient(await McpTokenAsync("glucose.read treatments.read devices.read therapy.read"));

        var statistics = await CallToolAsync(client, "get_glucose_statistics", new { days = 7 });
        statistics.GetProperty("result").TryGetProperty("structuredContent", out var structured).Should()
            .BeFalse(structured.ValueKind == JsonValueKind.Undefined ? "" : structured.ToString());
        ToolText(statistics).Should().Contain("Glucose statistics from").And.Contain("Time in each band:");

        var treatments = ToolText(await CallToolAsync(client, "get_recent_treatments", new { hours = 72, types = new[] { "carbs" } }));
        treatments.Should().Contain("Carb entries:").And.NotContain("Boluses");

        var profile = ToolText(await CallToolAsync(client, "get_profile_summary", new { }));
        profile.Should().Contain("Profile 'Default'").And.Contain("Basal (U/h): 00:00 1");

        var loop = ToolText(await CallToolAsync(client, "get_loop_health", new { days = 7 }));
        loop.Should().Contain("Days analyzed:");

        foreach (var text in new[] { ToolText(statistics), treatments, profile, loop })
            Encoding.UTF8.GetByteCount(text).Should().BeLessThan(8 * 1024);

        var refused = await CallToolAsync(client, "get_recent_treatments", new { hours = 24, types = new[] { "insulin" } });
        refused.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ToolText(refused).Should().Contain("types[0]: Send one of: bolus, carbs");
    }

    [Fact]
    public async Task Without_therapy_read_the_profile_summary_says_so_and_shows_nothing()
    {
        using var client = McpClient(await McpTokenAsync("glucose.read"));

        ToolText(await CallToolAsync(client, "get_profile_summary", new { }))
            .Should().Be("The profile is not shown: it needs the therapy.read scope, which this token does not hold.");
    }

    [Fact]
    public async Task Each_preset_and_text_tool_writes_one_audit_row_naming_what_it_read_once()
    {
        var since = DateTime.UtcNow;
        await SetReadAuditAsync(true);
        try
        {
            using var client = McpClient(await McpTokenAsync("glucose.read treatments.read devices.read therapy.read"));
            var agp = await CallToolAsync(client, "show_agp", new { days = 14 });
            var readings = agp.GetProperty("result").GetProperty("structuredContent").GetProperty("panels")[0].GetProperty("rows")[0];
            readings.TryGetProperty("tdd", out _).Should().BeTrue();
            await CallToolAsync(client, "show_treatments", new { hours = 24 });
            await CallToolAsync(client, "get_glucose_statistics", new { days = 14 });
            await CallToolAsync(client, "get_recent_treatments", new { hours = 48 });
            await CallToolAsync(client, "get_profile_summary", new { });
            await CallToolAsync(client, "get_loop_health", new { days = 7 });

            var glucoseReadings = await GlucoseRowsAsync(DateTime.UtcNow.AddDays(-14));
            var rows = await AuditAsync(since, 6);
            rows.Should().HaveCount(6);
            rows["MCP show_agp"].Entities.Should().Be("glucose,treatments");
            rows["MCP show_agp"].Count.Should().BeInRange(glucoseReadings, glucoseReadings + 100,
                "the three glucose panels share one read, counted once, beside the insulin and carb records");
            rows["MCP show_treatments"].Entities.Should().Be("glucose,treatments");
            rows["MCP get_glucose_statistics"].Entities.Should().Be("glucose,treatments");
            rows["MCP get_glucose_statistics"].Count.Should().BeInRange(glucoseReadings, glucoseReadings + 100);
            rows["MCP get_recent_treatments"].Entities.Should().Be("treatments");
            rows["MCP get_recent_treatments"].Count.Should().BeGreaterThan(0);
            rows["MCP get_profile_summary"].Entities.Should().Be("profiles");
            rows["MCP get_loop_health"].Entities.Should().Be("glucose,treatments,device_status,profiles");
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

    private async Task<int> GlucoseRowsAsync(DateTime since)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT set_config('app.current_tenant_id', @t, false);
            SELECT count(*)::int FROM sensor_glucose WHERE timestamp >= @since;
            """;
        cmd.Parameters.AddWithValue("t", _tenantId.ToString());
        cmd.Parameters.AddWithValue("since", since);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.NextResultAsync();
        await reader.ReadAsync();
        return reader.GetInt32(0);
    }

    /// <summary>The MCP read-audit rows since <paramref name="since"/>, by endpoint, once <paramref name="expected"/> are written.</summary>
    private async Task<Dictionary<string, (string? Entities, int Count)>> AuditAsync(DateTime since, int expected)
    {
        var rows = new Dictionary<string, (string? Entities, int Count)>();
        for (var attempt = 0; attempt < 50 && rows.Count < expected; attempt++)
        {
            await Task.Delay(100);
            rows.Clear();
            await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @t, false);
                SELECT endpoint, entity_type, record_count FROM read_access_log
                WHERE endpoint LIKE 'MCP %' AND created_at >= @since ORDER BY created_at;
                """;
            cmd.Parameters.AddWithValue("t", _tenantId.ToString());
            cmd.Parameters.AddWithValue("since", since);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            while (await reader.ReadAsync())
                rows[reader.GetString(0)] = (reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
        }
        return rows;
    }

    private async Task SeedProfileAsync()
    {
        static object[] Values(double value) => [new { time = "00:00", value }];
        var response = await AuthenticatedClient.PostAsJsonAsync("/api/v1/profile", new
        {
            defaultProfile = "Default",
            startDate = "2024-01-01T00:00:00.000Z",
            mills = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            units = "mg/dl",
            store = new Dictionary<string, object>
            {
                ["Default"] = new
                {
                    dia = 4, timezone = "UTC", units = "mg/dl", basal = Values(1.0), carbratio = Values(10), sens = Values(50),
                    target_low = Values(90), target_high = Values(140),
                },
            },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Eight days of glucose from 60 to 260 and back; a 45 g lunch with 4.5 U at 06:00 UTC daily; one note.</summary>
    private async Task SeedAsync(Guid tenantId)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(new TenantContext(tenantId, _slug, "MCP", true, false));
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();

        var today = DateTime.UtcNow.Date;
        var i = 0;
        for (var t = today.AddDays(-8); t < DateTime.UtcNow; t = t.AddMinutes(5), i++)
            context.SensorGlucose.Add(new SensorGlucoseEntity { Id = Guid.NewGuid(), Timestamp = t, Mgdl = 60 + Math.Abs(i % 80 - 40) * 5, DataSource = "test" });
        for (var d = 0; d <= 7; d++)
        {
            var lunch = today.AddDays(-d).AddHours(6);
            if (lunch > DateTime.UtcNow)
                continue;
            context.CarbIntakes.Add(new CarbIntakeEntity { Id = Guid.NewGuid(), Timestamp = lunch, Carbs = 45, DataSource = "test" });
            context.Boluses.Add(new BolusEntity { Id = Guid.NewGuid(), Timestamp = lunch, Insulin = 4.5, BolusKind = "Manual", DataSource = "test" });
        }
        context.Notes.Add(new NoteEntity { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow.AddHours(-2), Text = "Felt fine", DataSource = "test" });
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
        string.Join("\n", rpc.GetProperty("result").GetProperty("content").EnumerateArray().Select(c => c.GetProperty("text").GetString()));

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
