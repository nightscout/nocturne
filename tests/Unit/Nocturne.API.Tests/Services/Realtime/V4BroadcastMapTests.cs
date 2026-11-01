using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Devices;
using Nocturne.API.Controllers.V4.Glucose;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Models.Authorization;
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
    [InlineData(typeof(BGCheck), RealtimeCategories.Glucose, "bgCheck")]
    [InlineData(typeof(BolusCalculation), RealtimeCategories.Care, "bolusCalculation")]
    [InlineData(typeof(BasalInjection), RealtimeCategories.Care, "basalInjection")]
    [InlineData(typeof(TempBasal), RealtimeCategories.Care, "tempBasal")]
    [InlineData(typeof(Note), RealtimeCategories.Care, "note")]
    [InlineData(typeof(DeviceEvent), RealtimeCategories.Device, "deviceEvent")]
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

    /// <summary>The V4 controller serving each mapped type's HTTP read.</summary>
    public static TheoryData<Type, Type> HttpReads => new()
    {
        { typeof(SensorGlucose), typeof(SensorGlucoseController) },
        { typeof(MeterGlucose), typeof(MeterGlucoseController) },
        { typeof(Calibration), typeof(CalibrationController) },
        { typeof(Bolus), typeof(BolusController) },
        { typeof(CarbIntake), typeof(NutritionController) },
        { typeof(BGCheck), typeof(BGCheckController) },
        { typeof(BolusCalculation), typeof(BolusCalculationController) },
        { typeof(BasalInjection), typeof(BasalInjectionController) },
        { typeof(TempBasal), typeof(TempBasalController) },
        { typeof(Note), typeof(NoteController) },
        { typeof(DeviceEvent), typeof(DeviceEventController) },
        { typeof(ApsSnapshot), typeof(ApsSnapshotController) },
        { typeof(PumpSnapshot), typeof(PumpSnapshotController) },
        { typeof(UploaderSnapshot), typeof(UploaderSnapshotController) },
    };

    [Theory]
    [MemberData(nameof(HttpReads))]
    public void A_category_subscriber_may_read_each_record_it_carries_over_HTTP(Type modelType, Type controller)
    {
        V4BroadcastMap.TryGet(modelType, out var category, out _).Should().BeTrue();
        var subscriber = Scope.Normalize([RealtimeCategories.GoverningScopes[category], Scope.GlucoseRead]);

        HttpReadScopes(controller).Should().Contain(scope => Scope.Satisfies(subscriber, scope),
            $"{modelType.Name} rides {category}, so every {category} subscriber receives it");
    }

    /// <summary>The scopes the controller's list read accepts: its own, or the controller's.</summary>
    private static IReadOnlyList<string> HttpReadScopes(Type controller) =>
        controller.GetCustomAttribute<RequireScopeAttribute>()?.Scopes
        ?? controller.GetMethods()
            .Where(m => m.GetCustomAttribute<HttpGetAttribute>() is { Template: null })
            .Select(m => m.GetCustomAttribute<RequireScopeAttribute>())
            .OfType<RequireScopeAttribute>()
            .First()
            .Scopes;

    [Fact]
    public void A_treatments_reader_without_device_read_receives_no_device_event()
    {
        V4BroadcastMap.TryGet(typeof(DeviceEvent), out var category, out _);
        var treatmentsReader = new HubAuthorization(
            Guid.NewGuid(),
            Scope.Normalize([Scope.GlucoseRead, Scope.TreatmentsRead]),
            HubCredentialKind.Subject,
            Guid.NewGuid(),
            HistoryClamped: false);

        treatmentsReader.MayReceiveCategory(category).Should().BeFalse();
    }

    [Fact]
    public void A_glucose_reader_receives_bg_checks()
    {
        V4BroadcastMap.TryGet(typeof(BGCheck), out var category, out _);
        var glucoseReader = new HubAuthorization(
            Guid.NewGuid(),
            Scope.Normalize([Scope.GlucoseRead]),
            HubCredentialKind.Subject,
            Guid.NewGuid(),
            HistoryClamped: false);

        glucoseReader.MayReceiveCategory(category).Should().BeTrue();
    }
}
