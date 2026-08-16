using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

/// <summary>
/// Characterizes the record-type → device-category map. The device-event half is deliberately
/// pinned member by member: a new sensor-ish <see cref="DeviceEventType"/> falls into the pump half
/// by default, which would silently back-stamp CGM history to an insulin pump, so adding one must
/// break here and force a classification decision.
/// </summary>
[Trait("Category", "Unit")]
public class DeviceAttributionCategoriesTests
{
    [Fact]
    public void SensorEventTypes_AreExactlyTheCgmLifecycleEvents()
    {
        DeviceAttributionCategories.SensorEventTypes.Should().BeEquivalentTo(new[]
        {
            DeviceEventType.SensorStart,
            DeviceEventType.SensorChange,
            DeviceEventType.SensorStop,
            DeviceEventType.TransmitterSensorInsert,
        });
    }

    /// <summary>
    /// Pins the pump half member by member as well, so a new <see cref="DeviceEventType"/> fails here
    /// rather than defaulting into the pump half unexamined.
    /// </summary>
    [Fact]
    public void PumpEventTypes_AreExactlyTheRemainingLifecycleEvents()
    {
        DeviceAttributionCategories.PumpEventTypes.Should().BeEquivalentTo(new[]
        {
            DeviceEventType.SiteChange,
            DeviceEventType.InsulinChange,
            DeviceEventType.PumpBatteryChange,
            DeviceEventType.PodChange,
            DeviceEventType.ReservoirChange,
            DeviceEventType.CannulaChange,
            DeviceEventType.PodActivated,
            DeviceEventType.PodDeactivated,
            DeviceEventType.PumpSuspend,
            DeviceEventType.PumpResume,
            DeviceEventType.Priming,
            DeviceEventType.TubePriming,
            DeviceEventType.NeedlePriming,
            DeviceEventType.Rewind,
            DeviceEventType.DateChanged,
            DeviceEventType.TimeChanged,
            DeviceEventType.BolusMaxChanged,
            DeviceEventType.BasalMaxChanged,
            DeviceEventType.ProfileSwitch,
        });
    }

    [Fact]
    public void SensorAndPumpEventTypes_PartitionTheEnum()
    {
        var all = Enum.GetValues<DeviceEventType>();

        DeviceAttributionCategories.SensorEventTypes
            .Concat(DeviceAttributionCategories.PumpEventTypes)
            .Should().BeEquivalentTo(all, "every event type must be classified exactly once");
        DeviceAttributionCategories.PumpEventTypes
            .Should().NotIntersectWith(DeviceAttributionCategories.SensorEventTypes);
    }

    [Theory]
    [InlineData(DeviceEventType.SensorStart, DeviceCategory.CGM)]
    [InlineData(DeviceEventType.SensorChange, DeviceCategory.CGM)]
    [InlineData(DeviceEventType.SensorStop, DeviceCategory.CGM)]
    [InlineData(DeviceEventType.TransmitterSensorInsert, DeviceCategory.CGM)]
    [InlineData(DeviceEventType.SiteChange, DeviceCategory.InsulinPump)]
    [InlineData(DeviceEventType.CannulaChange, DeviceCategory.InsulinPump)]
    [InlineData(DeviceEventType.ReservoirChange, DeviceCategory.InsulinPump)]
    [InlineData(DeviceEventType.PumpBatteryChange, DeviceCategory.InsulinPump)]
    [InlineData(DeviceEventType.PodChange, DeviceCategory.InsulinPump)]
    [InlineData(DeviceEventType.ProfileSwitch, DeviceCategory.InsulinPump)]
    public void DeviceEvent_MapsEachEventTypeToItsOwningCategory(DeviceEventType eventType, DeviceCategory expected)
    {
        DeviceAttributionCategories.DeviceEvent(eventType).Should().Equal(expected);
        DeviceAttributionCategories.IsSensorEvent(eventType).Should().Be(expected == DeviceCategory.CGM);
    }

    [Fact]
    public void RecordTypes_MapToTheCategoriesIngestStampsThemWith()
    {
        DeviceAttributionCategories.SensorGlucose.Should().Equal(DeviceCategory.CGM);
        DeviceAttributionCategories.MeterGlucose.Should().Equal(DeviceCategory.GlucoseMeter);
        DeviceAttributionCategories.Bolus.Should().Equal(DeviceCategory.InsulinPump, DeviceCategory.SmartPen);
        DeviceAttributionCategories.TempBasal.Should().Equal(DeviceCategory.InsulinPump);
        DeviceAttributionCategories.BasalInjection.Should().Equal(DeviceCategory.InsulinPen, DeviceCategory.SmartPen);
    }

    [Fact]
    public void NoRecordTypeIsOwnedByAnUploader()
    {
        var uploaderOwns = new[]
        {
            DeviceAttributionCategories.SensorGlucose,
            DeviceAttributionCategories.MeterGlucose,
            DeviceAttributionCategories.Bolus,
            DeviceAttributionCategories.TempBasal,
            DeviceAttributionCategories.BasalInjection,
            DeviceAttributionCategories.SensorDeviceEvent,
            DeviceAttributionCategories.PumpDeviceEvent,
        };

        uploaderOwns.Should().AllSatisfy(c => c.Should().NotContain(DeviceCategory.Uploader));
    }
}
