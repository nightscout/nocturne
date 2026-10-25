using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A public share link granted alerts.read shows the alarm that is firing, through the share RLS
/// policy on the alert tables, and nothing of the alert configuration behind it.
/// </summary>
[Trait("Category", "Integration")]
public class ShareAlertsIntegrationTests : ApiIntegrationTestBase
{
    private const string RuleName = "Daycare low";
    private const string Acknowledger = "Parent Name";

    private HttpClient _admin = null!;
    private HttpClient? _share;
    private Guid _excursionId;

    public ShareAlertsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var (adminId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, Fixture.TenantId, "Share Admin");
        _admin = AuthTestHelpers.CreateBearerClient(
            Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, adminId));

        // Opened three days ago: a share without full history still sees it, because an open alert
        // is clamped by overlap rather than by when it started.
        var ruleId = Guid.CreateVersion7();
        _excursionId = Guid.CreateVersion7();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT set_config('app.current_tenant_id', '{Fixture.TenantId}', false);
            INSERT INTO alert_rules (id, tenant_id, name, condition_type, condition_params, auto_resolve_enabled,
                                     severity, client_configuration, is_enabled, allow_through_dnd, scope_class,
                                     sort_order, created_at, updated_at)
            VALUES ('{ruleId}', '{Fixture.TenantId}', '{RuleName}', 'threshold', '{"{}"}', false,
                    'critical', '{"{}"}', true, false, 'undirected', 1, now(), now());
            INSERT INTO alert_excursions (id, tenant_id, alert_rule_id, started_at, acknowledged_at, acknowledged_by)
            VALUES ('{_excursionId}', '{Fixture.TenantId}', '{ruleId}', now() - interval '3 days',
                    now() - interval '1 hour', '{Acknowledger}');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public override async Task DisposeAsync()
    {
        _share?.Dispose();
        using (var disable = await _admin.DeleteAsync("/api/v4/share"))
        {
            disable.EnsureSuccessStatusCode();
        }

        _admin.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task AShareGrantedAlerts_SeesTheFiringAlarmButNotWhoAcknowledgedIt()
    {
        var share = await EnableShareAsync("glucose.read", "alerts.read");

        using var response = await share.GetAsync("/api/v4/alerts/active");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var excursion = JsonNode.Parse(body)!.AsArray()
            .Select(node => node!.AsObject())
            .Single(e => e["id"]!.GetValue<string>() == _excursionId.ToString());
        excursion["ruleName"]!.GetValue<string>().Should().Be(RuleName,
            "the share RLS policy on alert_rules lets the rule's name through under alerts.read");
        excursion["acknowledgedAt"].Should().NotBeNull();
        excursion["acknowledgedBy"].Should().BeNull("a share may see that an alert was acknowledged, not by whom");
    }

    [Theory]
    [InlineData("/api/v4/alerts/history")]
    [InlineData("/api/v4/alert-rules")]
    [InlineData("/api/v4/alerts/dnd/windows/active")]
    public async Task AShareGrantedAlerts_IsRefusedTheAlertConfiguration(string path)
    {
        var share = await EnableShareAsync("glucose.read", "alerts.read");

        using var response = await share.GetAsync(path);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AShareWithoutAlerts_IsRefusedTheFiringAlarm()
    {
        var share = await EnableShareAsync("glucose.read");

        using var response = await share.GetAsync("/api/v4/alerts/active");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReceiptsFromAShareAndAGuest_AreListedForMembers_AndEachViewerSeesOnlyItsOwn()
    {
        var share = await EnableShareAsync("glucose.read", "alerts.read");
        using var guest = await ActivateGuestAsync("Grandma");

        (await share.PostAsync($"/api/v4/alerts/excursions/{_excursionId}/seen", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await guest.PostAsync($"/api/v4/alerts/excursions/{_excursionId}/seen", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await guest.PostAsync($"/api/v4/alerts/excursions/{_excursionId}/seen", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent, "recording it again keeps the first");

        var memberView = await ReceiptsAsync(_admin);
        memberView.Select(r => r["actorKind"]!.GetValue<string>())
            .Should().BeEquivalentTo(["public_link", "guest"]);
        memberView.Single(r => r["actorKind"]!.GetValue<string>() == "guest")["actorLabel"]!
            .GetValue<string>().Should().Be("Grandma");

        (await ReceiptsAsync(share)).Select(r => r["actorKind"]!.GetValue<string>())
            .Should().Equal(["public_link"], "a share sees its own receipt and nobody else's");
        (await ReceiptsAsync(guest)).Select(r => r["actorKind"]!.GetValue<string>())
            .Should().Equal(["guest"], "a guest sees its own receipt and nobody else's");

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        await using (var pin = conn.CreateCommand())
        {
            pin.CommandText = $"SELECT set_config('app.current_tenant_id', '{Fixture.TenantId}', false);";
            await pin.ExecuteNonQueryAsync();
        }

        await using var count = conn.CreateCommand();
        count.CommandText = $"SELECT count(*) FROM alert_excursion_mutes WHERE alert_excursion_id = '{_excursionId}';";
        ((long)(await count.ExecuteScalarAsync())!).Should().Be(0,
            "a receipt mutes nothing, least of all the data owner a guest acts for");
    }

    [Fact]
    public async Task AReceiptForAnAlarmThatIsNotOpen_IsNotFound()
    {
        var share = await EnableShareAsync("glucose.read", "alerts.read");

        using var response = await share.PostAsync($"/api/v4/alerts/excursions/{Guid.NewGuid()}/seen", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AShareWithoutAlerts_CannotRecordAReceipt()
    {
        var share = await EnableShareAsync("glucose.read");

        using var response = await share.PostAsync($"/api/v4/alerts/excursions/{_excursionId}/seen", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<List<JsonObject>> ReceiptsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v4/alerts/active");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsArray()
            .Single(e => e!["id"]!.GetValue<string>() == _excursionId.ToString())!["receipts"]!
            .AsArray().Select(r => r!.AsObject()).ToList();
    }

    private async Task<HttpClient> ActivateGuestAsync(string label)
    {
        using var link = await _admin.PostAsJsonAsync("/api/v4/guest-links", new
        {
            label,
            scopes = new[] { "glucose.read", "alerts.read" },
        });
        var body = await link.Content.ReadAsStringAsync();
        link.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var code = JsonNode.Parse(body)!["code"]!.GetValue<string>();

        var guest = Fixture.CreateHttpClient(new HttpClientHandler { UseCookies = true });
        (await guest.PostAsJsonAsync("/api/v4/guest-links/activate", new { code }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return guest;
    }

    /// <summary>
    /// Mints the tenant's share link with <paramref name="scopes"/> and returns a client addressing
    /// its host once that host serves glucose.
    /// </summary>
    private async Task<HttpClient> EnableShareAsync(params string[] scopes)
    {
        using var rotate = await _admin.PostAsync("/api/v4/share/rotate", null);
        rotate.StatusCode.Should().Be(HttpStatusCode.OK, await rotate.Content.ReadAsStringAsync());
        var url = (await rotate.Content.ReadFromJsonAsync<JsonObject>())!["url"]!.GetValue<string>();

        using (var put = await _admin.PutAsJsonAsync("/api/v4/share/scopes", new { scopes }))
        {
            put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        }

        _share?.Dispose();
        _share = Fixture.CreateApexClient();
        _share.DefaultRequestHeaders.Host = new Uri(url).Authority;

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            using var probe = await _share.GetAsync("/api/v1/entries.json?count=1");
            if (probe.StatusCode == HttpStatusCode.OK)
                return _share;

            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException(
                    $"The share host never served glucose: {(int)probe.StatusCode} "
                    + await probe.Content.ReadAsStringAsync());
            }

            await Task.Delay(250);
        }
    }
}
