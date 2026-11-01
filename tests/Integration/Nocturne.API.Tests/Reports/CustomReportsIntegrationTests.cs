using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Reports;

/// <summary>
/// <c>api/v4/reports/custom</c> end to end: saved specs stay in their tenant, shares and delegated
/// credentials cannot reach or change them, a run reads as the caller, and a spec that no longer
/// validates says why.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class CustomReportsIntegrationTests : ApiIntegrationTestBase
{
    private const string Route = "/api/v4/reports/custom";

    private string _baseDomain = null!;
    private string _slugA = null!;
    private string _slugB = null!;
    private Guid _tenantAId;
    private string _sessionA = null!;
    private string _sessionB = null!;

    public CustomReportsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
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

        _slugB = $"rep-b-{Guid.NewGuid():N}"[..18];
        var tenantBId = await AuthTestHelpers.SeedTenantAsync(Fixture, _slugB, "Reports Tenant B");
        _baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);

        var (subjectA, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantAId, "Reports User A");
        var (subjectB, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantBId, "Reports User B");
        _sessionA = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectA);
        _sessionB = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectB);
    }

    private static object Spec(string title = "Glucose") => new
    {
        version = 1,
        title,
        panels = new object[]
        {
            new
            {
                kind = "chart",
                range = new { hours = 6 },
                tracks = new[] { new { layers = new[] { new { dataset = new { name = "glucose_series" }, mark = "line", bindings = new { y = new[] { "glucose" } } } } } },
            },
            new { id = "stats", kind = "stat_tiles", dataset = new { name = "glucose_statistics" }, range = new { hours = 336 } },
        },
    };

    private HttpClient Session(string slug, string session) =>
        AuthTestHelpers.CreateTenantBearerClient(Fixture, slug, _baseDomain, session);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, body);
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task PostGlucoseAsync(string slug, string session, int sgv, TimeSpan age)
    {
        using var client = Session(slug, session);
        var at = DateTimeOffset.UtcNow - age;
        var response = await client.PostAsJsonAsync("/api/v1/entries", new[]
        {
            new { type = "sgv", sgv, direction = "Flat", date = at.ToUnixTimeMilliseconds(), dateString = at.UtcDateTime.ToString("o") },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Saved_reports_are_created_listed_read_renamed_and_deleted_within_their_tenant_only()
    {
        using var a = Session(_slugA, _sessionA);
        using var b = Session(_slugB, _sessionB);

        var created = await ReadAsync(await a.PostAsJsonAsync(Route, new { name = " Morning ", spec = Spec() }), HttpStatusCode.OK);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("name").GetString().Should().Be("Morning");
        created.GetProperty("spec").GetProperty("panels")[0].GetProperty("id").GetString().Should().Be("p1", "the stored spec is normalised");

        // The same name in another tenant is another report.
        await ReadAsync(await b.PostAsJsonAsync(Route, new { name = "Morning", spec = Spec("B") }), HttpStatusCode.OK);
        var duplicate = await ReadAsync(await a.PostAsJsonAsync(Route, new { name = "Morning", spec = Spec() }), HttpStatusCode.Conflict);
        duplicate.GetProperty("errors").GetProperty("name")[0].GetString().Should().Contain("Another custom report has this name");

        var listA = await ReadAsync(await a.GetAsync(Route), HttpStatusCode.OK);
        listA.EnumerateArray().Should().ContainSingle();
        listA[0].GetProperty("id").GetGuid().Should().Be(id);
        listA[0].GetProperty("panelCount").GetInt32().Should().Be(2);

        var listB = await ReadAsync(await b.GetAsync(Route), HttpStatusCode.OK);
        listB.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).Should().NotContain(id);

        // Another tenant's id reads, renames and deletes nothing.
        (await b.GetAsync($"{Route}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await b.PutAsJsonAsync($"{Route}/{id}", new { name = "Taken over" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await b.DeleteAsync($"{Route}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var other = await ReadAsync(await a.PostAsJsonAsync(Route, new { name = "Evenings", spec = Spec() }), HttpStatusCode.OK);
        var clash = await ReadAsync(await a.PutAsJsonAsync($"{Route}/{id}", new { name = "Evenings" }), HttpStatusCode.Conflict);
        clash.GetProperty("errors").GetProperty("name")[0].GetString().Should().Contain("Another custom report has this name");
        (await a.DeleteAsync($"{Route}/{other.GetProperty("id").GetGuid()}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var renamed = await ReadAsync(await a.PutAsJsonAsync($"{Route}/{id}", new { name = "Mornings" }), HttpStatusCode.OK);
        renamed.GetProperty("name").GetString().Should().Be("Mornings");
        var read = await ReadAsync(await a.GetAsync($"{Route}/{id}"), HttpStatusCode.OK);
        read.GetProperty("name").GetString().Should().Be("Mornings");
        read.GetProperty("errors").GetArrayLength().Should().Be(0);
        read.GetProperty("spec").GetProperty("title").GetString().Should().Be("Glucose");

        (await a.DeleteAsync($"{Route}/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await a.GetAsync($"{Route}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync(await a.GetAsync(Route), HttpStatusCode.OK)).GetArrayLength().Should().Be(0);

        // The name of a deleted report is free again.
        await ReadAsync(await a.PostAsJsonAsync(Route, new { name = "Mornings", spec = Spec() }), HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_invalid_spec_is_not_saved_and_the_errors_name_the_panel()
    {
        using var a = Session(_slugA, _sessionA);

        var refused = await ReadAsync(await a.PostAsJsonAsync(Route, new
        {
            name = "Broken",
            spec = new { version = 1, panels = new object[] { new { kind = "table", dataset = new { name = "insulin" } } } },
        }), HttpStatusCode.BadRequest);

        refused.GetProperty("errors").GetProperty("panels[0].dataset.name")[0].GetString().Should().Contain("'insulin' is not a dataset");

        var noSpec = await ReadAsync(await a.PostAsJsonAsync(Route, new { name = "No spec" }), HttpStatusCode.BadRequest);
        noSpec.GetProperty("errors").GetProperty("spec")[0].GetString().Should().Be("Send a spec.");
        (await ReadAsync(await a.GetAsync(Route), HttpStatusCode.OK)).EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).Should().NotContain("Broken");
    }

    [Fact]
    public async Task A_share_host_and_a_delegated_credential_cannot_change_saved_reports()
    {
        string shareHost;
        using (var scope = Fixture.Services.CreateScope())
        {
            var link = await scope.ServiceProvider.GetRequiredService<IShareLinkService>().RotateAsync(_tenantAId);
            shareHost = new Uri(link.Url!).Host;
        }

        using var share = Fixture.CreateHttpClient("nocturne-api", "http");
        share.DefaultRequestHeaders.Host = $"{shareHost.Split('.')[0]}.share.{_baseDomain}";
        (await share.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await share.PostAsJsonAsync($"{Route}/run", new { spec = Spec() })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await share.PostAsJsonAsync(Route, new { name = "Share", spec = Spec() }))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        // The api-secret is a full-access direct grant: it may read reports, but saving is the member's own act.
        using var apiSecret = AuthTestHelpers.CreateTenantClient(Fixture, _slugA, _baseDomain);
        apiSecret.DefaultRequestHeaders.Add("api-secret", TestApiSecret);
        (await apiSecret.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await apiSecret.PostAsJsonAsync(Route, new { name = "Api", spec = Spec() })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_history_clamped_member_running_a_saved_spec_gets_only_the_last_day()
    {
        await PostGlucoseAsync(_slugA, _sessionA, 300, TimeSpan.FromDays(10));
        await PostGlucoseAsync(_slugA, _sessionA, 120, TimeSpan.FromMinutes(5));

        using var owner = Session(_slugA, _sessionA);
        var saved = await ReadAsync(await owner.PostAsJsonAsync(Route, new { name = "Two weeks", spec = Spec() }), HttpStatusCode.OK);

        Guid subject;
        await using (var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync()))
        {
            await conn.OpenAsync();
            (subject, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantAId, "Reports Clamped");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE tenant_members SET limit_to_24_hours = true, direct_permissions = '["glucose.read","reports.read"]'::jsonb
                WHERE subject_id = @subject;
                """;
            cmd.Parameters.AddWithValue("subject", subject);
            (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
        }
        using var clamped = Session(_slugA, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subject));

        (await clamped.PostAsJsonAsync(Route, new { name = "Clamped", spec = Spec() })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "a member limited to a day of the record does not add reports for everyone");

        var report = await ReadAsync(await clamped.GetAsync($"{Route}/{saved.GetProperty("id").GetGuid()}"), HttpStatusCode.OK);
        var started = DateTimeOffset.UtcNow;
        var run = await ReadAsync(await clamped.PostAsJsonAsync($"{Route}/run", new { spec = report.GetProperty("spec") }), HttpStatusCode.OK);

        run.GetProperty("errors").GetArrayLength().Should().Be(0);
        var stats = run.GetProperty("result").GetProperty("panels")[1];
        stats.GetProperty("status").GetString().Should().Be("ok");
        stats.GetProperty("limited").GetBoolean().Should().BeTrue();
        stats.GetProperty("rows")[0].GetProperty("readings").GetInt32().Should().Be(1, "the reading of 10 days ago is older than the clamp");
        DateTimeOffset.FromUnixTimeMilliseconds(stats.GetProperty("window").GetProperty("from").GetInt64())
            .Should().BeCloseTo(started.AddHours(-24), TimeSpan.FromMinutes(1));

        var unclamped = await ReadAsync(await owner.PostAsJsonAsync($"{Route}/run", new { spec = report.GetProperty("spec") }), HttpStatusCode.OK);
        unclamped.GetProperty("result").GetProperty("panels")[1].GetProperty("rows")[0].GetProperty("readings").GetInt32().Should().Be(2);

        // A report the clamped member made before the clamp: its creator may not replace what every member's copy reads.
        await using (var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @tenant, false);
                UPDATE report_specs SET created_by_subject_id = @subject WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("tenant", _tenantAId.ToString());
            cmd.Parameters.AddWithValue("subject", subject);
            cmd.Parameters.AddWithValue("id", saved.GetProperty("id").GetGuid());
            await cmd.ExecuteNonQueryAsync();
        }
        var own = await ReadAsync(await clamped.GetAsync($"{Route}/{saved.GetProperty("id").GetGuid()}"), HttpStatusCode.OK);
        own.GetProperty("canEdit").GetBoolean().Should().BeTrue("the clamped member made it");
        (await clamped.PutAsJsonAsync($"{Route}/{saved.GetProperty("id").GetGuid()}", new { spec = Spec("Replaced") })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "a clamped member does not change what every member's copy reads");
        (await clamped.PutAsJsonAsync($"{Route}/{saved.GetProperty("id").GetGuid()}", new { name = "Renamed" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_stored_spec_of_an_older_version_opens_with_the_validators_message()
    {
        var id = Guid.CreateVersion7();
        await using (var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @tenant, false);
                INSERT INTO report_specs (id, tenant_id, name, spec, version, created_at, updated_at)
                VALUES (@id, @tenantId, 'Old', '{"version":0,"panels":[{"kind":"table","dataset":{"name":"glucose_series"}}]}'::jsonb, 0, now(), now());
                """;
            cmd.Parameters.AddWithValue("tenant", _tenantAId.ToString());
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("tenantId", _tenantAId);
            await cmd.ExecuteNonQueryAsync();
        }

        using var a = Session(_slugA, _sessionA);
        var report = await ReadAsync(await a.GetAsync($"{Route}/{id}"), HttpStatusCode.OK);

        var error = report.GetProperty("errors").EnumerateArray().Should().ContainSingle().Subject;
        error.GetProperty("path").GetString().Should().Be("version");
        error.GetProperty("message").GetString().Should().Be("0 is not a supported version. Send the number 1.");
        report.GetProperty("spec").GetProperty("version").GetInt32().Should().Be(0, "a spec that does not validate is returned as stored");

        var run = await ReadAsync(await a.PostAsJsonAsync($"{Route}/run", new { spec = report.GetProperty("spec") }), HttpStatusCode.OK);
        run.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);
        run.GetProperty("errors")[0].GetProperty("path").GetString().Should().Be("version");
    }

    [Fact]
    public async Task A_run_writes_a_title_as_data_and_an_oversized_spec_is_refused()
    {
        await PostGlucoseAsync(_slugA, _sessionA, 140, TimeSpan.FromMinutes(5));
        using var a = Session(_slugA, _sessionA);

        var run = await ReadAsync(await a.PostAsJsonAsync($"{Route}/run", new { spec = Spec("<b>bold</b>") }), HttpStatusCode.OK);

        // A spec of over 8 kB, more than a gateway accepts in a request line, runs: the run is a POST.
        var cohort = new { anchor = "Meal", foods = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray() };
        var wide = new
        {
            version = 1,
            title = "Wide",
            panels = Enumerable.Range(0, 4).Select(i => new
            {
                id = $"meals{i}",
                kind = "table",
                dataset = new { name = "event_response", cohorts = new[] { cohort, cohort, cohort } },
                range = new { hours = 72 },
            }).ToArray(),
        };
        JsonSerializer.Serialize(wide).Length.Should().BeGreaterThan(8 * 1024);
        var wideRun = await ReadAsync(await a.PostAsJsonAsync($"{Route}/run", new { spec = wide }), HttpStatusCode.OK);
        wideRun.GetProperty("errors").GetArrayLength().Should().Be(0);
        wideRun.GetProperty("result").GetProperty("panels").GetArrayLength().Should().Be(4);
        run.GetProperty("errors").GetArrayLength().Should().Be(0);
        run.GetProperty("result").GetProperty("spec").GetProperty("title").GetString().Should().Be("<b>bold</b>", "a title is data; the page writes it as text");
        run.GetProperty("result").TryGetProperty("openUrl", out _).Should().BeFalse("a run carries no link that opens a spec");

        var over = await ReadAsync(await a.PostAsJsonAsync($"{Route}/run", new { spec = new { version = 1, title = new string('a', 17 * 1024), panels = Array.Empty<object>() } }), HttpStatusCode.OK);
        over.GetProperty("errors")[0].GetProperty("message").GetString().Should().Contain("at most 16 kB");

        var tooLong = await a.PostAsJsonAsync($"{Route}/run", new { spec = new { version = 1, title = new string('a', 64 * 1024), panels = Array.Empty<object>() } });
        tooLong.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, "the body is refused while it is read");
    }

    [Fact]
    public async Task A_run_writes_one_read_audit_row_with_the_categories_it_read()
    {
        await PostGlucoseAsync(_slugA, _sessionA, 140, TimeSpan.FromMinutes(5));
        using var a = Session(_slugA, _sessionA);
        var since = DateTime.UtcNow;
        (await a.PutAsJsonAsync("/api/v4/audit/config", new { readAuditEnabled = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        try
        {
            await ReadAsync(await a.PostAsJsonAsync($"{Route}/run", new { spec = Spec() }), HttpStatusCode.OK);
            await ReadAsync(await a.PostAsJsonAsync($"{Route}/run", new { spec = Spec(), preview = true }), HttpStatusCode.OK);

            var rows = new List<(string Endpoint, int Status, string? Entities)>();
            var settled = false;
            for (var attempt = 0; attempt < 60 && !settled; attempt++)
            {
                // Once a row is there, read once more after a pause, so a second row has time to land.
                settled = rows.Count > 1;
                await Task.Delay(rows.Count > 1 ? 1000 : 100);
                rows.Clear();
                await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT set_config('app.current_tenant_id', @t, false);
                    SELECT endpoint, status_code, entity_type FROM read_access_log
                    WHERE endpoint LIKE 'POST /api/v4/reports/custom/run%' AND created_at >= @since;
                    """;
                cmd.Parameters.AddWithValue("t", _tenantAId.ToString());
                cmd.Parameters.AddWithValue("since", since);
                await using var reader = await cmd.ExecuteReaderAsync();
                await reader.NextResultAsync();
                while (await reader.ReadAsync())
                    rows.Add((reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            rows.Select(r => r.Endpoint).Should().BeEquivalentTo(
                ["POST /api/v4/reports/custom/run", "POST /api/v4/reports/custom/run (preview)"],
                "one row for viewing and one for the builder's preview, told apart");
            rows.Should().OnlyContain(r => r.Status == 200);
            rows.Should().OnlyContain(r => r.Entities == "glucose,treatments", "the series reads treatments for IOB and COB, and the statistics read glucose");
        }
        finally
        {
            (await a.PutAsJsonAsync("/api/v4/audit/config", new { readAuditEnabled = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task The_catalogue_lists_every_dataset_and_says_which_the_caller_may_read()
    {
        using var owner = Session(_slugA, _sessionA);
        var full = await ReadAsync(await owner.GetAsync($"{Route}/catalogue"), HttpStatusCode.OK);
        full.GetProperty("maxPanels").GetInt32().Should().Be(6);
        var datasets = full.GetProperty("datasets").EnumerateArray().ToList();
        datasets.Select(d => d.GetProperty("name").GetString()).Should().Contain(["glucose_series", "event_response", "treatments"]);
        datasets.Should().OnlyContain(d => d.GetProperty("permitted").GetBoolean(), "an owner reads every category");
        datasets.Should().OnlyContain(d => d.GetProperty("parameters").GetProperty("type").GetString() == "object");

        var (_, viewerSession) = await SeedViewerAsync("Catalogue Viewer");
        using var viewer = Session(_slugA, viewerSession);
        var narrow = await ReadAsync(await viewer.GetAsync($"{Route}/catalogue"), HttpStatusCode.OK);
        var byName = narrow.GetProperty("datasets").EnumerateArray().ToDictionary(d => d.GetProperty("name").GetString()!);
        byName.Keys.Should().BeEquivalentTo(datasets.Select(d => d.GetProperty("name").GetString()), "the catalogue reads no data, so it lists every dataset");
        byName["glucose_statistics"].GetProperty("permitted").GetBoolean().Should().BeTrue();
        byName["treatments"].GetProperty("permitted").GetBoolean().Should().BeFalse("the viewer holds glucose.read and reports.read only");
        byName["glucose_statistics"].GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "tdd").GetProperty("permitted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Every_template_runs_and_a_glucose_only_caller_gets_the_degraded_panels_with_the_scope_note()
    {
        using var owner = Session(_slugA, _sessionA);
        var templates = await ReadAsync(await owner.GetAsync($"{Route}/templates"), HttpStatusCode.OK);
        templates.EnumerateArray().Select(t => t.GetProperty("id").GetString()).Should()
            .Equal("glucose", "agp", "treatments", "comparison", "weekdays", "insulin-glucose");
        foreach (var template in templates.EnumerateArray())
        {
            var run = await ReadAsync(await owner.PostAsJsonAsync($"{Route}/run", new { spec = template.GetProperty("spec") }), HttpStatusCode.OK);
            run.GetProperty("errors").GetArrayLength().Should().Be(0, template.GetProperty("id").GetString());
        }

        var (_, viewerSession) = await SeedViewerAsync("Template Viewer");
        using var viewer = Session(_slugA, viewerSession);
        var agp = (await ReadAsync(await viewer.GetAsync($"{Route}/templates"), HttpStatusCode.OK)).EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == "agp");
        var degraded = await ReadAsync(await viewer.PostAsJsonAsync($"{Route}/run", new { spec = agp.GetProperty("spec") }), HttpStatusCode.OK);
        var figures = degraded.GetProperty("result").GetProperty("panels")[0];
        figures.GetProperty("status").GetString().Should().Be("ok");
        figures.GetProperty("omitted").EnumerateArray().Select(o => o.GetProperty("field").GetString()).Should().Contain("tdd");
        figures.GetProperty("summary").GetString().Should().Contain("Fields omitted: tdd, basalPercent, carbsPerDay (scope not granted).");

        var treatments = (await ReadAsync(await viewer.GetAsync($"{Route}/templates"), HttpStatusCode.OK)).EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == "treatments");
        var records = (await ReadAsync(await viewer.PostAsJsonAsync($"{Route}/run", new { spec = treatments.GetProperty("spec") }), HttpStatusCode.OK))
            .GetProperty("result").GetProperty("panels")[1];
        records.GetProperty("status").GetString().Should().Be("not_permitted", "the treatment records need treatments.read");
    }

    /// <summary>Glucose every five minutes over the last <paramref name="hours"/> hours, rising and falling, and a bolus every two hours.</summary>
    private async Task SeedGlucoseAndBolusesAsync(int hours)
    {
        using var client = Session(_slugA, _sessionA);
        var now = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(1, hours * 12).Select(i =>
        {
            var at = now.AddMinutes(-5 * i);
            var sgv = (int)(140 + 60 * Math.Sin(i / 8.0));
            return new { type = "sgv", sgv, direction = "Flat", date = at.ToUnixTimeMilliseconds(), dateString = at.UtcDateTime.ToString("o") };
        }).ToArray();
        (await client.PostAsJsonAsync("/api/v1/entries", entries)).StatusCode.Should().Be(HttpStatusCode.OK);
        var boluses = Enumerable.Range(1, hours / 2).Select(i => new
        {
            eventType = "Correction Bolus",
            insulin = 1.0 + i % 3,
            created_at = now.AddHours(-2 * i + 1).UtcDateTime.ToString("o"),
        }).ToArray();
        var response = await client.PostAsJsonAsync("/api/v1/treatments", boluses);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_four_track_glucose_chart_runs_for_the_owner_and_degrades_for_a_glucose_only_viewer()
    {
        await SeedGlucoseAndBolusesAsync(6);
        using var owner = Session(_slugA, _sessionA);
        var glucose = (await ReadAsync(await owner.GetAsync($"{Route}/templates"), HttpStatusCode.OK)).EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == "glucose").GetProperty("spec");

        var full = (await ReadAsync(await owner.PostAsJsonAsync($"{Route}/run", new { spec = glucose }), HttpStatusCode.OK))
            .GetProperty("result").GetProperty("panels")[0];
        full.GetProperty("tracks").GetArrayLength().Should().Be(4);
        var ownerLayers = full.GetProperty("tracks").EnumerateArray().SelectMany(t => t.GetProperty("layers").EnumerateArray())
            .ToDictionary(l => l.GetProperty("id").GetString()!);
        ownerLayers.Values.Should().OnlyContain(l => l.GetProperty("status").GetString() == "ok");
        ownerLayers["glucose"].GetProperty("rows").GetArrayLength().Should().BeGreaterThan(60);
        ownerLayers["treatments"].GetProperty("markers").GetArrayLength().Should().BeGreaterThan(0);
        ownerLayers["iob"].GetProperty("side").GetString().Should().Be("left");
        ownerLayers["cob"].GetProperty("side").GetString().Should().Be("right");

        var (_, viewerSession) = await SeedViewerAsync("Glucose Chart Viewer");
        using var viewer = Session(_slugA, viewerSession);
        var degraded = (await ReadAsync(await viewer.PostAsJsonAsync($"{Route}/run", new { spec = glucose }), HttpStatusCode.OK))
            .GetProperty("result").GetProperty("panels")[0];
        degraded.GetProperty("status").GetString().Should().Be("ok");
        var layers = degraded.GetProperty("tracks").EnumerateArray().SelectMany(t => t.GetProperty("layers").EnumerateArray())
            .ToDictionary(l => l.GetProperty("id").GetString()!, l => l);
        layers["glucose"].GetProperty("status").GetString().Should().Be("ok");
        layers["glucose"].GetProperty("rows").GetArrayLength().Should().BeGreaterThan(60);
        layers["basal"].GetProperty("status").GetString().Should().Be("not_permitted");
        layers["spans"].GetProperty("status").GetString().Should().Be("not_permitted");
        layers["treatments"].GetProperty("status").GetString().Should().Be("not_permitted");
        layers["basal"].GetProperty("rows").GetArrayLength().Should().Be(0);
        degraded.GetProperty("sentence").GetString().Should().Contain("Not shown:").And.Contain("Basal");
    }

    [Fact]
    public async Task A_scatter_and_a_lag_correlation_over_the_seeded_tenant_give_a_finite_r()
    {
        await SeedGlucoseAndBolusesAsync(8);
        using var owner = Session(_slugA, _sessionA);
        object Source(string field) => new { dataset = new { name = "glucose_series" }, field };
        object Panel(string id, string op) => new
        {
            id,
            kind = "chart",
            grid = 15,
            tracks = new[] { new { layers = new[] { new { derived = new { op, a = Source("iob"), b = Source("glucose") } } } } },
        };
        var spec = new { version = 1, range = new { hours = 8 }, panels = new[] { Panel("scatter", "scatter"), Panel("lag", "lag_correlation") } };

        var run = await ReadAsync(await owner.PostAsJsonAsync($"{Route}/run", new { spec }), HttpStatusCode.OK);

        run.GetProperty("errors").GetArrayLength().Should().Be(0);
        var panels = run.GetProperty("result").GetProperty("panels");
        var scatter = panels[0].GetProperty("tracks")[0].GetProperty("layers")[0];
        scatter.GetProperty("status").GetString().Should().Be("ok");
        scatter.GetProperty("xKind").GetString().Should().Be("value");
        var r = scatter.GetProperty("facts").EnumerateArray().Single(f => f.GetProperty("label").GetString() == "r").GetProperty("value").GetString();
        double.IsFinite(double.Parse(r!, System.Globalization.CultureInfo.InvariantCulture)).Should().BeTrue();
        var lag = panels[1].GetProperty("tracks")[0].GetProperty("layers")[0];
        lag.GetProperty("xKind").GetString().Should().Be("lag");
        lag.GetProperty("rows").GetArrayLength().Should().Be(25, "every 15 minutes from -180 to +180");
        var best = lag.GetProperty("facts").EnumerateArray().Single(f => f.GetProperty("label").GetString() == "Largest r").GetProperty("value").GetString();
        double.IsFinite(double.Parse(best!, System.Globalization.CultureInfo.InvariantCulture)).Should().BeTrue();
    }

    [Fact]
    public async Task A_spec_of_more_than_sixteen_reads_is_refused_with_the_count()
    {
        using var owner = Session(_slugA, _sessionA);
        var panels = Enumerable.Range(1, 6).Select(i => new
        {
            kind = "chart",
            range = new { hours = i },
            tracks = new[]
            {
                new
                {
                    layers = new object[]
                    {
                        new { dataset = new { name = "glucose_series" } },
                        new { dataset = new { name = "treatments" } },
                        new { dataset = new { name = "basal_delivery" } },
                    },
                },
            },
        }).ToArray();

        var run = await ReadAsync(await owner.PostAsJsonAsync($"{Route}/run", new { spec = new { version = 1, panels } }), HttpStatusCode.OK);

        run.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);
        var error = run.GetProperty("errors").EnumerateArray().Should().ContainSingle().Subject;
        error.GetProperty("path").GetString().Should().Be("panels");
        error.GetProperty("message").GetString().Should().StartWith("This report reads 18 datasets; a report reads at most 16.");
    }

    [Fact]
    public async Task The_catalogue_and_templates_are_refused_to_a_share_host()
    {
        string shareHost;
        using (var scope = Fixture.Services.CreateScope())
        {
            var link = await scope.ServiceProvider.GetRequiredService<IShareLinkService>().RotateAsync(_tenantAId);
            shareHost = new Uri(link.Url!).Host;
        }

        using var share = Fixture.CreateHttpClient("nocturne-api", "http");
        share.DefaultRequestHeaders.Host = $"{shareHost.Split('.')[0]}.share.{_baseDomain}";
        (await share.GetAsync($"{Route}/catalogue")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await share.GetAsync($"{Route}/templates")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task No_assistant_endpoint_or_protected_resource_document_is_served()
    {
        using var owner = Session(_slugA, _sessionA);
        (await owner.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "initialize" }))
            .StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        (await owner.GetAsync("/.well-known/oauth-protected-resource")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.GetAsync("/.well-known/oauth-protected-resource/mcp")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<(Guid Subject, string Session)> SeedViewerAsync(string name)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var (subject, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantAId, name);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM subject_roles WHERE subject_id = @subject;
            UPDATE tenant_members SET direct_permissions = '["glucose.read","reports.read"]'::jsonb WHERE subject_id = @subject;
            """;
        cmd.Parameters.AddWithValue("subject", subject);
        await cmd.ExecuteNonQueryAsync();
        return (subject, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subject));
    }

    [Fact]
    public async Task A_viewer_saves_and_changes_only_their_own_reports_and_an_owner_changes_anyones()
    {
        using var owner = Session(_slugA, _sessionA);
        var ownersReport = await ReadAsync(await owner.PostAsJsonAsync(Route, new { name = "Owner's", spec = Spec() }), HttpStatusCode.OK);
        var ownersId = ownersReport.GetProperty("id").GetGuid();

        var (_, viewerSession) = await SeedViewerAsync("Reports Viewer");
        using var viewer = Session(_slugA, viewerSession);

        var list = await ReadAsync(await viewer.GetAsync(Route), HttpStatusCode.OK);
        list.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == ownersId).GetProperty("canEdit").GetBoolean()
            .Should().BeFalse("the viewer did not create it and edits no one else's");
        (await ReadAsync(await viewer.GetAsync($"{Route}/{ownersId}"), HttpStatusCode.OK)).GetProperty("canEdit").GetBoolean().Should().BeFalse();
        (await viewer.PutAsJsonAsync($"{Route}/{ownersId}", new { name = "Mine now" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.DeleteAsync($"{Route}/{ownersId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var viewersReport = await ReadAsync(await viewer.PostAsJsonAsync(Route, new { name = "Viewer's", spec = Spec() }), HttpStatusCode.OK);
        var viewersId = viewersReport.GetProperty("id").GetGuid();
        viewersReport.GetProperty("canEdit").GetBoolean().Should().BeTrue();
        await ReadAsync(await viewer.PutAsJsonAsync($"{Route}/{viewersId}", new { name = "Viewer's, renamed" }), HttpStatusCode.OK);

        (await ReadAsync(await owner.GetAsync(Route), HttpStatusCode.OK)).EnumerateArray()
            .Should().OnlyContain(r => r.GetProperty("canEdit").GetBoolean(), "an owner edits every report");
        (await owner.DeleteAsync($"{Route}/{viewersId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadAsync(await owner.GetAsync($"{Route}/{ownersId}"), HttpStatusCode.OK)).GetProperty("name").GetString().Should().Be("Owner's");
    }

    [Fact]
    public async Task The_101st_report_is_refused_with_a_message()
    {
        await using (var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @tenant, false);
                INSERT INTO report_specs (id, tenant_id, name, spec, version, created_at, updated_at)
                SELECT gen_random_uuid(), @tenantId, 'Seeded ' || n, '{"version":1,"panels":[]}'::jsonb, 1, now(), now()
                FROM generate_series(1, 100) AS n;
                """;
            cmd.Parameters.AddWithValue("tenant", _tenantAId.ToString());
            cmd.Parameters.AddWithValue("tenantId", _tenantAId);
            await cmd.ExecuteNonQueryAsync();
        }

        using var a = Session(_slugA, _sessionA);
        var refused = await ReadAsync(await a.PostAsJsonAsync(Route, new { name = "One more", spec = Spec() }), HttpStatusCode.BadRequest);

        refused.GetProperty("errors").GetProperty("name")[0].GetString()
            .Should().Be("This account has 100 custom reports, the most it can keep. Delete a report to save another.");
        (await ReadAsync(await a.GetAsync(Route), HttpStatusCode.OK)).GetArrayLength().Should().Be(100);
    }

    [Fact]
    public async Task An_app_token_cannot_write_and_a_guest_link_cannot_read()
    {
        using var owner = Session(_slugA, _sessionA);
        var saved = await ReadAsync(await owner.PostAsJsonAsync(Route, new { name = "Shared", spec = Spec() }), HttpStatusCode.OK);
        var id = saved.GetProperty("id").GetGuid();

        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(owner, scope: "glucose.read reports.read");
        var flow = await AuthTestHelpers.ExecutePkceFlowAsync(owner, clientId, scope: "glucose.read reports.read");
        using (var app = Session(_slugA, flow.AccessToken))
        {
            (await app.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.OK, "an app may read the list");
            (await app.PostAsJsonAsync(Route, new { name = "App", spec = Spec() })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await app.PutAsJsonAsync($"{Route}/{id}", new { name = "App" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await app.DeleteAsync($"{Route}/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        var created = await ReadAsync(await owner.PostAsJsonAsync("/api/v4/guest-links", new { label = "Reports", scopes = new[] { "glucose.read" } }), HttpStatusCode.OK);
        using var anonymous = AuthTestHelpers.CreateTenantClient(Fixture, _slugA, _baseDomain);
        var activated = await anonymous.PostAsJsonAsync("/api/v4/guest-links/activate", new { code = created.GetProperty("code").GetString() });
        activated.StatusCode.Should().Be(HttpStatusCode.OK, await activated.Content.ReadAsStringAsync());
        var cookie = activated.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("nocturne-guest-session", StringComparison.Ordinal)).Split(';')[0];

        using var guest = AuthTestHelpers.CreateTenantClient(Fixture, _slugA, _baseDomain);
        guest.DefaultRequestHeaders.Add("Cookie", cookie);
        (await guest.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await guest.GetAsync($"{Route}/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
