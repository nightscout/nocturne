using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

[Trait("Category", "Unit")]
public class DeviceEventSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RecordedEvent_OmitsIsInferred()
    {
        var json = JsonSerializer.Serialize(
            new DeviceEvent { EventType = DeviceEventType.SensorStart },
            Options
        );

        JsonDocument.Parse(json).RootElement.TryGetProperty("isInferred", out _).Should().BeFalse();
    }

    [Fact]
    public void InferredEvent_WritesIsInferredTrue()
    {
        var json = JsonSerializer.Serialize(
            new DeviceEvent
            {
                EventType = DeviceEventType.SensorStart,
                DataSource = DataSources.SensorSessionInference,
            },
            Options
        );

        JsonDocument.Parse(json).RootElement.GetProperty("isInferred").GetBoolean().Should().BeTrue();
    }
}
