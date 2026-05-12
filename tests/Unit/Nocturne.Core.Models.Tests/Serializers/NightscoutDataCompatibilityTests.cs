using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests.Serializers;

/// <summary>
/// Tests that Nocturne correctly handles real-world Nightscout data patterns
/// found in the OpenAPS Data Commons (OpenHumans n=8 sample).
///
/// Each test documents a specific data pattern observed in the wild and verifies
/// that Nocturne can ingest it without data loss or errors.
/// </summary>
public class NightscoutDataCompatibilityTests
{
    // ========================================================================
    // Issue #5 — Entry.Direction as integer (HIGH)
    //
    // Older Nightscout records send direction as a numeric value (e.g., 9)
    // instead of a string like "Flat". System.Text.Json will throw a
    // JsonException when deserializing an integer into string? Direction.
    // ========================================================================

    [Fact]
    public void Entry_Direction_DeserializesIntegerValue()
    {
        var json = """{"direction": 9, "sgv": 120}""";

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry.Should().NotBeNull();
        entry!.Direction.Should().NotBeNull();
    }

    [Fact]
    public void Entry_Direction_DeserializesStringValue()
    {
        var json = """{"direction": "Flat", "sgv": 120}""";

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry!.Direction.Should().Be("Flat");
    }

    [Fact]
    public void Entry_Direction_DeserializesNullValue()
    {
        var json = """{"direction": null, "sgv": 120}""";

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry!.Direction.Should().BeNull();
    }

