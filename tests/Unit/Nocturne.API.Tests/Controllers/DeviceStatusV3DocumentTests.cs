using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;
using V3DeviceStatusController = Nocturne.API.Controllers.V3.DeviceStatusController;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// A v3 devicestatus document carries every key the status stores, as v1 does, and the same
/// identifier under <c>_id</c> and <c>identifier</c>.
/// </summary>
[Trait("Category", "Unit")]
public class DeviceStatusV3DocumentTests
{
    private const string StoredId = "507f1f77bcf86cd799439011";

    private static DeviceStatus Parse(string json) =>
        JsonSerializer.Deserialize<DeviceStatus>(json)!;

    [Fact]
    public void Document_carries_the_typed_keys_v3_used_to_drop()
    {
        var status = Parse(
            """
            {
              "_id": "507f1f77bcf86cd799439011",
              "device": "openaps://phone",
              "created_at": "2026-01-01T00:00:00.000Z",
              "date": 1767225600000,
              "isCharging": true,
              "override": { "active": true },
              "xdripjs": { "state": 6 },
              "cgm": { "type": "x" }
            }
            """);

        var document = V3DeviceStatusController.ToDocument(status);

        document.Select(p => p.Key)
            .Should()
            .Contain(["date", "isCharging", "override", "xdripjs", "cgm"]);
        document["isCharging"]!.GetValue<bool>().Should().BeTrue();
        document["date"]!.GetValue<long>().Should().Be(1767225600000);
    }

    [Fact]
    public void Document_carries_extension_keys_whole()
    {
        var status = Parse(
            """
            {
              "_id": "507f1f77bcf86cd799439011",
              "device": "openaps://phone",
              "app": "AAPS",
              "configuration": { "pump": "Combo", "insulin": 5 }
            }
            """);

        var document = V3DeviceStatusController.ToDocument(status);

        document["app"]!.GetValue<string>().Should().Be("AAPS");
        document["configuration"]!["pump"]!.GetValue<string>().Should().Be("Combo");
        document["configuration"]!["insulin"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public void Document_serves_one_identifier_as_id_and_identifier()
    {
        var document = V3DeviceStatusController.ToDocument(
            new DeviceStatus { Id = StoredId, Device = "openaps://phone" });

        document["_id"]!.GetValue<string>().Should().Be(StoredId);
        document["identifier"]!.GetValue<string>().Should().Be(StoredId);
    }

    [Fact]
    public void Document_keeps_uploaderBattery_and_leaves_isValid_out_until_set()
    {
        var unset = V3DeviceStatusController.ToDocument(
            new DeviceStatus { Id = StoredId, Device = "d" });
        unset.ContainsKey("uploaderBattery").Should().BeTrue();
        unset.ContainsKey("isValid").Should().BeFalse();

        var set = V3DeviceStatusController.ToDocument(
            new DeviceStatus { Id = StoredId, Device = "d", IsValid = true });
        set["isValid"]!.GetValue<bool>().Should().BeTrue();
    }
}
