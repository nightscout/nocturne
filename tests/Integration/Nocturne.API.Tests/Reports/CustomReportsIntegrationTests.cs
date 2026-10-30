using System.Buffers.Text;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
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
            new { dataset = new { name = "glucose_series" }, primitive = "time_series", bindings = new { y = new[] { "glucose" } }, range = new { hours = 6 } },
            new { id = "stats", dataset = new { name = "glucose_statistics" }, primitive = "stat_tiles", range = new { hours = 336 } },
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
            spec = new { version = 1, panels = new object[] { new { dataset = new { name = "insulin" }, primitive = "table" } } },
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
                VALUES (@id, @tenantId, 'Old', '{"version":0,"panels":[{"dataset":{"name":"glucose_series"},"primitive":"time_series","bindings":{"y":["glucose"]}}]}'::jsonb, 0, now(), now());
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
    public async Task The_open_link_a_run_returns_runs_the_same_spec_and_an_oversized_link_is_refused()
    {
        await PostGlucoseAsync(_slugA, _sessionA, 140, TimeSpan.FromMinutes(5));
        using var a = Session(_slugA, _sessionA);

        var first = await ReadAsync(await a.PostAsJsonAsync($"{Route}/run", new { spec = Spec("<b>bold</b>") }), HttpStatusCode.OK);
        var openUrl = first.GetProperty("result").GetProperty("openUrl").GetString()!;
        Log(openUrl);
        openUrl.Should().StartWith($"http://{_slugA}.{_baseDomain}/reports/custom/new?spec=");
        var encoded = QueryHelpers.ParseQuery(new Uri(openUrl).Query)["spec"].ToString();

        var linked = await ReadAsync(await a.PostAsJsonAsync($"{Route}/run", new { encoded }), HttpStatusCode.OK);
        linked.GetProperty("errors").GetArrayLength().Should().Be(0);
        linked.GetProperty("spec").GetRawText().Should().Be(first.GetProperty("spec").GetRawText());
        linked.GetProperty("result").GetProperty("spec").GetProperty("title").GetString().Should().Be("<b>bold</b>", "a title is data; the page writes it as text");

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(Encoding.UTF8.GetBytes(new string(' ', 1024 * 1024)));
        var bomb = await ReadAsync(await a.PostAsJsonAsync($"{Route}/run", new { encoded = Base64Url.EncodeToString(output.ToArray()) }), HttpStatusCode.OK);
        bomb.GetProperty("errors")[0].GetProperty("message").GetString().Should().Contain("at most 16 kB");

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

            var rows = new List<(int Status, string? Entities)>();
            var settled = false;
            for (var attempt = 0; attempt < 60 && !settled; attempt++)
            {
                // Once a row is there, read once more after a pause, so a second row has time to land.
                settled = rows.Count > 0;
                await Task.Delay(rows.Count > 0 ? 1000 : 100);
                rows.Clear();
                await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT set_config('app.current_tenant_id', @t, false);
                    SELECT status_code, entity_type FROM read_access_log
                    WHERE endpoint = 'POST /api/v4/reports/custom/run' AND created_at >= @since;
                    """;
                cmd.Parameters.AddWithValue("t", _tenantAId.ToString());
                cmd.Parameters.AddWithValue("since", since);
                await using var reader = await cmd.ExecuteReaderAsync();
                await reader.NextResultAsync();
                while (await reader.ReadAsync())
                    rows.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }

            var row = rows.Should().ContainSingle().Subject;
            row.Status.Should().Be(200);
            row.Entities.Should().Be("glucose,treatments", "the series reads treatments for IOB and COB, and the statistics read glucose");
        }
        finally
        {
            (await a.PutAsJsonAsync("/api/v4/audit/config", new { readAuditEnabled = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        }
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
