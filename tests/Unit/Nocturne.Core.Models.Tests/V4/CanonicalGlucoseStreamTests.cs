using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

[Trait("Category", "Unit")]
public class CanonicalGlucoseStreamTests
{
    // 12:00:00Z sits on a 5-minute boundary, so offsets within [0,5) share a bucket.
    private static readonly DateTime T0 = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static PatientDevice Cgm(int? rank = null, DateOnly? start = null, DateTime? created = null) => new()
    {
        Id = Guid.NewGuid(),
        DeviceCategory = DeviceCategory.CGM,
        Manufacturer = "Test",
        Model = "Test CGM",
        Rank = rank,
        StartDate = start,
        CreatedAt = created ?? T0.AddDays(-30),
    };

    private static SensorGlucose Reading(double minutes, Guid? deviceId = null, string? source = null, string? device = null) => new()
    {
        Id = Guid.NewGuid(),
        Mgdl = 120,
        Timestamp = T0.AddMinutes(minutes),
        PatientDeviceId = deviceId,
        DataSource = source,
        Device = device,
    };

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        CanonicalGlucoseStream.Select([], []).Should().BeEmpty();
    }

    [Fact]
    public void SingleStream_ReturnsInputUnchanged()
    {
        var device = Cgm(rank: 1);
        var readings = new List<SensorGlucose> { Reading(0, device.Id), Reading(5, device.Id), Reading(10, device.Id) };

        var result = CanonicalGlucoseStream.Select(readings, [device]);

        result.Should().BeSameAs(readings);
    }

    [Fact]
    public void TwoDevices_HigherRankWinsEveryContestedBucket()
    {
        var primary = Cgm(rank: 1);
        var secondary = Cgm(rank: 2);
        var readings = new List<SensorGlucose>
        {
            Reading(0, primary.Id), Reading(1, secondary.Id),
            Reading(5, primary.Id), Reading(6, secondary.Id),
        };

        var result = CanonicalGlucoseStream.Select(readings, [primary, secondary]);

        result.Should().OnlyContain(r => r.PatientDeviceId == primary.Id);
        result.Should().HaveCount(2);
    }

    [Fact]
    public void SecondaryFillsBucketsThePrimaryMisses()
    {
        var primary = Cgm(rank: 1);
        var secondary = Cgm(rank: 2);
        var readings = new List<SensorGlucose>
        {
            Reading(0, primary.Id),
            Reading(6, secondary.Id),   // primary silent 12:05-12:10 → secondary fills
            Reading(10, primary.Id),
        };

        var result = CanonicalGlucoseStream.Select(readings, [primary, secondary]);

        result.Should().HaveCount(3);
        result[1].PatientDeviceId.Should().Be(secondary.Id);
    }

    [Fact]
    public void WinnersFullDensityIsKeptWithinTheBucket()
    {
        // 1-minute Libre-style primary vs 5-minute secondary: all five primary readings survive.
        var primary = Cgm(rank: 1);
        var secondary = Cgm(rank: 2);
        var readings = new List<SensorGlucose>
        {
            Reading(0, primary.Id), Reading(1, primary.Id), Reading(2, primary.Id),
            Reading(3, primary.Id), Reading(4, primary.Id),
            Reading(2.5, secondary.Id),
        };

        var result = CanonicalGlucoseStream.Select(readings, [primary, secondary]);

        result.Should().HaveCount(5);
        result.Should().OnlyContain(r => r.PatientDeviceId == primary.Id);
    }

    [Fact]
    public void UnrankedDevices_MostRecentStartDateWins()
    {
        var older = Cgm(start: new DateOnly(2026, 1, 1));
        var newer = Cgm(start: new DateOnly(2026, 6, 1));
        var readings = new List<SensorGlucose> { Reading(0, older.Id), Reading(1, newer.Id) };

        var result = CanonicalGlucoseStream.Select(readings, [older, newer]);

        result.Should().ContainSingle().Which.PatientDeviceId.Should().Be(newer.Id);
    }

    [Fact]
    public void RankedDeviceBeatsUnranked_RegardlessOfStartDate()
    {
        var ranked = Cgm(rank: 5, start: new DateOnly(2026, 1, 1));
        var unranked = Cgm(start: new DateOnly(2026, 6, 1));
        var readings = new List<SensorGlucose> { Reading(0, ranked.Id), Reading(1, unranked.Id) };

        var result = CanonicalGlucoseStream.Select(readings, [ranked, unranked]);

        result.Should().ContainSingle().Which.PatientDeviceId.Should().Be(ranked.Id);
    }

    [Fact]
    public void RegisteredDeviceBeatsPseudoDevice()
    {
        var device = Cgm(rank: 1);
        var readings = new List<SensorGlucose>
        {
            Reading(0, device.Id),
            Reading(1, source: "xdrip", device: "xDrip-DexcomG6"),
        };

        var result = CanonicalGlucoseStream.Select(readings, [device]);

        result.Should().ContainSingle().Which.PatientDeviceId.Should().Be(device.Id);
    }

    [Fact]
    public void PseudoDeviceFillsBucketsRegisteredDevicesMiss()
    {
        var device = Cgm(rank: 1);
        var readings = new List<SensorGlucose>
        {
            Reading(0, device.Id),
            Reading(6, source: "xdrip", device: "phone"),
        };

        var result = CanonicalGlucoseStream.Select(readings, [device]);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void TwoPseudoDevices_StitchDeterministically_NotBlended()
    {
        var readings = new List<SensorGlucose>
        {
            Reading(0, source: "dexcom-connector", device: "share"),
            Reading(1, source: "libre-connector", device: "linkup"),
            Reading(5, source: "dexcom-connector", device: "share"),
            Reading(6, source: "libre-connector", device: "linkup"),
        };

        var result = CanonicalGlucoseStream.Select(readings, []);

        // Ordinal key order: dexcom-connector < libre-connector — same winner both buckets.
        result.Should().HaveCount(2);
        result.Should().OnlyContain(r => r.DataSource == "dexcom-connector");
    }

    [Fact]
    public void UnknownPatientDeviceId_TreatedAsPseudoDevice_BelowRegistered()
    {
        var registered = Cgm(rank: 1);
        var deleted = Guid.NewGuid();
        var readings = new List<SensorGlucose> { Reading(0, registered.Id), Reading(1, deleted) };

        var result = CanonicalGlucoseStream.Select(readings, [registered]);

        result.Should().ContainSingle().Which.PatientDeviceId.Should().Be(registered.Id);
    }

    [Fact]
    public void InputOrderIsPreserved()
    {
        var primary = Cgm(rank: 1);
        var secondary = Cgm(rank: 2);
        // Descending order, as v1 queries return.
        var readings = new List<SensorGlucose>
        {
            Reading(10, primary.Id), Reading(6, secondary.Id), Reading(0, primary.Id),
        };

        var result = CanonicalGlucoseStream.Select(readings, [primary, secondary]);

        result.Select(r => r.Timestamp).Should().BeInDescendingOrder();
        result.Should().HaveCount(3);
    }

    [Fact]
    public void BucketBoundary_SplitsAtAlignedFiveMinutes()
    {
        var primary = Cgm(rank: 1);
        var secondary = Cgm(rank: 2);
        var readings = new List<SensorGlucose>
        {
            Reading(4.5, secondary.Id),  // bucket [12:00,12:05): contested, primary wins below
            Reading(4.0, primary.Id),
            Reading(5.5, secondary.Id),  // bucket [12:05,12:10): secondary alone → kept
        };

        var result = CanonicalGlucoseStream.Select(readings, [primary, secondary]);

        result.Should().HaveCount(2);
        result.Select(r => r.PatientDeviceId).Should().Equal(primary.Id, secondary.Id);
    }
}
