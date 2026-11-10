using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Projections;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

[Trait("Category", "Unit")]
public class SensorGlucoseSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly Guid RecordId = Guid.Parse("0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f");

    [Fact]
    public void NullRssi_IsOmitted()
    {
        var json = JsonSerializer.Serialize(new SensorGlucose { Mgdl = 120 }, Options);

        JsonDocument.Parse(json).RootElement.TryGetProperty("rssi", out _).Should().BeFalse();
    }

    [Fact]
    public void Rssi_IsWrittenWhenSet()
    {
        var json = JsonSerializer.Serialize(new SensorGlucose { Mgdl = 120, Rssi = -70 }, Options);

        JsonDocument.Parse(json).RootElement.GetProperty("rssi").GetInt32().Should().Be(-70);
    }

    [Fact]
    public void LegacyApiId_IsServedOnTheWire()
    {
        var reading = new SensorGlucose { Id = RecordId, Timestamp = DateTime.UtcNow, Mgdl = 120 };

        var json = JsonSerializer.Serialize(reading, Options);

        JsonDocument.Parse(json).RootElement.GetProperty("legacyApiId").GetString().Should().Be("0198c2a41f3b7c2d9e556a1b");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("65f1c2a41f3b7c2d9e556a1b")]
    [InlineData("0198c2a4-1f3b-7c2d-9e55-aaaaaaaaaaaa")]
    [InlineData("dexcom_1700000000000")]
    public void LegacyApiId_MatchesTheIdTheRealtimeEventCarries(string? legacyId)
    {
        var reading = new SensorGlucose
        {
            Id = RecordId,
            LegacyId = legacyId,
            Timestamp = DateTime.UtcNow,
            Mgdl = 120,
        };

        var wire = JsonSerializer.SerializeToElement(EntryProjection.FromSensorGlucose(reading));

        reading.LegacyApiId.Should().Be(wire.GetProperty("_id").GetString());
    }
}
