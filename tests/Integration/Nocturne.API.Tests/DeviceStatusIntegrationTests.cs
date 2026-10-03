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
    public async Task HealthCheck_ShouldReturnOk()
    {
        // Act
        var response = await ApiClient.GetAsync("/health");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
