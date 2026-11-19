using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Loop reports every cycle enacted, in open loop too, so a Loop snapshot is stamped closed loop from
/// the <c>loopSettings.dosingEnabled</c> of the Loop profile in force, whichever of the two uploads
/// arrives first.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class LoopClosedLoopIntegrationTests : ApiIntegrationTestBase
{
    public LoopClosedLoopIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static string Iso(DateTimeOffset at) => at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static object Schedule(double value) => new[] { new { time = "00:00", value, timeAsSeconds = 0 } };

    /// <summary>A profile as Loop uploads it when its settings change.</summary>
    private static object LoopProfile(DateTimeOffset startDate, bool dosingEnabled, string? id = null) => new
    {
        _id = id ?? Guid.NewGuid().ToString("N")[..24],
        defaultProfile = "Default",
        startDate = Iso(startDate),
        units = "mg/dL",
        enteredBy = "Loop",
        store = new Dictionary<string, object>
        {
            ["Default"] = new
            {
                dia = 6,
                timezone = "Australia/Sydney",
                units = "mg/dL",
                basal = Schedule(0.8),
                carbratio = Schedule(10),
                sens = Schedule(45),
                target_low = Schedule(100),
                target_high = Schedule(110),
            },
        },
        loopSettings = new
        {
            dosingEnabled,
            minimumBGGuard = 75,
            maximumBasalRatePerHour = 3.0,
            maximumBolus = 8.0,
            preMealTargetRange = new[] { 80, 100 },
            overridePresets = new[]
            {
                new { name = "Running", symbol = "R", duration = 3600, insulinNeedsScaleFactor = 0.5, targetRange = new[] { 140, 160 } },
            },
            deviceToken = "1d7a2b6c4f",
            bundleIdentifier = "com.UY678SP37Q.loopkit.Loop",
        },
    };

    /// <summary>A device status as Loop uploads it each cycle.</summary>
    private static object LoopDeviceStatus(DateTimeOffset at) => new
    {
        device = "loop://iPhone",
        created_at = Iso(at),
        pump = new
        {
            clock = Iso(at), pumpID = "1F0A2B", manufacturer = "Insulet", model = "Dash",
            reservoir = 120.5, suspended = false, bolusing = false,
        },
        uploader = new { timestamp = Iso(at), name = "iPhone", battery = 80 },
        loop = new
        {
            name = "Loop",
            version = "3.4.1",
            timestamp = Iso(at),
            iob = new { timestamp = Iso(at), iob = 1.25 },
            cob = new { timestamp = Iso(at), cob = 12.0 },
            predicted = new { startDate = Iso(at), values = new[] { 120.0, 118.4, 116.9, 115.5 } },
            recommendedBolus = 0.0,
            enacted = new { received = true, timestamp = Iso(at), rate = 0.65, duration = 30, bolusVolume = 0.0 },
        },
    };

    private async Task PostAsync(string path, object body)
    {
        var response = await CreateAuthenticatedClient().PostAsJsonAsync(path, body);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    private async Task SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        var response = await CreateAuthenticatedClient().SendAsync(request);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonObject> SettingsStartingAtAsync(DateTimeOffset start)
    {
        var versions = await CreateAuthenticatedClient().GetFromJsonAsync<JsonArray>("/api/v4/profile/settings/by-name/Default");
        return versions!.OfType<JsonObject>()
            .Single(v => v["timestamp"]!.GetValue<DateTimeOffset>() == start);
    }

    private Task<IReadOnlyList<ApsLoopCycle>> CyclesAsync(DateTimeOffset from, DateTimeOffset to) =>
        WithTenantScopeAsync(sp => sp.GetRequiredService<IApsSnapshotRepository>()
            .GetLoopCyclesAsync(from.UtcDateTime, to.UtcDateTime));

    [Fact]
    public async Task ALoopSnapshot_TakesTheDosingEnabledOfTheLoopProfileInForce()
    {
        var open = Now.AddHours(-4);
        var closed = Now.AddHours(-2);
        await PostAsync("/api/v1/profile", LoopProfile(open, dosingEnabled: false));
        await PostAsync("/api/v1/devicestatus", LoopDeviceStatus(open.AddMinutes(5)));
        await PostAsync("/api/v1/profile", LoopProfile(closed, dosingEnabled: true));
        await PostAsync("/api/v1/devicestatus", LoopDeviceStatus(closed.AddMinutes(5)));

        var cycles = await CyclesAsync(open, Now);

        cycles.Select(c => (c.Timestamp, c.Enacted, c.ClosedLoop)).Should().Equal(
            (open.AddMinutes(5).UtcDateTime, true, (bool?)false),
            (closed.AddMinutes(5).UtcDateTime, true, (bool?)true));
        cycles.Select(c => c.EnactedClosedLoop).Should().Equal(false, true);
    }

    [Fact]
    public async Task LoopSnapshotsStoredBeforeTheirLoopProfile_AreStampedWhenItArrives()
    {
        var open = Now.AddHours(-4);
        var closed = Now.AddHours(-2);
        await PostAsync("/api/v1/devicestatus", LoopDeviceStatus(open.AddMinutes(5)));
        await PostAsync("/api/v1/devicestatus", LoopDeviceStatus(closed.AddMinutes(5)));

        (await CyclesAsync(open, Now)).Select(c => c.ClosedLoop).Should().Equal(new bool?[] { null, null });

        await PostAsync("/api/v1/profile", LoopProfile(closed, dosingEnabled: true));
        await PostAsync("/api/v1/profile", LoopProfile(open, dosingEnabled: false));

        (await CyclesAsync(open, Now)).Select(c => c.ClosedLoop).Should().Equal(new bool?[] { false, true });
    }

    [Fact]
    public async Task AnOrefSnapshot_IsNeverStamped()
    {
        var at = Now.AddHours(-1);
        await PostAsync("/api/v1/profile", LoopProfile(at.AddHours(-1), dosingEnabled: false));
        await PostAsync("/api/v1/devicestatus", new
        {
            device = "openaps://Trio",
            created_at = Iso(at),
            openaps = new
            {
                suggested = new { bg = 120, timestamp = Iso(at), reason = "COB: 0", duration = 30, rate = 0.5 },
                enacted = new { bg = 120, timestamp = Iso(at), received = true, duration = 30, rate = 0.5 },
                iob = new { iob = 0.5, timestamp = Iso(at) },
            },
        });

        var cycle = (await CyclesAsync(at.AddMinutes(-1), at.AddMinutes(1))).Should().ContainSingle().Subject;
        cycle.AidAlgorithm.Should().NotBe(AidAlgorithm.Loop);
        cycle.ClosedLoop.Should().BeNull();
        cycle.EnactedClosedLoop.Should().BeTrue();
    }

    [Fact]
    public async Task ReplacingALoopProfileDocument_RestampsTheSnapshotsItGoverns()
    {
        var start = Now.AddHours(-4);
        const string id = "6600000000000000000000a1";
        await PostAsync("/api/v1/profile", LoopProfile(start, dosingEnabled: false, id));
        await PostAsync("/api/v1/devicestatus", LoopDeviceStatus(start.AddMinutes(5)));
        (await CyclesAsync(start, Now)).Select(c => c.ClosedLoop).Should().Equal(new bool?[] { false });

        await SendAsync(HttpMethod.Put, "/api/v1/profile", LoopProfile(start, dosingEnabled: true, id));

        (await CyclesAsync(start, Now)).Select(c => c.ClosedLoop).Should().Equal(new bool?[] { true });
    }

    [Fact]
    public async Task DeletingTheLoopProfileDocumentInForce_FallsBackToTheVersionBefore()
    {
        var open = Now.AddHours(-4);
        var closed = Now.AddHours(-2);
        const string closedId = "6600000000000000000000b2";
        await PostAsync("/api/v1/profile", LoopProfile(open, dosingEnabled: false));
        await PostAsync("/api/v1/profile", LoopProfile(closed, dosingEnabled: true, closedId));
        await PostAsync("/api/v1/devicestatus", LoopDeviceStatus(closed.AddMinutes(5)));
        (await CyclesAsync(open, Now)).Select(c => c.ClosedLoop).Should().Equal(new bool?[] { true });

        await SendAsync(HttpMethod.Delete, $"/api/v1/profile/{closedId}");

        (await CyclesAsync(open, Now)).Select(c => c.ClosedLoop).Should().Equal(new bool?[] { false });
    }

    [Fact]
    public async Task DeletingAndEditingV4TherapySettings_RestampsTheSnapshotsTheyGovern()
    {
        var open = Now.AddHours(-4);
        var closed = Now.AddHours(-2);
        await PostAsync("/api/v1/profile", LoopProfile(open, dosingEnabled: false));
        await PostAsync("/api/v1/profile", LoopProfile(closed, dosingEnabled: true));
        await PostAsync("/api/v1/devicestatus", LoopDeviceStatus(open.AddMinutes(5)));
        await PostAsync("/api/v1/devicestatus", LoopDeviceStatus(closed.AddMinutes(5)));

        var second = await SettingsStartingAtAsync(closed);
        await SendAsync(HttpMethod.Delete, $"/api/v4/profile/settings/{second["id"]}");
        (await CyclesAsync(open, Now)).Select(c => c.ClosedLoop).Should().Equal(new bool?[] { false, false });

        var first = await SettingsStartingAtAsync(open);
        first["loopSettings"]!["dosingEnabled"] = true;
        await SendAsync(HttpMethod.Put, $"/api/v4/profile/settings/{first["id"]}", first);

        (await CyclesAsync(open, Now)).Select(c => c.ClosedLoop).Should().Equal(new bool?[] { true, true });
    }

    [Fact]
    public async Task LoopSnapshotsPostedToTheV4Endpoint_AreStamped()
    {
        var at = Now.AddHours(-1);
        await PostAsync("/api/v1/profile", LoopProfile(at.AddHours(-1), dosingEnabled: false));

        await PostAsync("/api/v4/device-status/aps", new[]
        {
            new { timestamp = Iso(at), aidAlgorithm = "Loop", enacted = true, enactedRate = 0.5 },
        });

        var cycle = (await CyclesAsync(at.AddMinutes(-1), at.AddMinutes(1))).Should().ContainSingle().Subject;
        cycle.ClosedLoop.Should().BeFalse();
        cycle.EnactedClosedLoop.Should().BeFalse();
    }

    [Fact]
    public async Task ABatchImportSpanningWeeks_StampsEverySnapshotItWrote()
    {
        var open = Now.AddDays(-30);
        var closed = Now.AddDays(-10);
        await PostAsync("/api/v1/profile", LoopProfile(open, dosingEnabled: false));
        await PostAsync("/api/v1/profile", LoopProfile(closed, dosingEnabled: true));

        var days = new[] { -29, -20, -12, -9, -1 };
        var statuses = days
            .Select(d => JsonSerializer.Deserialize<DeviceStatus>(JsonSerializer.Serialize(LoopDeviceStatus(Now.AddDays(d))))!)
            .ToList();
        await WithTenantScopeAsync(sp => sp.GetRequiredService<IDeviceStatusService>()
            .ImportDeviceStatusesAsync(statuses, source: null));

        (await CyclesAsync(open, Now)).Select(c => c.ClosedLoop)
            .Should().Equal(new bool?[] { false, false, false, true, true });
    }
}
