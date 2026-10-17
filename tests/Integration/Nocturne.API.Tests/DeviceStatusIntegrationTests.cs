using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Integration tests for DeviceStatus and Health endpoints against the API running in-process on real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
[Parity]
public class DeviceStatusIntegrationTests : ApiIntegrationTestBase
{
    public DeviceStatusIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output
    )
        : base(fixture, output) { }

    private static DeviceStatus CreateTestDeviceStatus() =>
        new()
        {
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Device = "openaps://test-rig",
            CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Uploader = new UploaderStatus { Battery = 85 },
        };

    [Fact]
    public async Task PostDeviceStatus_ShouldCreateAndReturnEntry()
    {
        // Arrange
        var client = CreateAuthenticatedClient();
        var deviceStatus = CreateTestDeviceStatus();

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/devicestatus", deviceStatus);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<DeviceStatus[]>(content);

        created.Should().NotBeNullOrEmpty();
        created![0].Device.Should().Be("openaps://test-rig");
        created[0].Id.Should().NotBeNullOrEmpty();

        Log($"Created device status with ID: {created[0].Id}");
    }

    [Fact]
    public async Task GetDeviceStatus_ShouldReturnCreatedEntries()
    {
        // Arrange
        var client = CreateAuthenticatedClient();
        var deviceStatus = CreateTestDeviceStatus();
        await client.PostAsJsonAsync("/api/v1/devicestatus", deviceStatus);

        // Act
        var response = await AuthenticatedClient.GetAsync("/api/v1/devicestatus?count=10");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize<DeviceStatus[]>(content);

        entries.Should().NotBeNullOrEmpty();
        entries!.Should().Contain(e => e.Device == "openaps://test-rig");
    }

    [Fact]
    public async Task GetDeviceStatusById_ShouldReturnMatchingEntry()
    {
        // Arrange
        var client = CreateAuthenticatedClient();
        var deviceStatus = CreateTestDeviceStatus();
        var postResponse = await client.PostAsJsonAsync("/api/v1/devicestatus", deviceStatus);
        var created = JsonSerializer.Deserialize<DeviceStatus[]>(
            await postResponse.Content.ReadAsStringAsync()
        );
        var id = created![0].Id;

        // Act - use find query to locate by created_at since there is no GET by ID endpoint
        var response = await AuthenticatedClient.GetAsync($"/api/v1/devicestatus?count=50");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize<DeviceStatus[]>(content);

        entries.Should().NotBeNullOrEmpty();
        entries!.Should().Contain(e => e.Id == id);

        var matched = entries.First(e => e.Id == id);
        matched.Device.Should().Be("openaps://test-rig");

        Log($"Found device status by ID: {id}");
    }

    [Fact]
    public async Task DeleteDeviceStatusById_ShouldRemoveEntry()
    {
        // Arrange
        var client = CreateAuthenticatedClient();
        var deviceStatus = CreateTestDeviceStatus();
        var postResponse = await client.PostAsJsonAsync("/api/v1/devicestatus", deviceStatus);
        var created = JsonSerializer.Deserialize<DeviceStatus[]>(
            await postResponse.Content.ReadAsStringAsync()
        );
        var id = created![0].Id;

        // Act
        var deleteResponse = await client.DeleteAsync($"/api/v1/devicestatus/{id}");

        // Assert
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await deleteResponse.Content.ReadFromJsonAsync<JsonElement>();
        status.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        status.GetProperty("deletedCount").GetInt64().Should().Be(1);
        status.GetProperty("n").GetInt64().Should().Be(1);

        // Verify it no longer appears in the list
        var listResponse = await AuthenticatedClient.GetAsync("/api/v1/devicestatus?count=50");
        var content = await listResponse.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize<DeviceStatus[]>(content);

        entries.Should().NotContain(e => e.Id == id);

        Log($"Deleted device status with ID: {id}");
    }

    [Fact]
    public async Task DeleteDeviceStatus_UnknownId_AnswersOkWithNoneDeleted()
    {
        var response = await CreateAuthenticatedClient().DeleteAsync("/api/v1/devicestatus/000000000000000000000000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        status.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        status.GetProperty("deletedCount").GetInt64().Should().Be(0);
        status.GetProperty("n").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task DeleteDeviceStatus_AnyIdWithFind_DeletesTheMatches()
    {
        var client = CreateAuthenticatedClient();
        var device = $"test://wildcard-{Guid.NewGuid():N}";
        var statuses = new[] { CreateTestDeviceStatus(), CreateTestDeviceStatus() };
        statuses[1].Mills -= 60_000;
        statuses[1].CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(statuses[1].Mills)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        foreach (var status in statuses)
        {
            status.Device = device;
            status.Pump = new PumpStatus { Clock = status.CreatedAt, Reservoir = 100 };
        }
        (await client.PostAsJsonAsync("/api/v1/devicestatus", statuses))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.DeleteAsync($"/api/v1/devicestatus/*?find[device]={device}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("deletedCount").GetInt64().Should().Be(2);
        var remaining = JsonSerializer.Deserialize<DeviceStatus[]>(
            await client.GetStringAsync("/api/v1/devicestatus?count=50"));
        remaining.Should().NotContain(s => s.Device == device);
    }

    [Fact]
    public async Task DeleteDeviceStatus_AnyIdWithoutQuery_IsRefused()
    {
        var response = await CreateAuthenticatedClient().DeleteAsync("/api/v1/devicestatus/*");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UploaderOnlyStatus_IsListedCountedAndInHistory()
    {
        var at = UniqueTime();
        var cursor = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();
        await PostAsync(new { created_at = Iso(at), mills = Mills(at), device = "xDrip-DexcomG6", uploader = new { battery = 41 } });

        var listed = (await ListAsync(at, at)).Should().ContainSingle().Subject;
        listed.GetProperty("uploader").GetProperty("battery").GetInt32().Should().Be(41);
        (await CountAsync(at, at)).Should().Be(1);
        (await HistoryAsync(cursor)).Should().ContainSingle(doc => CreatedAt(doc) == Iso(at))
            .Which.GetProperty("uploader").GetProperty("battery").GetInt32().Should().Be(41);
    }

    [Fact]
    public async Task PumpStatusWithoutAps_CarriesItsUploaderAndExtras()
    {
        var at = UniqueTime();
        await PostAsync(new
        {
            created_at = Iso(at), mills = Mills(at), device = "medtronic-600://phone",
            pump = new { clock = Iso(at), reservoir = 87.5 },
            uploader = new { battery = 63 },
            connect = new { sensorState = "OK" },
        });

        var listed = (await ListAsync(at, at)).Should().ContainSingle().Subject;
        listed.GetProperty("pump").GetProperty("reservoir").GetDouble().Should().Be(87.5);
        listed.GetProperty("uploader").GetProperty("battery").GetInt32().Should().Be(63);
        listed.GetProperty("connect").GetProperty("sensorState").GetString().Should().Be("OK");
        (await CountAsync(at, at)).Should().Be(1);
    }

    [Fact]
    public async Task ExtrasOnlyStatus_IsListedCountedAndInHistory()
    {
        var at = UniqueTime();
        var cursor = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();
        await PostAsync(new { created_at = Iso(at), mills = Mills(at), device = "xdripjs://rig", xdripjs = new { state = 6, stateString = "OK" } });

        var listed = (await ListAsync(at, at)).Should().ContainSingle().Subject;
        listed.GetProperty("xdripjs").GetProperty("stateString").GetString().Should().Be("OK");
        (await CountAsync(at, at)).Should().Be(1);
        (await HistoryAsync(cursor)).Should().ContainSingle(doc => CreatedAt(doc) == Iso(at));
    }

    [Fact]
    public async Task StatusesOfEveryKind_PageNewestFirstEachOnce()
    {
        var newest = UniqueTime();
        var times = Enumerable.Range(0, 4).Select(i => newest.AddMinutes(-i)).ToArray();
        await PostAsync(new
        {
            created_at = Iso(times[0]), mills = Mills(times[0]), device = "openaps://rig",
            openaps = new { suggested = new { bg = 120, timestamp = Iso(times[0]) } },
        });
        await PostAsync(new { created_at = Iso(times[1]), mills = Mills(times[1]), device = "pump://rig", pump = new { clock = Iso(times[1]), reservoir = 50 } });
        await PostAsync(new { created_at = Iso(times[2]), mills = Mills(times[2]), device = "xdrip://phone", uploader = new { battery = 70 } });
        await PostAsync(new { created_at = Iso(times[3]), mills = Mills(times[3]), device = "xdripjs://rig", xdripjs = new { state = 6 } });

        var pages = new List<string?>();
        for (var skip = 0; skip < 5; skip++)
            pages.AddRange((await ListAsync(times[^1], newest, $"&count=1&skip={skip}")).Select(CreatedAt));

        pages.Should().Equal(times.Select(Iso));
        (await CountAsync(times[^1], newest)).Should().Be(4);
    }

    [Fact]
    public async Task HealthCheck_ShouldReturnOk()
    {
        // Act
        var response = await ApiClient.GetAsync("/health");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static DateTime UniqueTime() =>
        new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(Random.Shared.Next(0, 50_000) * 10);

    private static string Iso(DateTime at) => at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static long Mills(DateTime at) => new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static string? CreatedAt(JsonElement doc) =>
        doc.TryGetProperty("created_at", out var createdAt) ? createdAt.GetString() : null;

    private static string Window(DateTime from, DateTime to) =>
        $"find[created_at][$gte]={Iso(from)}&find[created_at][$lte]={Iso(to)}";

    private async Task PostAsync(object status) =>
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/devicestatus", status))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    private async Task<List<JsonElement>> ListAsync(DateTime from, DateTime to, string paging = "&count=50") =>
        (await AuthenticatedClient.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/devicestatus?{Window(from, to)}{paging}"))!;

    private async Task<long> CountAsync(DateTime from, DateTime to)
    {
        var rows = await AuthenticatedClient.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/count/devicestatus/where?{Window(from, to)}");
        return rows!.Count == 0 ? 0 : rows.Single().GetProperty("count").GetInt64();
    }

    private async Task<List<JsonElement>> HistoryAsync(long cursor)
    {
        var body = await AuthenticatedClient.GetFromJsonAsync<JsonElement>(
            $"/api/v3/devicestatus/history/{cursor}?limit=1000");
        return body.GetProperty("result").EnumerateArray().ToList();
    }
}
