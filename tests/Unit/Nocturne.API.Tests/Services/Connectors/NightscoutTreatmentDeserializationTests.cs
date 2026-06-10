using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Regression tests for the connector treatment-ingestion path.
///
/// Many real-world Nightscout uploaders emit numeric treatment fields as JSON
/// strings (e.g. <c>"insulin":"1.5"</c>, <c>"carbs":"45"</c>, <c>"duration":"30"</c>). The
/// connector deserializes treatment pages with <see cref="JsonDefaults.CaseInsensitive"/>;
/// before flexible numeric converters were registered there, a single string-encoded
/// number threw a <see cref="JsonException"/> that aborted the entire page fetch. Because
/// glucose entries (clean numeric) are fetched separately and succeeded, affected tenants
/// ended up with full glucose history but no treatment (bolus/carb/basal) backfill.
/// </summary>
public class NightscoutTreatmentDeserializationTests
{
    [Fact]
    public void Treatments_WithStringEncodedNumbers_DeserializeWithoutThrowing()
    {
        // A treatments page as emitted by an uploader that stringifies numeric fields.
        var json = """
        [
            {
                "eventType": "Meal Bolus",
                "insulin": "1.5",
                "carbs": "45",
                "duration": "0",
                "created_at": "2026-01-15T08:30:00.000Z"
            },
            {
                "eventType": "Temp Basal",
                "absolute": "0.75",
                "rate": "0.75",
                "duration": "30",
                "created_at": "2026-01-15T09:00:00.000Z"
            }
        ]
        """;

        var act = () => JsonSerializer.Deserialize<Treatment[]>(json, JsonDefaults.CaseInsensitive);

        act.Should().NotThrow<JsonException>();

        var treatments = JsonSerializer.Deserialize<Treatment[]>(json, JsonDefaults.CaseInsensitive);
        treatments.Should().HaveCount(2);

        treatments![0].EventType.Should().Be("Meal Bolus");
        treatments[0].Insulin.Should().Be(1.5);
        treatments[0].Carbs.Should().Be(45);

        treatments[1].EventType.Should().Be("Temp Basal");
        treatments[1].Absolute.Should().Be(0.75);
        treatments[1].Rate.Should().Be(0.75);
        treatments[1].Duration.Should().Be(30);
    }

    [Fact]
    public void Treatments_WithNumericTypedNumbers_StillDeserialize()
    {
        // The common case must keep working: native JSON numbers.
        var json = """
        [
            {
                "eventType": "Correction Bolus",
                "insulin": 1.2,
                "carbs": 0,
                "created_at": "2026-01-15T10:00:00.000Z"
            }
        ]
        """;

        var treatments = JsonSerializer.Deserialize<Treatment[]>(json, JsonDefaults.CaseInsensitive);

        treatments.Should().ContainSingle();
        treatments![0].Insulin.Should().Be(1.2);
    }

    [Theory]
    [InlineData("\"1.5\"", 1.5)]
    [InlineData("2.25", 2.25)]
    [InlineData("\"\"", null)]
    [InlineData("null", null)]
    public void JsonDefaults_CoercesStringEncodedDecimals(string jsonValue, double? expected)
    {
        // Defensive coverage for any connector-deserialized model carrying decimal fields
        // (the numeric converters apply to every type these options touch).
        var result = JsonSerializer.Deserialize<decimal?>(jsonValue, JsonDefaults.CaseInsensitive);

        result.Should().Be(expected.HasValue ? (decimal)expected.Value : null);
    }

    [Fact]
    public void Treatments_WithStringOrIntBooleans_DeserializeWithoutThrowing()
    {
        // Uploaders variously send booleans as "true"/"false", 1/0, or native bools.
        // The model's bool/bool? fields (isValid, automatic, ...) lack per-property converters,
        // so the connector's flexible boolean converters must absorb these.
        var json = """
        [
            { "eventType": "SMB", "automatic": "true", "isValid": 1, "created_at": "2026-01-15T08:30:00.000Z" },
            { "eventType": "Meal Bolus", "automatic": false, "isValid": "0", "created_at": "2026-01-15T09:00:00.000Z" }
        ]
        """;

        var act = () => JsonSerializer.Deserialize<Treatment[]>(json, JsonDefaults.CaseInsensitive);

        act.Should().NotThrow<JsonException>();
        var treatments = JsonSerializer.Deserialize<Treatment[]>(json, JsonDefaults.CaseInsensitive);
        treatments.Should().HaveCount(2);
        treatments![0].Automatic.Should().Be(true);
    }

    [Fact]
    public void DeviceStatus_WithStringTypedNumbersAndBooleans_DeserializeWithoutThrowing()
    {
        // Device status is the most heterogeneous Nightscout collection (pump/loop/openaps).
        // Numbers and booleans in the nested payload routinely arrive as strings; the registered
        // converters apply to nested types too.
        var json = """
        [
            {
                "device": "openaps://pi",
                "isCharging": "true",
                "uploader": { "battery": "82", "batteryVoltage": "4.1" },
                "pump": {
                    "reservoir": "120.5",
                    "battery": { "percent": "75", "voltage": "1.45" },
                    "status": { "bolusing": "false", "suspended": 0 }
                },
                "created_at": "2026-01-15T08:30:00.000Z"
            }
        ]
        """;

        var act = () => JsonSerializer.Deserialize<DeviceStatus[]>(json, JsonDefaults.CaseInsensitive);

        act.Should().NotThrow<JsonException>();
        var statuses = JsonSerializer.Deserialize<DeviceStatus[]>(json, JsonDefaults.CaseInsensitive);
        statuses.Should().ContainSingle();
        statuses![0].Pump!.Reservoir.Should().Be(120.5);
        statuses[0].IsCharging.Should().Be(true);
    }
}
