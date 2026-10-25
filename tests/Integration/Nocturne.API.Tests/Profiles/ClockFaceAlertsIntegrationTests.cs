using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Profiles;

/// <summary>
/// A public clock face shows the alarm that is firing only when its owner turned alerts on, and the
/// clock id is the only credential it needs.
/// </summary>
[Trait("Category", "Integration")]
public class ClockFaceAlertsIntegrationTests : ApiIntegrationTestBase
{
    private const string RuleName = "Kitchen low";

    private HttpClient _owner = null!;
    private Guid _excursionId;

    public ClockFaceAlertsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var (ownerId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, Fixture.TenantId, "Clock Owner");
        _owner = AuthTestHelpers.CreateBearerClient(
            Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, ownerId));

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
            INSERT INTO alert_excursions (id, tenant_id, alert_rule_id, started_at)
            VALUES ('{_excursionId}', '{Fixture.TenantId}', '{ruleId}', now() - interval '5 minutes');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public override async Task DisposeAsync()
    {
        _owner.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task AFaceShowingAlerts_ServesTheFiringAlarmToAnAnonymousViewer()
    {
        var faceId = await CreateFaceAsync(showAlerts: true);
        using var viewer = Fixture.CreateHttpClient(new HttpClientHandler());

        using var response = await viewer.GetAsync($"/api/v4/clockfaces/{faceId}/alerts");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var alert = JsonNode.Parse(body)!.AsArray().Single()!.AsObject();
        alert["id"]!.GetValue<string>().Should().Be(_excursionId.ToString());
        alert["ruleName"]!.GetValue<string>().Should().Be(RuleName);
        alert["acknowledged"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task AFaceNotShowingAlerts_ServesNone()
    {
        var faceId = await CreateFaceAsync(showAlerts: false);
        using var viewer = Fixture.CreateHttpClient(new HttpClientHandler());

        using var response = await viewer.GetAsync($"/api/v4/clockfaces/{faceId}/alerts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownFace_IsNotFound()
    {
        using var viewer = Fixture.CreateHttpClient(new HttpClientHandler());

        using var response = await viewer.GetAsync($"/api/v4/clockfaces/{Guid.NewGuid()}/alerts");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AFaceAllowedToAcknowledge_RecordsThatItSawTheAlarm()
    {
        var faceId = await CreateFaceAsync(showAlerts: true, allowMarkSeen: true);
        using var viewer = Fixture.CreateHttpClient(new HttpClientHandler());

        (await viewer.PostAsync($"/api/v4/clockfaces/{faceId}/alerts/{_excursionId}/seen", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var clock = await viewer.GetAsync($"/api/v4/clockfaces/{faceId}/alerts");
        JsonNode.Parse(await clock.Content.ReadAsStringAsync())!.AsArray().Single()!["seenHere"]!
            .GetValue<bool>().Should().BeTrue();

        using var member = await _owner.GetAsync("/api/v4/alerts/active");
        var receipt = JsonNode.Parse(await member.Content.ReadAsStringAsync())!.AsArray()
            .Single(e => e!["id"]!.GetValue<string>() == _excursionId.ToString())!["receipts"]!
            .AsArray().Single()!;
        receipt["actorKind"]!.GetValue<string>().Should().Be("clock");
        receipt["actorLabel"]!.GetValue<string>().Should().Be("Kitchen");
    }

    [Fact]
    public async Task AFaceNotAllowedToAcknowledge_CannotRecordAReceipt()
    {
        var faceId = await CreateFaceAsync(showAlerts: true);
        using var viewer = Fixture.CreateHttpClient(new HttpClientHandler());

        using var response = await viewer.PostAsync($"/api/v4/clockfaces/{faceId}/alerts/{_excursionId}/seen", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AllowingAcknowledgeWithoutShowingAlerts_IsRefused()
    {
        using var response = await _owner.PostAsJsonAsync("/api/v4/clockfaces", new
        {
            name = "Kitchen",
            config = new { rows = Array.Empty<object>(), settings = new { allowMarkSeen = true } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<Guid> CreateFaceAsync(bool showAlerts, bool allowMarkSeen = false)
    {
        using var response = await _owner.PostAsJsonAsync("/api/v4/clockfaces", new
        {
            name = "Kitchen",
            config = new { rows = Array.Empty<object>(), settings = new { showAlerts, allowMarkSeen } },
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        return Guid.Parse(JsonNode.Parse(body)!["id"]!.GetValue<string>());
    }
}
