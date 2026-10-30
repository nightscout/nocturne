using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

[Trait("Category", "Unit")]
public class SensorGlucoseSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

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
    public void NullQualityAndSensorStatus_AreOmitted()
    {
        var root = JsonDocument.Parse(JsonSerializer.Serialize(new SensorGlucose { Mgdl = 120 }, Options)).RootElement;

        root.TryGetProperty("quality", out _).Should().BeFalse();
        root.TryGetProperty("sensorStatus", out _).Should().BeFalse();
    }

    [Fact]
    public void QualityAndSensorStatus_AreWrittenWhenSet()
    {
        var reading = new SensorGlucose { Mgdl = 76, Quality = SensorGlucoseQuality.Invalid, SensorStatus = "G7:Errors/info2" };
        var root = JsonDocument.Parse(JsonSerializer.Serialize(reading, Options)).RootElement;

        root.GetProperty("quality").GetString().Should().Be("Invalid");
        root.GetProperty("sensorStatus").GetString().Should().Be("G7:Errors/info2");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(SensorGlucoseQuality.Valid, true)]
    [InlineData(SensorGlucoseQuality.Uncertain, true)]
    [InlineData(SensorGlucoseQuality.Invalid, false)]
    public void CountsInStatistics_LeavesOutOnlyInvalid(SensorGlucoseQuality? quality, bool counts)
    {
        new SensorGlucose { Mgdl = 76, Quality = quality }.CountsInStatistics().Should().Be(counts);
    }
}
