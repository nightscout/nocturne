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
}
