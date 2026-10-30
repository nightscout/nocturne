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
/// The Loop Analysis datasets over <c>/mcp</c>: a week of lunches, two with penne and one with rice
/// on record and the rest carbs only, read through <c>find_foods</c>, an <c>event_response</c>
/// panel filtered to penne, and <c>refine_report</c>.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class McpLoopAnalysisIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private string _slug = null!;
    private string _baseDomain = null!;
    private string _session = null!;
    private Guid _penne;
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
        var (subject, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "MCP loop analysis");
        _session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subject);

        await SeedProfileAsync();
        _penne = await SeedLunchesAsync(tenantId);
    }

    [Fact]
    public async Task An_event_response_panel_filtered_to_a_food_counts_carbs_only_meals_apart()
    {
        using var client = McpClient(await McpTokenAsync("glucose.read treatments.read food.read"));

        var foods = await CallToolAsync(client, "find_foods", new { query = "pen" });
        ToolText(foods).Should().Contain($"Penne (id {_penne}, category Pasta): 2 carb entries in the last 90 days.");

        var spec = new
        {
            version = 1,
            range = new { hours = 9 * 24 },
            panels = new object[]
            {
                new
                {
                    id = "bands",
                    dataset = new { name = "event_response", cohorts = new object[] { new { anchor = "Meal", foods = new[] { _penne } }, new { anchor = "Meal" } } },
                    primitive = "range_bands",
                },
                new { id = "events", dataset = new { name = "event_response", cohorts = new object[] { new { anchor = "Meal", foods = new[] { _penne } } } }, primitive = "table" },
            },
        };
        var call = await CallToolAsync(client, "render_report", new { spec });
        call.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
        var text = ToolText(call);
        Log(text);

        text.Should().Contain("Cohort 1: Meal, foods Penne: 2 events.");
        text.Should().Contain("Food filter: 3 meals carried a food record, and 2 of them matched it. 4 meals had only carbs, with no food record, so the filter could not apply to them and they are not counted.");
        text.Should().Contain("Cohort 2: Meal: 7 events.");

        var structured = call.GetProperty("result").GetProperty("structuredContent");
        var bands = structured.GetProperty("panels")[0];
        bands.GetProperty("status").GetString().Should().Be("ok");
        bands.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("series").GetString()).Distinct()
            .Should().Equal("Cohort 1: Meal, foods Penne", "Cohort 2: Meal");
        bands.GetProperty("link").GetString().Should().Contain("/reports/loop-analysis/meals-and-events?").And.Contain($"foodIds1={_penne}");
        var events = structured.GetProperty("panels")[1].GetProperty("rows");
        events.GetArrayLength().Should().Be(2);
        events[0].GetProperty("summary").GetString().Should().StartWith("Meal of 45 g on ");

        var refined = await CallToolAsync(client, "refine_report", new
        {
            spec = structured.GetProperty("spec"),
            patch = new { panels = new[] { new { id = "events", dataset = new { cohorts = new[] { new { anchor = "Meal" } } } } } },
        });
        refined.TryGetProperty("error", out var refineError).Should().BeFalse(refineError.ToString());
        refined.GetProperty("result").GetProperty("structuredContent").GetProperty("panels")[1].GetProperty("rows").GetArrayLength()
            .Should().Be(7, "the refined spec's events panel names every meal");
    }

    [Fact]
    public async Task Without_food_read_a_food_filtered_cohort_is_not_shown_and_the_summary_says_why()
    {
        using var client = McpClient(await McpTokenAsync("glucose.read treatments.read"));

        var call = await CallToolAsync(client, "render_report", new
        {
            spec = new
            {
                version = 1,
                range = new { hours = 9 * 24 },
                panels = new object[]
                {
                    new { dataset = new { name = "event_response", cohorts = new object[] { new { anchor = "Meal", foods = new[] { _penne } }, new { anchor = "Meal" } } }, primitive = "range_bands" },
                },
            },
        });

        var text = ToolText(call);
        text.Should().Contain("Cohort 1 is not shown: its food filter needs the food.read scope");
        text.Should().Contain("Cohort 2: Meal: 7 events.");
        var refused = await CallToolAsync(client, "find_foods", new { query = "pen" });
        refused.TryGetProperty("error", out var scopeError).Should().BeTrue();
        scopeError.GetProperty("message").GetString().Should().Contain("food.read");
    }

    [Fact]
    public async Task A_single_food_filtered_cohort_without_food_read_is_an_empty_panel_not_every_meal_or_an_error()
    {
        using var client = McpClient(await McpTokenAsync("glucose.read treatments.read"));

        var call = await CallToolAsync(client, "render_report", new
        {
            spec = new
            {
                version = 1,
                range = new { hours = 9 * 24 },
                panels = new object[]
                {
                    new { id = "bands", dataset = new { name = "event_response", cohorts = new object[] { new { anchor = "Meal", foods = new[] { _penne } } } }, primitive = "range_bands" },
                    new { id = "events", dataset = new { name = "event_response", cohorts = new object[] { new { anchor = "Meal", foodCategory = "Pasta" } } }, primitive = "table" },
                },
            },
        });

        call.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
        call.GetProperty("result").TryGetProperty("isError", out var isError).Should().BeFalse(isError.ToString());
        var text = ToolText(call);
        text.Should().Contain("Cohort 1 is not shown: its food filter needs the food.read scope");
        text.Should().Contain("This token's scopes show no cohort.");
        text.Should().NotContain("events.", "no cohort ran, so nothing is counted");
        foreach (var panel in call.GetProperty("result").GetProperty("structuredContent").GetProperty("panels").EnumerateArray())
        {
            panel.GetProperty("status").GetString().Should().Be("ok");
            panel.GetProperty("rows").GetArrayLength().Should().Be(0, "the food-filtered cohort is never widened to every meal");
        }
    }

    [Fact]
    public async Task Find_foods_counts_carb_entries_only_for_a_token_that_reads_treatments()
    {
        var since = DateTime.UtcNow;
        await SetReadAuditAsync(true);
        try
        {
            await FindFoodsWithAndWithoutTreatmentsAsync(since);
        }
        finally
        {
            await SetReadAuditAsync(false);
        }
    }

    private async Task FindFoodsWithAndWithoutTreatmentsAsync(DateTime since)
    {

        using (var foodOnly = McpClient(await McpTokenAsync("food.read")))
        {
            var call = await CallToolAsync(foodOnly, "find_foods", new { query = "pen" });
            var text = ToolText(call);
            text.Should().Contain($"Penne (id {_penne}, category Pasta).");
            text.Should().Contain("Carb entries are not counted: they need the treatments.read scope");
            text.Should().NotContain("carb entr", "no count of meals reaches a token without treatments.read")
                .And.NotContain("90 days");
            var food = call.GetProperty("result").GetProperty("structuredContent").GetProperty("foods")[0];
            food.GetProperty("carbEntries").ValueKind.Should().Be(JsonValueKind.Null);
        }

        using (var withTreatments = McpClient(await McpTokenAsync("food.read treatments.read")))
            ToolText(await CallToolAsync(withTreatments, "find_foods", new { query = "pen" }))
                .Should().Contain("Penne (id " + _penne + ", category Pasta): 2 carb entries in the last 90 days.");

        var rows = await AuditEntitiesAsync("MCP find_foods", since, 2);
        rows.Should().Equal(["foods", "foods,treatments"], "the counts read carb entries, the food.read-only call did not");
    }

    private async Task SetReadAuditAsync(bool enabled)
    {
        using var session = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slug, _baseDomain, _session);
        var response = await session.PutAsJsonAsync("/api/v4/audit/config", new { readAuditEnabled = enabled });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The read-audit entity types of a tool's calls since <paramref name="since"/>, oldest first, once
    /// <paramref name="expected"/> rows are written.
    /// </summary>
    private async Task<List<string?>> AuditEntitiesAsync(string endpoint, DateTime since, int expected)
    {
        var rows = new List<string?>();
        for (var attempt = 0; attempt < 50 && rows.Count < expected; attempt++)
        {
            await Task.Delay(100);
            rows.Clear();
            await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @t, false);
                SELECT entity_type FROM read_access_log WHERE endpoint = @e AND created_at >= @since ORDER BY created_at;
                """;
            cmd.Parameters.AddWithValue("t", _tenantId.ToString());
            cmd.Parameters.AddWithValue("e", endpoint);
            cmd.Parameters.AddWithValue("since", since);
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            while (await reader.ReadAsync())
                rows.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }
        return rows;
    }

    [Fact]
    public async Task The_other_loop_analysis_datasets_render_with_their_summaries()
    {
        using var client = McpClient(await McpTokenAsync("glucose.read treatments.read therapy.read"));

        var call = await CallToolAsync(client, "render_report", new
        {
            spec = new
            {
                version = 1,
                range = new { hours = 9 * 24 },
                panels = new object[]
                {
                    new { dataset = new { name = "daily_profile" }, primitive = "range_bands" },
                    new { dataset = new { name = "glucose_distribution_by_hour" }, primitive = "range_bands" },
                    new { dataset = new { name = "settings_evidence" }, primitive = "table" },
                },
            },
        });

        call.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
        var panels = call.GetProperty("result").GetProperty("structuredContent").GetProperty("panels");
        panels[0].GetProperty("rows").GetArrayLength().Should().Be(288);
        panels[1].GetProperty("rows").GetArrayLength().Should().Be(24);
        panels.EnumerateArray().Select(p => p.GetProperty("status").GetString()).Should().OnlyContain(s => s == "ok");
        var text = ToolText(call);
        Log(text);
        text.Should().Contain("Daily profile over local days").And.Contain("Glucose by hour of day").And.Contain("next to the profile's");
    }

    [Fact]
    public async Task A_clamped_member_asking_for_six_hours_of_events_is_served_those_six_hours()
    {
        Guid subject;
        await using (var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync()))
        {
            await conn.OpenAsync();
            (subject, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "MCP loop clamped");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE tenant_members SET limit_to_24_hours = true, direct_permissions = '["glucose.read","treatments.read"]'::jsonb
                WHERE subject_id = @subject;
                """;
            cmd.Parameters.AddWithValue("subject", subject);
            (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
        }
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subject);
        using var client = McpClient(await McpTokenAsync("glucose.read treatments.read", session));

        var started = DateTimeOffset.UtcNow;
        var call = await CallToolAsync(client, "render_report", new
        {
            spec = new
            {
                version = 1,
                range = new { hours = 6 },
                panels = new object[] { new { dataset = new { name = "event_response", cohorts = new object[] { new { anchor = "Meal" } } }, primitive = "table" } },
            },
        });

        call.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
        var text = ToolText(call);
        Log(text);
        var panel = call.GetProperty("result").GetProperty("structuredContent").GetProperty("panels")[0];
        panel.GetProperty("status").GetString().Should().Be("ok");
        var window = panel.GetProperty("window");
        DateTimeOffset.FromUnixTimeMilliseconds(window.GetProperty("from").GetInt64())
            .Should().BeCloseTo(started.AddHours(-6), TimeSpan.FromMinutes(1), "the six hours asked for, not the whole 24-hour window");
        DateTimeOffset.FromUnixTimeMilliseconds(window.GetProperty("to").GetInt64()).Should().BeCloseTo(started, TimeSpan.FromMinutes(1));
        text.Should().Contain("only the part of the range in that window").And.NotContain("covers that window");
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

    /// <summary>
    /// Eight days of glucose at 110, rising 80 an hour after a 45 g lunch with a 4.5 U bolus at 06:00
    /// UTC on each of the last seven days; penne on two lunches, rice on one, the rest carbs only.
    /// </summary>
    private async Task<Guid> SeedLunchesAsync(Guid tenantId)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(new TenantContext(tenantId, _slug, "MCP", true, false));
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();

        var today = DateTime.UtcNow.Date;
        var lunches = Enumerable.Range(1, 7).Select(d => today.AddDays(-d).AddHours(6)).ToList();
        for (var t = today.AddDays(-8); t < DateTime.UtcNow; t = t.AddMinutes(5))
        {
            var since = lunches.Select(l => (t - l).TotalMinutes).Where(m => m is >= 0 and <= 180).DefaultIfEmpty(-1).Max();
            var mgdl = since < 0 ? 110 : since <= 60 ? 110 + since * 80 / 60 : 190 - (since - 60) * 80 / 120;
            context.SensorGlucose.Add(new SensorGlucoseEntity { Id = Guid.NewGuid(), Timestamp = t, Mgdl = mgdl, DataSource = "test" });
        }

        var penne = new FoodEntity { Id = Guid.NewGuid(), Name = "Penne", Category = "Pasta", Carbs = 30, Portion = 100 };
        var rice = new FoodEntity { Id = Guid.NewGuid(), Name = "Rice", Category = "Grains", Carbs = 28, Portion = 100 };
        context.Foods.AddRange(penne, rice);
        for (var i = 0; i < lunches.Count; i++)
        {
            var intake = new CarbIntakeEntity { Id = Guid.NewGuid(), Timestamp = lunches[i], Carbs = 45, DataSource = "test" };
            context.CarbIntakes.Add(intake);
            context.Boluses.Add(new BolusEntity { Id = Guid.NewGuid(), Timestamp = lunches[i], Insulin = 4.5, BolusKind = "Manual", DataSource = "test" });
            Guid? food = i switch { 0 or 1 => penne.Id, 2 => rice.Id, _ => null };
            if (food is not null)
                context.TreatmentFoods.Add(new TreatmentFoodEntity { Id = Guid.NewGuid(), CarbIntakeId = intake.Id, FoodId = food, Carbs = 45, Portions = 1 });
        }
        await context.SaveChangesAsync();
        return penne.Id;
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
    private async Task<string> McpTokenAsync(string scope, string? sessionToken = null)
    {
        var sessionToUse = sessionToken ?? _session;
        const string redirectUri = "http://localhost:9999/callback";
        var resource = $"http://{_slug}.{_baseDomain}/mcp";
        using var session = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slug, _baseDomain, sessionToUse);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(session, scope: scope);
        var (verifier, challenge) = AuthTestHelpers.GeneratePkceChallenge();

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var noRedirect = Fixture.CreateHttpClient(handler);
        noRedirect.DefaultRequestHeaders.Host = $"{_slug}.{_baseDomain}";
        noRedirect.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToUse);
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
