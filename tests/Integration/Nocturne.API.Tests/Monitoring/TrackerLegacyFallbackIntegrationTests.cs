using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Services;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Monitoring;

/// <summary>
/// The legacy-age fallbacks at <c>/api/v4/trackers/legacy-fallbacks</c> and their conversion at
/// <c>/api/v4/trackers/from-legacy</c>, against the real database and alert engine.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class TrackerLegacyFallbackIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private string _accessToken = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        using var client = CreateAuthenticatedClient();
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_, _accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Fallback Test User");
    }

    private HttpClient Client() => AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, _accessToken);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> RecordDeviceEventAsync(HttpClient client, string eventType, DateTime at)
    {
        var response = await client.PostAsJsonAsync("/api/v4/observations/device-events", new
        {
            timestamp = at,
            eventType,
            dataSource = "legacy-fallback-test",
        });
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    /// <summary>Runs the tenant's alert sweep once, as the job scheduler does every 30 seconds.</summary>
    private async Task SweepAsync()
    {
        var tenant = new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false);
        await using var scope = TenantJobScope.Open(Fixture.Services, tenant, $"job:{AlertSweepJob.JobName}");
        await Fixture.Services.GetServices<IScheduledJob>().OfType<AlertSweepJob>().Single()
            .RunAsync(new JobRun(tenant, null, 0, scope.ServiceProvider, DateTimeOffset.UtcNow), CancellationToken.None);
    }

    /// <summary>The names of the rules of the definition that have fired, oldest first.</summary>
    private Task<List<string>> FiredRulesForAsync(string definitionId) =>
        WithTenantScopeAsync(async services =>
        {
            await using var db = await services.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
            var tag = TrackerAlertRuleSyncService.ManagedByTag(Guid.Parse(definitionId));
            return await db.AlertInstances
                .Where(i => i.AlertExcursion!.AlertRule!.ManagedBy == tag)
                .OrderBy(i => i.TriggeredAt)
                .Select(i => i.AlertExcursion!.AlertRule!.Name)
                .ToListAsync();
        });

    /// <summary>Moves the running instance's start and recorded time back, as if that much time had passed.</summary>
    private Task AgeRunAsync(string definitionId, TimeSpan by) =>
        WithTenantScopeAsync(async services =>
        {
            await using var db = await services.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
            var id = Guid.Parse(definitionId);
            var run = await db.TrackerInstances.SingleAsync(i => i.DefinitionId == id && i.CompletedAt == null);
            run.StartedAt -= by;
            run.RecordedAt -= by;
            await db.SaveChangesAsync();
            return true;
        });

    private async Task<string> ConvertAsync(HttpClient client, string kind)
    {
        var conversion = await JsonAsync(await client.PostAsJsonAsync(
            "/api/v4/trackers/from-legacy", new { kinds = new[] { kind } }));
        return conversion.GetProperty("definitions")[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task AnUncoveredSensor_IsAFallbackUntilItIsConverted()
    {
        using var client = Client();
        var eventId = await RecordDeviceEventAsync(client, "SensorStart", DateTime.UtcNow.AddDays(-3));

        var fallbacks = await JsonAsync(await client.GetAsync("/api/v4/trackers/legacy-fallbacks"));
        var sensor = fallbacks.GetProperty("fallbacks").EnumerateArray().Should().ContainSingle().Subject;
        sensor.GetProperty("kind").GetString().Should().Be("Sensor");
        sensor.GetProperty("latestEventId").GetString().Should().Be(eventId);
        sensor.GetProperty("schedule").GetArrayLength().Should().Be(2);

        var conversion = await JsonAsync(await client.PostAsJsonAsync(
            "/api/v4/trackers/from-legacy", new { kinds = new[] { "Sensor" } }));
        var instance = conversion.GetProperty("instances").EnumerateArray().Should().ContainSingle().Subject;
        instance.GetProperty("schedule").EnumerateArray().Select(s => s.GetProperty("firesAt").GetDateTime())
            .Should().Equal(sensor.GetProperty("schedule").EnumerateArray().Select(s => s.GetProperty("firesAt").GetDateTime()));

        var after = await JsonAsync(await client.GetAsync("/api/v4/trackers/legacy-fallbacks"));
        after.GetProperty("fallbacks").GetArrayLength().Should().Be(0);
        var covered = after.GetProperty("covered").EnumerateArray().Should().ContainSingle().Subject;
        covered.GetProperty("kind").GetString().Should().Be("Sensor");
        covered.GetProperty("definitionIds")[0].GetString()
            .Should().Be(conversion.GetProperty("definitions")[0].GetProperty("id").GetString());
        var running = await JsonAsync(await client.GetAsync("/api/v4/trackers/instances"));
        running.EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("startedAt").GetDateTime().Should()
            .BeCloseTo(sensor.GetProperty("latestEventAt").GetDateTime(), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task TwoConcurrentConversions_CreateOneTracker()
    {
        using var client = Client();
        await RecordDeviceEventAsync(client, "SiteChange", DateTime.UtcNow.AddHours(-20));

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/api/v4/trackers/from-legacy", new { kinds = new[] { "Site" } }),
            client.PostAsJsonAsync("/api/v4/trackers/from-legacy", new { kinds = new[] { "Site" } }));

        var created = 0;
        foreach (var response in responses)
            created += (await JsonAsync(response)).GetProperty("definitions").GetArrayLength();
        created.Should().Be(1);
        (await JsonAsync(await client.GetAsync("/api/v4/trackers/definitions"))).GetArrayLength().Should().Be(1);
        (await JsonAsync(await client.GetAsync("/api/v4/trackers/instances"))).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ConvertingAnOldSensor_DeliversNothingForTheThresholdItHasPassed()
    {
        using var client = Client();
        await RecordDeviceEventAsync(client, "SensorStart", DateTime.UtcNow.AddDays(-9).AddHours(-1));
        var definitionId = await ConvertAsync(client, "Sensor");

        await SweepAsync();
        await SweepAsync();

        (await FiredRulesForAsync(definitionId)).Should().BeEmpty();
    }

    [Fact]
    public async Task SweepsDuringTheConversion_DeliverNothingForThePassedThreshold()
    {
        using var client = Client();
        await RecordDeviceEventAsync(client, "SensorStart", DateTime.UtcNow.AddDays(-9).AddHours(-1));

        using var done = new CancellationTokenSource();
        var sweeping = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
                await SweepAsync();
        });
        var response = await client.PostAsJsonAsync("/api/v4/trackers/from-legacy", new { kinds = new[] { "Sensor" } });
        await SweepAsync();
        await done.CancelAsync();
        await sweeping;

        var definitionId = (await JsonAsync(response)).GetProperty("definitions")[0].GetProperty("id").GetString()!;
        (await FiredRulesForAsync(definitionId)).Should().BeEmpty();
    }

    [Fact]
    public async Task AfterAQuietWarning_TheHazardStillFiresWhenTheSensorExpires()
    {
        using var client = Client();
        await RecordDeviceEventAsync(client, "SensorStart", DateTime.UtcNow.AddDays(-9).AddHours(-1));
        var definitionId = await ConvertAsync(client, "Sensor");

        await AgeRunAsync(definitionId, TimeSpan.FromDays(1));
        await SweepAsync();

        (await FiredRulesForAsync(definitionId)).Should().ContainSingle()
            .Which.Should().EndWith("240h elapsed");
    }

    [Fact]
    public async Task TheConvertedLaddersRules_RespectDoNotDisturb()
    {
        using var client = Client();
        await RecordDeviceEventAsync(client, "SiteChange", DateTime.UtcNow.AddHours(-2));
        var definitionId = Guid.Parse(await ConvertAsync(client, "Site"));

        var rules = await WithTenantScopeAsync(async services =>
        {
            await using var db = await services.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
            var tag = TrackerAlertRuleSyncService.ManagedByTag(definitionId);
            return await db.AlertRules.Where(r => r.ManagedBy == tag)
                .Select(r => new { r.AllowThroughDnd, r.Severity }).ToListAsync();
        });

        rules.Should().HaveCount(2).And.OnlyContain(r => !r.AllowThroughDnd && r.Severity != AlertRuleSeverity.Critical);
    }

    [Fact]
    public async Task ABackdatedManualStart_DeliversOnlyTheThresholdsThatFallDueAfterIt()
    {
        using var client = Client();
        var created = await client.PostAsJsonAsync("/api/v4/trackers/definitions", new
        {
            name = "Manual sensor",
            category = "Sensor",
            lifespanHours = 240,
            mode = "Duration",
            notificationThresholds = new[]
            {
                new { urgency = "Warn", hours = -24, displayOrder = 0, description = "passed" },
                new { urgency = "Hazard", hours = 240, displayOrder = 1, description = "later" },
            },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var definitionId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        (await client.PostAsJsonAsync("/api/v4/trackers/instances", new
        {
            definitionId,
            startedAt = DateTime.UtcNow.AddDays(-9).AddHours(-1),
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        await SweepAsync();
        (await FiredRulesForAsync(definitionId)).Should().BeEmpty();

        await AgeRunAsync(definitionId, TimeSpan.FromDays(1));
        await SweepAsync();
        (await FiredRulesForAsync(definitionId)).Should().Equal("Manual sensor: later");
    }

    [Fact]
    public async Task AnAppointmentBookedTwoHoursAhead_StillSendsItsDayBeforeReminder()
    {
        using var client = Client();
        var created = await client.PostAsJsonAsync("/api/v4/trackers/definitions", new
        {
            name = "Clinic",
            category = "Appointment",
            mode = "Event",
            notificationThresholds = new[]
            {
                new { urgency = "Info", hours = -24, displayOrder = 0, description = "day before" },
            },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var definitionId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        (await client.PostAsJsonAsync("/api/v4/trackers/instances", new
        {
            definitionId,
            scheduledAt = DateTime.UtcNow.AddHours(2),
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        await SweepAsync();

        (await FiredRulesForAsync(definitionId)).Should().Equal("Clinic: day before");
    }
}