    [Fact]
    public void Entry_Direction_DeserializesEmptyString()
    {
        var json = """{"direction": "", "sgv": 120}""";

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry!.Direction.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, "DoubleUp")]
    [InlineData(2, "SingleUp")]
    [InlineData(3, "FortyFiveUp")]
    [InlineData(4, "Flat")]
    [InlineData(5, "FortyFiveDown")]
    [InlineData(6, "SingleDown")]
    [InlineData(7, "DoubleDown")]
    [InlineData(8, "NOT COMPUTABLE")]
    [InlineData(9, "RATE OUT OF RANGE")]
    public void Entry_Direction_MapsIntegerToKnownDirectionString(int numericDirection, string expectedDirection)
    {
        var json = $$"""{"direction": {{numericDirection}}, "sgv": 120}""";

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry!.Direction.Should().Be(expectedDirection);
    }

    // ========================================================================
    // Issue #14 — Profile TimeValue.TimeAsSeconds as string (MEDIUM)
    //
    // Real Nightscout profiles send timeAsSeconds as a string ("0", "10800")
    // but the model uses int? with no FlexibleConverter.
    // ========================================================================

    [Fact]
    public void Profile_TimeAsSeconds_DeserializesStringValue()
    {
        var json = """
        {
            "store": {
                "Default": {
                    "basal": [{"time": "00:00", "value": "0.35", "timeAsSeconds": "0"}],
                    "carbratio": [], "sens": [], "target_low": [], "target_high": []
                }
            }
        }
        """;

        var profile = JsonSerializer.Deserialize<Profile>(json);

        profile!.Store["Default"].Basal.Should().HaveCount(1);
        profile.Store["Default"].Basal[0].TimeAsSeconds.Should().Be(0);
    }

    [Fact]
    public void Profile_TimeAsSeconds_DeserializesNumericValue()
    {
        var json = """
        {
            "store": {
                "Default": {
                    "basal": [{"time": "03:00", "value": "0.35", "timeAsSeconds": 10800}],
                    "carbratio": [], "sens": [], "target_low": [], "target_high": []
                }
            }
        }
        """;

        var profile = JsonSerializer.Deserialize<Profile>(json);

        profile!.Store["Default"].Basal[0].TimeAsSeconds.Should().Be(10800);
    }

    [Fact]
    public void Profile_TimeAsSeconds_DeserializesMissingAsNull()
    {
        var json = """
        {
            "store": {
                "Default": {
                    "basal": [{"time": "00:00", "value": "0.35"}],
                    "carbratio": [], "sens": [], "target_low": [], "target_high": []
                }
            }
        }
        """;

        var profile = JsonSerializer.Deserialize<Profile>(json);

        profile!.Store["Default"].Basal[0].TimeAsSeconds.Should().BeNull();
    }

    // ========================================================================
    // Issue #4 — Entry dateString in US format (MEDIUM)
    //
    // Older records use "10/09/2016 16:34:21 PM" instead of ISO 8601.
    // Entry.Mills getter uses DateTime.TryParse with RoundtripKind.
    // ========================================================================

    [Fact]
    public void Entry_DateString_ParsesUsFormat()
    {
        var json = """{"dateString": "10/09/2016 4:34:21 PM", "sgv": 120}""";

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry!.DateString.Should().Be("10/09/2016 4:34:21 PM");
        // Mills should be calculable from this dateString
        entry.Mills.Should().BeGreaterThan(0, "US-format dateString should be parseable to mills");
    }

    [Fact]
    public void Entry_DateString_ParsesIsoFormat()
    {
        var json = """{"dateString": "2017-10-08T19:58:22.681-0400", "sgv": 120}""";

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry!.Mills.Should().BeGreaterThan(0);
    }

    // ========================================================================
    // Issues #6-10 — Medtronic nested objects on treatments (MEDIUM)
    //
    // OpenAPS/Medtronic treatments include rich nested objects:
    // raw_rate, raw_duration, bolus, wizard, medtronic URI, _type, links, uuid
    // These are silently dropped since Treatment has no JsonExtensionData.
    // ========================================================================

    [Fact]
    public void Treatment_DeserializesWithNestedMedtronicObjects()
    {
        var json = """
        {
            "enteredBy": "H7UC20",
            "duration": 30,
            "insulin": null,
            "absolute": 0.75,
            "rate": 0.75,
            "eventType": "Temp Basal",
            "raw_rate": {
                "_description": "TempBasal 2017-10-08T22:21:55",
                "rate": 0.75,
                "_type": "TempBasal",
                "temp": "absolute"
            },
            "raw_duration": {
                "_description": "TempBasalDuration 2017-10-08T22:21:55",
                "duration (min)": 30,
                "_type": "TempBasalDuration"
            },
            "carbs": null,
            "_id": "59daddd0c7d5afdddbc99331",
            "medtronic": "mm://openaps/mm-format-ns-treatments/Temp Basal",
            "created_at": "2017-10-08T22:21:55-04:00"
        }
        """;

        var treatment = JsonSerializer.Deserialize<Treatment>(json);

        treatment.Should().NotBeNull();
        treatment!.EventType.Should().Be("Temp Basal");
        treatment.Rate.Should().Be(0.75);
        treatment.Duration.Should().Be(30);
    }

    [Fact]
    public void Treatment_RoundTripsNestedMedtronicObjects()
    {
        var json = """
        {
            "enteredBy": "H7UC20",
            "duration": 30,
            "rate": 0.75,
            "eventType": "Temp Basal",
            "raw_rate": {
                "_description": "TempBasal 2017-10-08T22:21:55",
                "rate": 0.75,
                "_type": "TempBasal",
                "temp": "absolute"
            },
            "raw_duration": {
                "duration (min)": 30,
                "_type": "TempBasalDuration"
            },
            "medtronic": "mm://openaps/mm-format-ns-treatments/Temp Basal",
            "_id": "59daddd0c7d5afdddbc99331",
            "created_at": "2017-10-08T22:21:55-04:00"
        }
        """;

        var treatment = JsonSerializer.Deserialize<Treatment>(json);
        var reserialized = JsonSerializer.Serialize(treatment);
        var roundTripped = JsonSerializer.Deserialize<JsonDocument>(reserialized);

        roundTripped!.RootElement.TryGetProperty("raw_rate", out _).Should().BeTrue(
            "raw_rate nested object should be preserved through round-trip");
        roundTripped.RootElement.TryGetProperty("raw_duration", out _).Should().BeTrue(
            "raw_duration nested object should be preserved through round-trip");
        roundTripped.RootElement.TryGetProperty("medtronic", out _).Should().BeTrue(
            "medtronic URI should be preserved through round-trip");
    }

    [Fact]
    public void Treatment_PreservesMedtronicTypeField()
    {
        var json = """
        {
            "eventType": "Note",
            "_type": "PumpSuspend",
            "notes": "Pump suspended",
            "medtronic": "mm://openaps/mm-format-ns-treatments/PumpSuspend",
            "_id": "59daddd0c7d5afdddbc99332",
            "created_at": "2017-10-08T22:21:55-04:00"
        }
        """;

        var treatment = JsonSerializer.Deserialize<Treatment>(json);
        var reserialized = JsonSerializer.Serialize(treatment);
        var roundTripped = JsonSerializer.Deserialize<JsonDocument>(reserialized);

        roundTripped!.RootElement.TryGetProperty("_type", out var typeValue).Should().BeTrue(
            "_type (Medtronic pump event type) should be preserved");
        typeValue.GetString().Should().Be("PumpSuspend");
    }

    [Fact]
    public void Treatment_DeserializesBolusWizardNestedObjects()
    {
        var json = """
        {
            "eventType": "Correction Bolus",
            "insulin": 1.2,
            "bolus": {
                "programmed": 1.2,
                "amount": 1.2,
                "duration": 0,
                "type": "normal"
            },
            "wizard": {
                "carb_input": 0,
                "carb_ratio": 33,
                "sensitivity": 110,
                "bg": 180,
                "bg_target_low": 100,
                "bg_target_high": 100,
                "correction_estimate": 0.73,
                "bolus_estimate": 0.7,
                "unabsorbed_insulin_total": 0
            },
            "_id": "59daddd0c7d5afdddbc99333",
            "created_at": "2017-10-08T22:21:55-04:00"
        }
        """;

        var treatment = JsonSerializer.Deserialize<Treatment>(json);
        var reserialized = JsonSerializer.Serialize(treatment);
        var roundTripped = JsonSerializer.Deserialize<JsonDocument>(reserialized);

        roundTripped!.RootElement.TryGetProperty("bolus", out _).Should().BeTrue(
            "bolus nested object should be preserved");
        roundTripped.RootElement.TryGetProperty("wizard", out _).Should().BeTrue(
            "wizard nested object should be preserved");
    }

    // ========================================================================
    // Bug — Entry "date" field (Unix ms) not mapped to Mills
    //
    // Nightscout v1 entries API returns "date" (Unix milliseconds) as the
    // primary timestamp, but the Entry model maps Mills from "mills" only.
    // "date" falls into AdditionalProperties and Mills returns 0 (epoch 1970).
    // When "dateString" is also absent, the migrator imports BG data at 1970.
    // ========================================================================

    [Fact]
    public void Entry_Mills_ResolvesFromDateFieldWhenMillsMissing()
    {
        // Nightscout entry with "date" but no "mills" and no "dateString"
        var json = """{"sgv": 120, "type": "sgv", "date": 1507507102681}""";

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry.Should().NotBeNull();
        entry!.Mills.Should().Be(1507507102681,
            "Mills should resolve from the Nightscout 'date' field when 'mills' is absent");
    }

    [Fact]
    public void Entry_Mills_PrefersMillsOverDateField()
    {
        // When both "mills" and "date" are present, "mills" wins
        var json = """{"sgv": 120, "mills": 1507507102681, "date": 9999999999999}""";

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry!.Mills.Should().Be(1507507102681,
            "Mills should use the 'mills' value when both 'mills' and 'date' are present");
    }

    [Fact]
    public void Entry_Mills_ResolvesFromDateFieldInFullRecord()
    {
        // Real-world Nightscout entry from AAPS — has "date" but no "mills"
        var json = """
        {
            "sgv": 108,
            "filtered": 142941.16575,
            "unfiltered": 142941.16575,
            "direction": "Flat",
            "device": "xDrip-LibreAlarm",
            "noise": 0,
            "type": "sgv",
            "date": 1507507102681,
            "_id": "59dabbb7c7d5afdddbc99300"
        }
        """;

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry!.Mills.Should().Be(1507507102681,
            "full Nightscout entry with 'date' field should resolve Mills correctly");
    }

    // ========================================================================
    // Issues #2, #3 — Entry glucose/rawbg fields (LOW)
    //
    // Some entries have a "glucose" field (duplicate of sgv) and "rawbg" from
    // the Glimp app. These aren't on the Entry model and get silently dropped.
    // ========================================================================

    [Fact]
    public void Entry_DeserializesWithGlucoseField()
    {
        var json = """{"sgv": 120, "glucose": 120, "type": "sgv", "date": 1507507102681}""";

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry.Should().NotBeNull();
        entry!.Sgv.Should().Be(120);
    }

    [Fact]
    public void Entry_DeserializesWithRawbgField()
    {
        var json = """{"sgv": 95, "rawbg": 98, "type": "sgv", "date": 1507507102681}""";

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry.Should().NotBeNull();
        entry!.Sgv.Should().Be(95);
    }

    // ========================================================================
    // Issue #11 — units variant spellings (LOW)
    //
    // Sample data contains "mg/dl", "mgdl", and "mg/dL" in treatments.
    // ========================================================================

    [Fact]
    public void Treatment_AcceptsAllUnitsVariants()
    {
        var variants = new[] { "mg/dl", "mgdl", "mg/dL", "mmol", "mmol/L" };

        foreach (var units in variants)
        {
            var json = $$"""{"units": "{{units}}", "eventType": "BG Check", "glucose": 120}""";

            var treatment = JsonSerializer.Deserialize<Treatment>(json);

            treatment.Should().NotBeNull($"units value '{units}' should be accepted");
            treatment!.Units.Should().Be(units);
        }
    }

    // ========================================================================
    // Full real-world record tests — representative samples from the dataset
    // ========================================================================

    [Fact]
    public void Entry_DeserializesFullXDripDexcomShareRecord()
    {
        var json = """
        {
            "delta": 12.02,
            "direction": "Flat",
            "sgv": 179,
            "filtered": 196000,
            "noise": 1,
            "dateString": "2017-10-08T19:58:22.681-0400",
            "sysTime": "2017-10-08T19:58:22.681-0400",
            "device": "xDrip-DexcomShare",
            "rssi": 100,
            "_id": "59dabbb7c7d5afdddbc992f4",
            "type": "sgv",
            "unfiltered": 203000,
            "date": 1507507102681
        }
        """;

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry.Should().NotBeNull();
        entry!.Sgv.Should().Be(179);
        entry.Direction.Should().Be("Flat");
        entry.Delta.Should().Be(12.02);
        entry.Filtered.Should().Be(196000);
        entry.Unfiltered.Should().Be(203000);
        entry.Noise.Should().Be(1);
        entry.Device.Should().Be("xDrip-DexcomShare");
        entry.Type.Should().Be("sgv");
        entry.Rssi.Should().Be(100);
        entry.Id.Should().Be("59dabbb7c7d5afdddbc992f4");
    }

    [Fact]
    public void Entry_DeserializesLibreAlarmRecordWithFloatFiltered()
    {
        var json = """
        {
            "sgv": 108,
            "filtered": 142941.16575,
            "unfiltered": 142941.16575,
            "direction": "Flat",
            "device": "xDrip-LibreAlarm",
            "noise": 0,
            "type": "sgv",
            "date": 1507507102681,
            "_id": "59dabbb7c7d5afdddbc99300"
        }
        """;

        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry.Should().NotBeNull();
        entry!.Filtered.Should().BeApproximately(142941.16575, 0.001);
        entry.Noise.Should().Be(0);
    }

    [Fact]
    public void Profile_DeserializesFullRealWorldProfile()
    {
        var json = """
        {
            "units": "mg/dl",
            "startDate": "2017-07-02T00:22:00.000Z",
            "defaultProfile": "Default",
            "store": {
                "Default": {
                    "dia": "5",
                    "units": "mg/dl",
                    "sens": [{"value": "110", "time": "00:00", "timeAsSeconds": "0"}],
                    "target_low": [{"value": "100", "time": "00:00", "timeAsSeconds": "0"}],
                    "target_high": [{"value": "100", "time": "00:00", "timeAsSeconds": "0"}],
                    "carbratio": [{"value": "33", "time": "00:00", "timeAsSeconds": "0"}],
                    "basal": [
                        {"value": "0.35", "time": "00:00", "timeAsSeconds": "0"},
                        {"value": "0.35", "time": "03:00", "timeAsSeconds": "10800"},
                        {"value": "0.35", "time": "05:00", "timeAsSeconds": "18000"},
                        {"value": "0.3", "time": "07:00", "timeAsSeconds": "25200"},
                        {"value": "0.45", "time": "17:00", "timeAsSeconds": "61200"},
                        {"value": "0.35", "time": "19:00", "timeAsSeconds": "68400"}
                    ],
                    "carbs_hr": "20",
                    "delay": "20",
                    "timezone": "US/Eastern",
                    "startDate": "1970-01-01T00:00:00.000Z"
                }
            },
            "mills": "1498954920000",
            "_id": "59583d7db676066b88af2842",
            "created_at": "2017-07-02T00:25:33.154Z"
        }
        """;

        var profile = JsonSerializer.Deserialize<Profile>(json);

        profile.Should().NotBeNull();
        profile!.DefaultProfile.Should().Be("Default");
        profile.Mills.Should().Be(1498954920000);
        profile.Units.Should().Be("mg/dl");

        var defaultProfile = profile.Store["Default"];
        defaultProfile.Dia.Should().Be(5.0);
        defaultProfile.CarbsHr.Should().Be(20);
        defaultProfile.Delay.Should().Be(20);
        defaultProfile.Timezone.Should().Be("US/Eastern");

        defaultProfile.Basal.Should().HaveCount(6);
        defaultProfile.Basal[0].Value.Should().Be(0.35);
        defaultProfile.Basal[0].TimeAsSeconds.Should().Be(0);
        defaultProfile.Basal[1].TimeAsSeconds.Should().Be(10800);

        defaultProfile.Sens[0].Value.Should().Be(110);
        defaultProfile.CarbRatio[0].Value.Should().Be(33);
    }

    [Fact]
    public void Treatment_DeserializesFullMedtronicTempBasal()
    {
        var json = """
        {
            "enteredBy": "H7UC20",
            "duration": 30,
            "insulin": null,
            "absolute": 0.75,
            "timestamp": "2017-10-08T22:21:55-04:00",
            "rate": 0.75,
            "eventType": "Temp Basal",
            "raw_rate": {
                "_description": "TempBasal 2017-10-08T22:21:55 head[2], body[1] op[0x33]",
                "timestamp": "2017-10-08T22:21:55-04:00",
                "_type": "TempBasal",
                "_date": "b795164811",
                "rate": 0.75,
                "_body": "00",
                "_head": "331e",
                "temp": "absolute"
            },
            "carbs": null,
            "_id": "59daddd0c7d5afdddbc99331",
            "medtronic": "mm://openaps/mm-format-ns-treatments/Temp Basal",
            "raw_duration": {
                "_description": "TempBasalDuration 2017-10-08T22:21:55 head[2], body[0] op[0x16]",
                "duration (min)": 30,
                "timestamp": "2017-10-08T22:21:55-04:00",
                "_type": "TempBasalDuration",
                "_date": "b795164811",
                "_body": "",
                "_head": "1601"
            },
            "created_at": "2017-10-08T22:21:55-04:00"
        }
        """;

        var treatment = JsonSerializer.Deserialize<Treatment>(json);

        treatment.Should().NotBeNull();
        treatment!.EventType.Should().Be("Temp Basal");
        treatment.Rate.Should().Be(0.75);
        treatment.Absolute.Should().Be(0.75);
        treatment.Duration.Should().Be(30);
        treatment.EnteredBy.Should().Be("H7UC20");
    }
}
