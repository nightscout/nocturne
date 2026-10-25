using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Extensions;
using Nocturne.Core.Models.Projections;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.Entries;

[Trait("Category", "Unit")]
public class EntryResponseQualityTests
{
    private static JsonElement V1(Entry entry) =>
        JsonDocument.Parse(JsonSerializer.Serialize(entry.ToV1Response())).RootElement;

    private static JsonElement V3(Entry entry) =>
        JsonDocument.Parse(JsonSerializer.Serialize(entry.ToV3Response())).RootElement;

    private static Entry Flagged() => EntryProjection.FromSensorGlucose(new SensorGlucose
    {
        Id = Guid.CreateVersion7(),
        Timestamp = DateTime.UtcNow,
        Mgdl = 76,
        Quality = SensorGlucoseQuality.Invalid,
        SensorStatus = "G7:Errors",
    });

    [Fact]
    public void V1AndV3_WriteTheProjectedQualityAndSensorStatus()
    {
        foreach (var root in new[] { V1(Flagged()), V3(Flagged()) })
        {
            root.GetProperty("quality").GetString().Should().Be("Invalid");
            root.GetProperty("sensorStatus").GetString().Should().Be("G7:Errors");
        }
    }

    [Fact]
    public void V1AndV3_OmitThemWhenTheReadingHasNone()
    {
        var entry = EntryProjection.FromSensorGlucose(new SensorGlucose
        {
            Id = Guid.CreateVersion7(),
            Timestamp = DateTime.UtcNow,
            Mgdl = 120,
        });

        foreach (var root in new[] { V1(entry), V3(entry) })
        {
            root.TryGetProperty("quality", out _).Should().BeFalse();
            root.TryGetProperty("sensorStatus", out _).Should().BeFalse();
        }
    }

    [Fact]
    public void V1_ReadsAnExtraKeyThatArrivedAsJson()
    {
        var entry = JsonSerializer.Deserialize<Entry>(
            """{"type":"sgv","sgv":140,"date":1794833191967,"quality":"Uncertain","sensorStatus":"L3:notActionable"}""")!;

        var root = V1(entry);

        root.GetProperty("quality").GetString().Should().Be("Uncertain");
        root.GetProperty("sensorStatus").GetString().Should().Be("L3:notActionable");
    }
}
