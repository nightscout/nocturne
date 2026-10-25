using FluentAssertions;
using Nocturne.API.Services.ChartData;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.ChartData;

public class DashboardChartDataAssemblerTests
{
    [Fact]
    public void Assemble_MapsAllFieldsFromContext()
    {
        // Arrange
        var context = new ChartDataContext
        {
            StartTime = 1700000000000L,
            EndTime = 1700086400000L,
            IntervalMinutes = 5,

            IobSeries = new List<TimeSeriesPoint> { new() { Timestamp = 1700000000000L, Value = 1.5 } },
            CobSeries = new List<TimeSeriesPoint> { new() { Timestamp = 1700000000000L, Value = 20.0 } },
            BasalSeries = new List<BasalPoint> { new() { Timestamp = 1700000000000L, Rate = 0.8, ScheduledRate = 0.9, Origin = BasalDeliveryOrigin.Algorithm, FillColor = ChartColor.GlucoseInRange, StrokeColor = ChartColor.GlucoseHigh } },
            DefaultBasalRate = 0.75,
            MaxBasalRate = 2.5,
            MinIob = -0.4,
            MaxIob = 5.0,
            MaxCob = 60.0,

            GlucoseData = new List<GlucosePointDto> { new() { Time = 1700000000000L, Sgv = 120.0, Direction = "Flat", DataSource = "dexcom" } },
            GlucoseYMax = 300.0,
            Thresholds = new ChartThresholdsDto { Low = 70, High = 180, VeryLow = 54, VeryHigh = 250, GlucoseYMax = 0 },

            BolusMarkers = new List<BolusMarkerDto> { new() { Time = 1700000000000L, Insulin = 2.0, TreatmentId = "t1", BolusType = BolusType.MealBolus, IsOverride = false, DataSource = "loop" } },
            CarbMarkers = new List<CarbMarkerDto> { new() { Time = 1700000000000L, Carbs = 45.0, MealPeriod = MealPeriod.Lunch, TreatmentId = "t2", IsOffset = false, DataSource = "loop" } },
            DeviceEventMarkers = new List<DeviceEventMarkerDto> { new() { Time = 1700000000000L, EventType = DeviceEventType.SiteChange, Notes = "left arm", TreatmentId = "t3", Color = ChartColor.GlucoseVeryLow } },
            BgCheckMarkers = new List<BgCheckMarkerDto> { new() { Time = 1700000000000L, Glucose = 115.0, GlucoseType = "Finger", TreatmentId = "t4" } },

            PumpModeSpans = new List<ChartStateSpanDto> { new() { Id = "s1", Category = StateSpanCategory.PumpMode, State = "Closed", StartMills = 1700000000000L, EndMills = null, Color = ChartColor.InsulinBolus } },
            ProfileSpans = new List<ChartStateSpanDto> { new() { Id = "s2", Category = StateSpanCategory.Profile, State = "Default", StartMills = 1700000000000L, EndMills = null, Color = ChartColor.InsulinBolus } },
            OverrideSpans = new List<ChartStateSpanDto> { new() { Id = "s3", Category = StateSpanCategory.Override, State = "Sport", StartMills = 1700000000000L, EndMills = null, Color = ChartColor.InsulinBolus } },
            ActivitySpans = new List<ChartStateSpanDto> { new() { Id = "s4", Category = StateSpanCategory.Exercise, State = "Run", StartMills = 1700000000000L, EndMills = null, Color = ChartColor.InsulinBolus } },
            TempBasalSpans = new List<ChartStateSpanDto> { new() { Id = "s5", Category = StateSpanCategory.PumpMode, State = "TempBasal", StartMills = 1700000000000L, EndMills = null, Color = ChartColor.InsulinBolus } },
            BasalDeliverySpans = new List<BasalDeliverySpanDto> { new() { Id = "bd1", StartMills = 1700000000000L, EndMills = null, Rate = 0.8, Origin = BasalDeliveryOrigin.Scheduled, Source = "pump", FillColor = ChartColor.GlucoseInRange, StrokeColor = ChartColor.GlucoseHigh } },

            SystemEventMarkers = new List<SystemEventMarkerDto> { new() { Id = "se1", Time = 1700000000000L, EventType = SystemEventType.Alarm, Category = SystemEventCategory.Pump, Code = "E001", Description = "Low battery", Color = ChartColor.GlucoseVeryHigh } },
            TrackerMarkers = new List<TrackerMarkerDto> { new() { Id = "tr1", DefinitionId = "def1", Name = "Insulin change", Category = TrackerCategory.Reservoir, Time = 1700000000000L, Icon = "droplet", Color = ChartColor.InsulinBolus } },
        };

        var assembler = new DashboardChartDataAssembler();

        // Act
        var result = assembler.Assemble(context);

        // Assert — collections: count check
        result.IobSeries.Should().HaveCount(1);
        result.CobSeries.Should().HaveCount(1);
        result.BasalSeries.Should().HaveCount(1);
        result.GlucoseData.Should().HaveCount(1);
        result.BolusMarkers.Should().HaveCount(1);
        result.CarbMarkers.Should().HaveCount(1);
        result.DeviceEventMarkers.Should().HaveCount(1);
        result.BgCheckMarkers.Should().HaveCount(1);
        result.PumpModeSpans.Should().HaveCount(1);
        result.ProfileSpans.Should().HaveCount(1);
        result.OverrideSpans.Should().HaveCount(1);
        result.ActivitySpans.Should().HaveCount(1);
        result.TempBasalSpans.Should().HaveCount(1);
        result.BasalDeliverySpans.Should().HaveCount(1);
        result.SystemEventMarkers.Should().HaveCount(1);
        result.TrackerMarkers.Should().HaveCount(1);

        // Assert — scalar fields
        result.DefaultBasalRate.Should().Be(context.DefaultBasalRate);
        result.MaxBasalRate.Should().Be(context.MaxBasalRate);
        result.MinIob.Should().Be(context.MinIob);
        result.MaxIob.Should().Be(context.MaxIob);
        result.MaxCob.Should().Be(context.MaxCob);

        // Assert — Thresholds with expression: GlucoseYMax comes from context.GlucoseYMax, not context.Thresholds.GlucoseYMax
        result.Thresholds.Low.Should().Be(context.Thresholds.Low);
        result.Thresholds.High.Should().Be(context.Thresholds.High);
        result.Thresholds.VeryLow.Should().Be(context.Thresholds.VeryLow);
        result.Thresholds.VeryHigh.Should().Be(context.Thresholds.VeryHigh);
        result.Thresholds.GlucoseYMax.Should().Be(context.GlucoseYMax);
        result.Thresholds.GlucoseYMax.Should().NotBe(context.Thresholds.GlucoseYMax, "the with expression should override the stale value from context.Thresholds");

        // Assert — collection reference identity (same list instances passed through)
        result.IobSeries.Should().BeSameAs(context.IobSeries);
        result.CobSeries.Should().BeSameAs(context.CobSeries);
        result.BasalSeries.Should().BeSameAs(context.BasalSeries);
        result.GlucoseData.Should().BeSameAs(context.GlucoseData);
        result.BolusMarkers.Should().BeSameAs(context.BolusMarkers);
        result.CarbMarkers.Should().BeSameAs(context.CarbMarkers);
        result.DeviceEventMarkers.Should().BeSameAs(context.DeviceEventMarkers);
        result.BgCheckMarkers.Should().BeSameAs(context.BgCheckMarkers);
        result.PumpModeSpans.Should().BeSameAs(context.PumpModeSpans);
        result.ProfileSpans.Should().BeSameAs(context.ProfileSpans);
        result.OverrideSpans.Should().BeSameAs(context.OverrideSpans);
        result.ActivitySpans.Should().BeSameAs(context.ActivitySpans);
        result.TempBasalSpans.Should().BeSameAs(context.TempBasalSpans);
        result.BasalDeliverySpans.Should().BeSameAs(context.BasalDeliverySpans);
        result.SystemEventMarkers.Should().BeSameAs(context.SystemEventMarkers);
        result.TrackerMarkers.Should().BeSameAs(context.TrackerMarkers);
    }
}
