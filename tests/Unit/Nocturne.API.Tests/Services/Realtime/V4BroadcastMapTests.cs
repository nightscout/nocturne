using Nocturne.API.Services.Realtime;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Unit.Services.Realtime;

/// <summary>
/// Unit tests for <see cref="V4BroadcastMap"/> — the static map from a V4 model type to its broadcast
/// category and <c>recordType</c> discriminator. Pins the mapped types and confirms the dormant
/// <c>therapy</c> family (schedules / therapy settings) is intentionally unmapped.
/// </summary>
public class V4BroadcastMapTests
{
    [Theory]
    // Every mapped type is pinned: the recordType strings are the wire contract the SDK consumers
    // (companion + Prelude) depend on, so a typo here must fail the build, not ship silently.
    [InlineData(typeof(SensorGlucose), RealtimeCategories.Glucose, "sensorGlucose")]
    [InlineData(typeof(MeterGlucose), RealtimeCategories.Glucose, "meterGlucose")]
    [InlineData(typeof(Calibration), RealtimeCategories.Glucose, "calibration")]
    [InlineData(typeof(Bolus), RealtimeCategories.Care, "bolus")]
    [InlineData(typeof(CarbIntake), RealtimeCategories.Care, "carbIntake")]
    [InlineData(typeof(BGCheck), RealtimeCategories.Care, "bgCheck")]
    [InlineData(typeof(BolusCalculation), RealtimeCategories.Care, "bolusCalculation")]
    [InlineData(typeof(BasalInjection), RealtimeCategories.Care, "basalInjection")]
    [InlineData(typeof(TempBasal), RealtimeCategories.Care, "tempBasal")]
    [InlineData(typeof(Note), RealtimeCategories.Care, "note")]
    [InlineData(typeof(DeviceEvent), RealtimeCategories.Care, "deviceEvent")]
    [InlineData(typeof(ApsSnapshot), RealtimeCategories.Device, "apsSnapshot")]
    [InlineData(typeof(PumpSnapshot), RealtimeCategories.Device, "pumpSnapshot")]
    [InlineData(typeof(UploaderSnapshot), RealtimeCategories.Device, "uploaderSnapshot")]
    public void TryGet_MappedType_ReturnsCategoryAndRecordType(
        Type modelType, string expectedCategory, string expectedRecordType)
    {
        var found = V4BroadcastMap.TryGet(modelType, out var category, out var recordType);

        found.Should().BeTrue();
        category.Should().Be(expectedCategory);
        recordType.Should().Be(expectedRecordType);
    }

    [Theory]
    [InlineData(typeof(BasalSchedule))]
    [InlineData(typeof(TherapySettings))]
    public void TryGet_UnmappedTherapyType_ReturnsFalse(Type modelType)
    {
        var found = V4BroadcastMap.TryGet(modelType, out var category, out var recordType);

        found.Should().BeFalse();
        category.Should().BeEmpty();
        recordType.Should().BeEmpty();
    }
}
