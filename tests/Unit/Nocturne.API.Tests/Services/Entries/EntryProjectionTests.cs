using FluentAssertions;
using Nocturne.API.Services.Entries;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Entries;

public class EntryProjectionTests
{
    private static readonly DateTime TestTimestamp = new(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long TestMills = new DateTimeOffset(TestTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly Guid TestId = Guid.Parse("01961234-5678-7aaa-bbbb-ccccddddeeee");

    // -------------------------------------------------------------------------
    // FromSensorGlucose
    // -------------------------------------------------------------------------

    [Fact]
    public void FromSensorGlucose_SetsTypeSgv()
    {
        var sg = CreateSensorGlucose();

        var entry = EntryProjection.FromSensorGlucose(sg);

        entry.Type.Should().Be("sgv");
    }

    [Fact]
    public void FromSensorGlucose_UsesLegacyIdWhenPresent()
    {
        var sg = CreateSensorGlucose();
        sg.LegacyId = "abc123legacy";

        var entry = EntryProjection.FromSensorGlucose(sg);

        entry.Id.Should().Be("abc123legacy");
    }

    [Fact]
    public void FromSensorGlucose_FallsBackToGuidIdWhenNoLegacyId()
    {
        var sg = CreateSensorGlucose();
        sg.LegacyId = null;

        var entry = EntryProjection.FromSensorGlucose(sg);

        entry.Id.Should().Be(TestId.ToString());
    }

    [Fact]
    public void FromSensorGlucose_MapsMillsFromTimestamp()
    {
        var sg = CreateSensorGlucose();

        var entry = EntryProjection.FromSensorGlucose(sg);

        entry.Mills.Should().Be(TestMills);
    }

    [Fact]
    public void FromSensorGlucose_MapsGlucoseValues()
    {
        var sg = CreateSensorGlucose();
        sg.Mgdl = 120;

        var entry = EntryProjection.FromSensorGlucose(sg);

        entry.Mgdl.Should().Be(120);
        entry.Sgv.Should().Be(120);
        entry.Mmol.Should().BeApproximately(120 / 18.0182, 0.001);
    }

    [Fact]
    public void FromSensorGlucose_MapsDirection()
    {
        var sg = CreateSensorGlucose();
        sg.Direction = GlucoseDirection.FortyFiveUp;

        var entry = EntryProjection.FromSensorGlucose(sg);

        entry.Direction.Should().Be("FortyFiveUp");
    }

    [Fact]
    public void FromSensorGlucose_NullDirection_MapsToNull()
    {
        var sg = CreateSensorGlucose();
        sg.Direction = null;

        var entry = EntryProjection.FromSensorGlucose(sg);

        entry.Direction.Should().BeNull();
    }

    [Fact]
    public void FromSensorGlucose_MapsTrend()
    {
        var sg = CreateSensorGlucose();
        sg.Direction = GlucoseDirection.Flat;

        var entry = EntryProjection.FromSensorGlucose(sg);

        entry.Trend.Should().Be((int)GlucoseTrend.Flat);
    }

    [Fact]
    public void FromSensorGlucose_MapsSensorSpecificFields()
    {
        var sg = CreateSensorGlucose();
        sg.TrendRate = 1.5;
        sg.Noise = 2;
        sg.Filtered = 100_000;
        sg.Unfiltered = 110_000;
        sg.Delta = 3.5;

        var entry = EntryProjection.FromSensorGlucose(sg);

        entry.TrendRate.Should().Be(1.5);
        entry.Noise.Should().Be(2);
        entry.Filtered.Should().Be(100_000);
        entry.Unfiltered.Should().Be(110_000);
        entry.Delta.Should().Be(3.5);
    }

    [Fact]
    public void FromSensorGlucose_MapsCommonFields()
    {
        var sg = CreateSensorGlucose();
        sg.Device = "xDrip-DexcomG6";
        sg.App = "xDrip";
        sg.DataSource = "dexcom-connector";
        sg.UtcOffset = -300;

        var entry = EntryProjection.FromSensorGlucose(sg);

        entry.Device.Should().Be("xDrip-DexcomG6");
        entry.App.Should().Be("xDrip");
        entry.DataSource.Should().Be("dexcom-connector");
        entry.UtcOffset.Should().Be(-300);
    }

    [Fact]
    public void FromSensorGlucose_SetsIsValidTrue()
    {
        var sg = CreateSensorGlucose();

        var entry = EntryProjection.FromSensorGlucose(sg);

        entry.IsValid.Should().BeTrue();
    }

    [Fact]
    public void FromSensorGlucose_SetsDateString()
    {
        var sg = CreateSensorGlucose();

        var entry = EntryProjection.FromSensorGlucose(sg);

        entry.DateString.Should().Be("2026-04-24T12:00:00.000Z");
    }

    [Fact]
    public void FromSensorGlucose_SetsSrvModifiedFromModifiedAt()
    {
        var sg = CreateSensorGlucose();
        var modifiedAt = new DateTime(2026, 4, 24, 13, 0, 0, DateTimeKind.Utc);
        sg.ModifiedAt = modifiedAt;

        var entry = EntryProjection.FromSensorGlucose(sg);

        entry.SrvModified.Should().Be(new DateTimeOffset(modifiedAt, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    // -------------------------------------------------------------------------
    // FromMeterGlucose
    // -------------------------------------------------------------------------

    [Fact]
    public void FromMeterGlucose_SetsTypeMbg()
    {
        var mg = CreateMeterGlucose();

        var entry = EntryProjection.FromMeterGlucose(mg);

        entry.Type.Should().Be("mbg");
    }

    [Fact]
    public void FromMeterGlucose_UsesLegacyIdWhenPresent()
    {
        var mg = CreateMeterGlucose();
        mg.LegacyId = "mbg_legacy_id";

        var entry = EntryProjection.FromMeterGlucose(mg);

        entry.Id.Should().Be("mbg_legacy_id");
    }

    [Fact]
    public void FromMeterGlucose_FallsBackToGuidId()
    {
        var mg = CreateMeterGlucose();
        mg.LegacyId = null;

        var entry = EntryProjection.FromMeterGlucose(mg);

        entry.Id.Should().Be(TestId.ToString());
    }

    [Fact]
    public void FromMeterGlucose_MapsGlucoseValues()
    {
        var mg = CreateMeterGlucose();
        mg.Mgdl = 95;

        var entry = EntryProjection.FromMeterGlucose(mg);

        entry.Mgdl.Should().Be(95);
        entry.Mbg.Should().Be(95);
    }

    [Fact]
    public void FromMeterGlucose_MapsCommonFields()
    {
        var mg = CreateMeterGlucose();
        mg.Device = "Contour Next";
        mg.App = "xDrip";
        mg.DataSource = "manual";
        mg.UtcOffset = 60;

        var entry = EntryProjection.FromMeterGlucose(mg);

        entry.Device.Should().Be("Contour Next");
        entry.App.Should().Be("xDrip");
        entry.DataSource.Should().Be("manual");
        entry.UtcOffset.Should().Be(60);
    }

    [Fact]
    public void FromMeterGlucose_SetsIsValidTrue()
    {
        var mg = CreateMeterGlucose();

        var entry = EntryProjection.FromMeterGlucose(mg);

        entry.IsValid.Should().BeTrue();
    }

    [Fact]
    public void FromMeterGlucose_SetsDateString()
    {
        var mg = CreateMeterGlucose();

        var entry = EntryProjection.FromMeterGlucose(mg);

        entry.DateString.Should().Be("2026-04-24T12:00:00.000Z");
    }

    // -------------------------------------------------------------------------
    // FromCalibration
    // -------------------------------------------------------------------------

    [Fact]
    public void FromCalibration_SetsTypeCal()
    {
        var cal = CreateCalibration();

        var entry = EntryProjection.FromCalibration(cal);

        entry.Type.Should().Be("cal");
    }

    [Fact]
    public void FromCalibration_UsesLegacyIdWhenPresent()
    {
        var cal = CreateCalibration();
        cal.LegacyId = "cal_legacy_id";

        var entry = EntryProjection.FromCalibration(cal);

        entry.Id.Should().Be("cal_legacy_id");
    }

    [Fact]
    public void FromCalibration_FallsBackToGuidId()
    {
        var cal = CreateCalibration();
        cal.LegacyId = null;

        var entry = EntryProjection.FromCalibration(cal);

        entry.Id.Should().Be(TestId.ToString());
    }

    [Fact]
    public void FromCalibration_MapsCalibrationFields()
    {
        var cal = CreateCalibration();
        cal.Slope = 828.3;
        cal.Intercept = 32456.2;
        cal.Scale = 1.0;

        var entry = EntryProjection.FromCalibration(cal);

        entry.Slope.Should().Be(828.3);
        entry.Intercept.Should().Be(32456.2);
        entry.Scale.Should().Be(1.0);
    }

    [Fact]
    public void FromCalibration_SetsIsCalibrationTrue()
    {
        var cal = CreateCalibration();

        var entry = EntryProjection.FromCalibration(cal);

        entry.IsCalibration.Should().BeTrue();
    }

    [Fact]
    public void FromCalibration_MapsCommonFields()
    {
        var cal = CreateCalibration();
        cal.Device = "xDrip-DexcomG4";
        cal.App = "xDrip";
        cal.DataSource = "xdrip-connector";
        cal.UtcOffset = -480;

        var entry = EntryProjection.FromCalibration(cal);

        entry.Device.Should().Be("xDrip-DexcomG4");
        entry.App.Should().Be("xDrip");
        entry.DataSource.Should().Be("xdrip-connector");
        entry.UtcOffset.Should().Be(-480);
    }

    [Fact]
    public void FromCalibration_SetsIsValidTrue()
    {
        var cal = CreateCalibration();

        var entry = EntryProjection.FromCalibration(cal);

        entry.IsValid.Should().BeTrue();
    }

    [Fact]
    public void FromCalibration_SetsDateString()
    {
        var cal = CreateCalibration();

        var entry = EntryProjection.FromCalibration(cal);

        entry.DateString.Should().Be("2026-04-24T12:00:00.000Z");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static SensorGlucose CreateSensorGlucose() => new()
    {
        Id = TestId,
        Timestamp = TestTimestamp,
        Mgdl = 120,
        CreatedAt = TestTimestamp,
        ModifiedAt = TestTimestamp,
    };

    private static MeterGlucose CreateMeterGlucose() => new()
    {
        Id = TestId,
        Timestamp = TestTimestamp,
        Mgdl = 95,
        CreatedAt = TestTimestamp,
        ModifiedAt = TestTimestamp,
    };

    private static Calibration CreateCalibration() => new()
    {
        Id = TestId,
        Timestamp = TestTimestamp,
        CreatedAt = TestTimestamp,
        ModifiedAt = TestTimestamp,
    };
}
