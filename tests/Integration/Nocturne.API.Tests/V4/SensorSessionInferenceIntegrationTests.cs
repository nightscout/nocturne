using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.V4;

/// <summary>
/// Registering a CGM gives it a sensor session history: the readings it already explains are
/// attributed to it, and the warm-up gap in them becomes an inferred sensor start.
/// </summary>
[Collection(ApiIntegrationShards.One)]
[Trait("Category", "Integration")]
public class SensorSessionInferenceIntegrationTests : ApiIntegrationTestBase
{
    public SensorSessionInferenceIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task RegisteringACgm_InfersTheSensorChangeInItsExistingReadings()
    {
        using var client = CreateAuthenticatedClient();
        var serial = Guid.NewGuid().ToString("N")[..12];
        var now = DateTime.UtcNow;
        var oldSensorEnds = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddDays(-4);
        var newSensorBegins = oldSensorEnds.AddMinutes(40);

        var readings = new List<object>();
        for (var t = oldSensorEnds.AddDays(-1); t <= oldSensorEnds; t = t.AddMinutes(5))
            readings.Add(Reading(t, serial));
        for (var t = newSensorBegins; t <= newSensorBegins.AddDays(1); t = t.AddMinutes(5))
            readings.Add(Reading(t, serial));
        (await client.PostAsJsonAsync("/api/v4/glucose/sensor/bulk", readings))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var registered = await client.PostAsJsonAsync("/api/v4/patient-record/devices", new
        {
            deviceCategory = "CGM",
            manufacturer = "Dexcom",
            model = "G7",
            catalogId = "dexcom-g7",
            serialNumber = serial,
            isCurrent = true,
        });
        registered.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var cleanup = new DeviceCleanup(client, await IdOfAsync(registered));

        var query = $"/api/v4/observations/device-events?from={oldSensorEnds.AddHours(-1):O}&to={newSensorBegins.AddHours(1):O}" +
            $"&source={DataSources.SensorSessionInference}";
        using var body = JsonDocument.Parse(await client.GetStringAsync(query));
        var events = body.RootElement.GetProperty("data").EnumerateArray().ToList();

        events.Should().ContainSingle();
        events[0].GetProperty("timestamp").GetDateTime().ToUniversalTime().Should().Be(newSensorBegins.AddMinutes(-30));
    }

    /// <summary>
    /// An edit's withdrawal is the system's, not the editing user's: a user deletion would dismiss
    /// the gap for good, and the start could never be inferred again once the gap reopened.
    /// </summary>
    [Fact]
    public async Task AStartAnEditWithdrew_IsInferredAgainWhenItsGapReopens()
    {
        using var client = CreateAuthenticatedClient();
        var serial = Guid.NewGuid().ToString("N")[..12];
        var now = DateTime.UtcNow;
        var oldSensorEnds = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddDays(-8);
        var newSensorBegins = oldSensorEnds.AddMinutes(40);

        var readings = new List<object>();
        for (var t = oldSensorEnds.AddDays(-1); t <= oldSensorEnds; t = t.AddMinutes(5))
            readings.Add(Reading(t, serial));
        for (var t = newSensorBegins; t <= newSensorBegins.AddDays(1); t = t.AddMinutes(5))
            readings.Add(Reading(t, serial));
        (await client.PostAsJsonAsync("/api/v4/glucose/sensor/bulk", readings)).EnsureSuccessStatusCode();

        var device = new
        {
            deviceCategory = "CGM",
            manufacturer = "Dexcom",
            model = "G7",
            catalogId = "dexcom-g7",
            serialNumber = serial,
            isCurrent = true,
        };
        var registered = await client.PostAsJsonAsync("/api/v4/patient-record/devices", device);
        registered.EnsureSuccessStatusCode();
        var deviceId = await IdOfAsync(registered);
        await using var cleanup = new DeviceCleanup(client, deviceId);
        (await InferredStartsAsync(client, oldSensorEnds, newSensorBegins)).Should().ContainSingle();

        var fill = new List<object>();
        for (var t = oldSensorEnds.AddMinutes(5); t < newSensorBegins; t = t.AddMinutes(5))
            fill.Add(Reading(t, serial));
        var filled = await client.PostAsJsonAsync("/api/v4/glucose/sensor/bulk", fill);
        filled.EnsureSuccessStatusCode();
        var fillIds = JsonDocument.Parse(await filled.Content.ReadAsStringAsync()).RootElement
            .EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();

        await EditStartDateAsync(client, deviceId, device, oldSensorEnds.AddDays(-3));
        (await InferredStartsAsync(client, oldSensorEnds, newSensorBegins)).Should().BeEmpty("the filled gap is no sensor change");

        foreach (var id in fillIds)
            (await client.DeleteAsync($"/api/v4/glucose/sensor/{id}")).EnsureSuccessStatusCode();
        await EditStartDateAsync(client, deviceId, device, oldSensorEnds.AddDays(-2));

        (await InferredStartsAsync(client, oldSensorEnds, newSensorBegins)).Should().ContainSingle();
    }

    private static async Task EditStartDateAsync(HttpClient client, Guid deviceId, object device, DateTime startDate)
    {
        var json = JsonSerializer.SerializeToNode(device)!.AsObject();
        json["id"] = deviceId;
        json["startDate"] = DateOnly.FromDateTime(startDate).ToString("yyyy-MM-dd");
        var response = await client.PutAsJsonAsync($"/api/v4/patient-record/devices/{deviceId}", json);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<JsonElement>> InferredStartsAsync(HttpClient client, DateTime oldSensorEnds, DateTime newSensorBegins)
    {
        var query = $"/api/v4/observations/device-events?from={oldSensorEnds.AddHours(-1):O}&to={newSensorBegins.AddHours(1):O}" +
            $"&source={DataSources.SensorSessionInference}";
        using var body = JsonDocument.Parse(await client.GetStringAsync(query));
        return body.RootElement.GetProperty("data").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static async Task<Guid> IdOfAsync(HttpResponseMessage registered) =>
        JsonDocument.Parse(await registered.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

    /// <summary>
    /// Deletes the test's device: the tenant is shared, and a Dexcom left registered is where the
    /// stamper would attribute the next test's readings before that test's own device exists.
    /// </summary>
    private sealed class DeviceCleanup(HttpClient client, Guid deviceId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await client.DeleteAsync($"/api/v4/patient-record/devices/{deviceId}");
    }

    /// <summary>
    /// A reading carrying its sensor's serial, which tells the stamper whose it is among the other
    /// tests' Dexcoms registered in the shared tenant.
    /// </summary>
    private static object Reading(DateTime at, string serial) => new
    {
        timestamp = new DateTimeOffset(at),
        mgdl = 120,
        dataSource = DataSources.DexcomConnector,
        device = $"{DataSources.DexcomConnector} {serial}",
    };
}
