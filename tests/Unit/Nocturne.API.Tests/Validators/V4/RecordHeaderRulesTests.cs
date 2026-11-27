using System.Runtime.CompilerServices;
using FluentAssertions;
using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Validators.Alerts;
using Nocturne.API.Validators.V4;
using Nocturne.Core.Constants;
using Xunit;

namespace Nocturne.API.Tests.Validators.V4;

/// <summary>
/// The header every V4 record request carries (the timestamp, <c>DataSource</c> and <c>SyncIdentifier</c>) is
/// refused the same way by every validator, whichever record it belongs to.
/// </summary>
[Trait("Category", "Unit")]
public class RecordHeaderRulesTests
{
    private static readonly Type[] ValidatorTypes = typeof(CreateBolusRequestValidator).Assembly.GetTypes()
        .Where(t => t is { IsAbstract: false, IsClass: true } && t.Namespace == typeof(CreateBolusRequestValidator).Namespace)
        .Where(t => typeof(IValidator).IsAssignableFrom(t))
        .OrderBy(t => t.Name)
        .ToArray();

    private static TheoryData<Type> ValidatorsOf(string property)
    {
        var data = new TheoryData<Type>();
        foreach (var type in ValidatorTypes.Where(t => RequestTypeOf(t).GetProperty(property)?.PropertyType == typeof(string)))
        {
            data.Add(type);
        }

        return data;
    }

    public static TheoryData<Type> DataSourceValidators() => ValidatorsOf("DataSource");

    public static TheoryData<Type> SyncIdentifierValidators() => ValidatorsOf("SyncIdentifier");

    public static TheoryData<Type> TimestampValidators()
    {
        var data = new TheoryData<Type>();
        foreach (var type in ValidatorTypes.Where(t => RequestTypeOf(t).GetProperty("Timestamp")?.PropertyType is { } type
                                                       && (type == typeof(DateTimeOffset) || type == typeof(DateTime))))
        {
            data.Add(type);
        }

        return data;
    }

    public static TheoryData<Type> BasalInjectionValidators() => new()
    {
        typeof(CreateBasalInjectionRequestValidator),
        typeof(UpdateBasalInjectionRequestValidator),
    };

    public static TheoryData<Type> SyncKeyedRecordValidators() => new()
    {
        typeof(UpsertBGCheckRequestValidator),
        typeof(UpsertDeviceEventRequestValidator),
        typeof(UpsertNoteRequestValidator),
        typeof(BodyWeightValidator),
    };

    public static TheoryData<Type> ProvenanceLabelValidators() => new()
    {
        typeof(CreateBasalInjectionRequestValidator),
        typeof(UpdateBasalInjectionRequestValidator),
        typeof(CreateBolusRequestValidator),
        typeof(UpdateBolusRequestValidator),
        typeof(CreateCarbIntakeRequestValidator),
        typeof(UpdateCarbIntakeRequestValidator),
        typeof(CreateMealRequestValidator),
        typeof(CreateReservoirReportRequestValidator),
        typeof(BasalScheduleValidator),
        typeof(CarbRatioScheduleValidator),
        typeof(SensitivityScheduleValidator),
        typeof(TargetRangeScheduleValidator),
        typeof(TherapySettingsValidator),
        typeof(UpsertApsSnapshotRequestValidator),
        typeof(UpsertBGCheckRequestValidator),
        typeof(UpsertBolusCalculationRequestValidator),
        typeof(UpsertCalibrationRequestValidator),
        typeof(UpsertDeviceEventRequestValidator),
        typeof(UpsertMeterGlucoseRequestValidator),
        typeof(UpsertNoteRequestValidator),
        typeof(UpsertPumpSnapshotRequestValidator),
        typeof(UpsertSensorGlucoseRequestValidator),
        typeof(UpsertUploaderSnapshotRequestValidator),
    };

    public static TheoryData<Type> HealthSampleValidators() => new()
    {
        typeof(UpsertHeartRateRequestValidator),
        typeof(UpsertStepCountRequestValidator),
    };

    [Theory]
    [MemberData(nameof(DataSourceValidators))]
    public void A_data_source_longer_than_its_column_is_refused(Type validatorType)
    {
        Errors(validatorType, ("DataSource", new string('a', RecordColumnLengths.Provenance + 1))).Should().Contain("DataSource");
        Errors(validatorType, ("DataSource", new string('a', RecordColumnLengths.Provenance))).Should().NotContain("DataSource");
    }

    [Theory]
    [MemberData(nameof(SyncIdentifierValidators))]
    public void A_sync_identifier_longer_than_its_column_is_refused(Type validatorType)
    {
        var withDataSource = ("DataSource", "loop");

        Errors(validatorType, withDataSource, ("SyncIdentifier", new string('a', RecordColumnLengths.SyncIdentifier + 1))).Should().Contain("SyncIdentifier");
        Errors(validatorType, withDataSource, ("SyncIdentifier", new string('a', RecordColumnLengths.SyncIdentifier))).Should().NotContain("SyncIdentifier");
    }

    [Theory]
    [MemberData(nameof(TimestampValidators))]
    public void An_unset_timestamp_is_refused_with_the_one_message(Type validatorType)
    {
        var timestamp = RequestTypeOf(validatorType).GetProperty("Timestamp")!.PropertyType == typeof(DateTime)
            ? (object)default(DateTime)
            : default(DateTimeOffset);
        var failures = Validate(validatorType, ("Timestamp", timestamp));

        failures.Should().Contain(f => f.PropertyName == "Timestamp" && f.ErrorMessage == TimestampRule.Message);
    }

    [Theory]
    [MemberData(nameof(BasalInjectionValidators))]
    public void A_basal_injection_sync_identifier_needs_a_data_source_in_the_words_every_record_uses(Type validatorType)
    {
        var failures = Validate(validatorType, ("DataSource", null), ("SyncIdentifier", "abc"));

        failures.Should().Contain(f => f.PropertyName == "DataSource" && f.ErrorMessage == RecordColumnRules.DataSourceRequiredMessage);
        Errors(validatorType, ("DataSource", "loop"), ("SyncIdentifier", "abc")).Should().NotContain("DataSource");
    }

    [Theory]
    [MemberData(nameof(SyncKeyedRecordValidators))]
    public void A_sync_key_needs_a_data_source_in_the_words_every_record_uses(Type validatorType)
    {
        var failures = Validate(validatorType, ("DataSource", null), ("SyncIdentifier", "abc"));

        failures.Should().Contain(f => f.PropertyName == "DataSource" && f.ErrorMessage == RecordColumnRules.DataSourceRequiredMessage);
        Errors(validatorType, ("DataSource", "loop"), ("SyncIdentifier", "abc")).Should().NotContain("DataSource");
        Errors(validatorType, ("DataSource", null), ("SyncIdentifier", null)).Should().NotContain("DataSource");
    }

    [Theory]
    [MemberData(nameof(BasalInjectionValidators))]
    public void Basal_injection_notes_are_capped_at_their_column(Type validatorType)
    {
        Errors(validatorType, ("Notes", new string('a', 4097))).Should().Contain("Notes");
        Errors(validatorType, ("Notes", new string('a', 4096))).Should().NotContain("Notes");
    }

    [Theory]
    [MemberData(nameof(ProvenanceLabelValidators))]
    public void A_device_or_app_longer_than_its_column_is_refused(Type validatorType)
    {
        foreach (var property in new[] { "Device", "App" })
        {
            Errors(validatorType, (property, new string('a', RecordColumnLengths.Provenance + 1))).Should().Contain(property);
            Errors(validatorType, (property, new string('a', RecordColumnLengths.Provenance))).Should().NotContain(property);
        }
    }

    [Theory]
    [MemberData(nameof(HealthSampleValidators))]
    public void A_health_sample_device_or_app_longer_than_its_column_is_refused(Type validatorType)
    {
        foreach (var property in new[] { "Device", "App" })
        {
            Errors(validatorType, (property, new string('a', RecordColumnLengths.HealthSampleLabel + 1))).Should().Contain(property);
            Errors(validatorType, (property, new string('a', RecordColumnLengths.HealthSampleLabel))).Should().NotContain(property);
        }
    }

    public static TheoryData<Type, string, int> TextColumns() => new()
    {
        { typeof(BodyWeightValidator), "Device", RecordColumnLengths.HealthSampleLabel },
        { typeof(BodyWeightValidator), "EnteredBy", RecordColumnLengths.HealthSampleLabel },
        { typeof(BodyWeightValidator), "CreatedAt", 50 },
        { typeof(SleepSessionValidator), "Timezone", 100 },
        { typeof(SleepSessionValidator), "SourceDevice", 200 },
        { typeof(SleepSessionValidator), "SourceApp", 200 },
        { typeof(SleepSessionValidator), "OriginalId", 255 },
        { typeof(CreateStateSpanRequestValidator), "State", 100 },
        { typeof(CreateStateSpanRequestValidator), "Source", 50 },
        { typeof(CreateStateSpanRequestValidator), "OriginalId", 255 },
        { typeof(UpdateStateSpanRequestValidator), "State", 100 },
        { typeof(UpdateStateSpanRequestValidator), "Source", 50 },
        { typeof(TherapySettingsValidator), "ProfileName", RecordColumnLengths.ScheduleProfileName },
        { typeof(TherapySettingsValidator), "LegacyId", 255 },
        { typeof(TherapySettingsValidator), "Timezone", 64 },
        { typeof(TherapySettingsValidator), "Units", 10 },
        { typeof(CreateBolusRequestValidator), "InsulinType", ColumnLengths.Bolus.InsulinType },
        { typeof(UpdateBolusRequestValidator), "InsulinType", ColumnLengths.Bolus.InsulinType },
        { typeof(CreateMealRequestValidator), "InsulinType", ColumnLengths.Bolus.InsulinType },
        { typeof(UpsertBolusCalculationRequestValidator), "BloodGlucoseInputSource", ColumnLengths.BolusCalculation.BloodGlucoseInputSource },
        { typeof(UpsertNoteRequestValidator), "EventType", ColumnLengths.Note.EventType },
        { typeof(UpsertApsSnapshotRequestValidator), "AidVersion", ColumnLengths.ApsSnapshot.AidVersion },
        { typeof(UpsertUploaderSnapshotRequestValidator), "Name", ColumnLengths.UploaderSnapshot.Name },
        { typeof(UpsertUploaderSnapshotRequestValidator), "Type", ColumnLengths.UploaderSnapshot.Type },
        { typeof(TherapySettingsValidator), "EnteredBy", 100 },
        { typeof(TherapySettingsValidator), "StartDate", 50 },
        { typeof(BasalScheduleValidator), "ProfileName", RecordColumnLengths.ScheduleProfileName },
        { typeof(BasalScheduleValidator), "LegacyId", 255 },
        { typeof(CarbRatioScheduleValidator), "ProfileName", RecordColumnLengths.ScheduleProfileName },
        { typeof(SensitivityScheduleValidator), "ProfileName", RecordColumnLengths.ScheduleProfileName },
        { typeof(TargetRangeScheduleValidator), "ProfileName", RecordColumnLengths.ScheduleProfileName },
        { typeof(CreateLabHbA1cResultRequestValidator), "Note", 500 },
        { typeof(StartTrackerInstanceRequestValidator), "StartNotes", 1000 },
        { typeof(StartTrackerInstanceRequestValidator), "StartTreatmentId", 255 },
        { typeof(CompleteTrackerInstanceRequestValidator), "CompletionNotes", 1000 },
        { typeof(CompleteTrackerInstanceRequestValidator), "CompleteTreatmentId", 255 },
        { typeof(ApplyPresetRequestValidator), "OverrideNotes", 1000 },
        { typeof(CreateNotificationThresholdRequestValidator), "Description", 500 },
        { typeof(ConnectorFoodEntryImportValidator), "ConnectorSource", 50 },
        { typeof(ConnectorFoodEntryImportValidator), "ExternalEntryId", 255 },
        { typeof(ConnectorFoodEntryImportValidator), "ExternalFoodId", 255 },
        { typeof(ConnectorFoodEntryImportValidator), "MealName", 50 },
        { typeof(ConnectorFoodEntryImportValidator), "ServingDescription", 100 },
        { typeof(ConnectorFoodImportValidator), "ExternalId", 255 },
        { typeof(ConnectorFoodImportValidator), "Name", 500 },
        { typeof(ConnectorFoodImportValidator), "BrandName", 200 },
        { typeof(UpsertTimezoneEntryRequestValidator), "Timezone", 64 },
        { typeof(UpdateCoachMarkRequestValidator), "Status", 50 },
        { typeof(AcknowledgeRequestValidator), "AcknowledgedBy", 256 },
        { typeof(MarkDeliveredRequestValidator), "PlatformMessageId", 256 },
        { typeof(MarkDeliveredRequestValidator), "PlatformThreadId", 256 },
        { typeof(MarkFailedRequestValidator), "PlatformMessageId", 256 },
        { typeof(DiscrepancyAnalysisDtoValidator), "TraceId", 128 },
        { typeof(DiscrepancyAnalysisDtoValidator), "RequestMethod", 10 },
        { typeof(DiscrepancyAnalysisDtoValidator), "RequestPath", 2048 },
        { typeof(DiscrepancyAnalysisDtoValidator), "Summary", 1000 },
        { typeof(DiscrepancyAnalysisDtoValidator), "SelectedResponseTarget", 50 },
        { typeof(DiscrepancyAnalysisDtoValidator), "SelectionReason", 500 },
        { typeof(DiscrepancyAnalysisDtoValidator), "ErrorMessage", 2000 },
        { typeof(DiscrepancyDetailDtoValidator), "Field", 500 },
        { typeof(DiscrepancyDetailDtoValidator), "NightscoutValue", 2000 },
        { typeof(DiscrepancyDetailDtoValidator), "NocturneValue", 2000 },
        { typeof(DiscrepancyDetailDtoValidator), "Description", 1000 },
    };

    [Theory]
    [MemberData(nameof(TextColumns))]
    public void A_text_longer_than_its_column_is_refused(Type validatorType, string property, int length)
    {
        Errors(validatorType, (property, new string('a', length + 1))).Should().Contain(property);
        Errors(validatorType, (property, new string('a', length))).Should().NotContain(property);
    }

    [Theory]
    [InlineData("Type", 100)]
    [InlineData("EnteredBy", 50)]
    public void An_activity_text_longer_than_its_state_span_column_is_refused(string property, int length)
    {
        Errors(typeof(UpsertActivityRequestValidator), (property, new string('a', length + 1))).Should().Contain(property);
        Errors(typeof(UpsertActivityRequestValidator), (property, new string('a', length))).Should().NotContain(property);
    }

    private static Type RequestTypeOf(Type validatorType) => validatorType.BaseType!.GetGenericArguments()[0];

    private static List<string> Errors(Type validatorType, params (string Property, object? Value)[] values) =>
        Validate(validatorType, values).Select(f => f.PropertyName).ToList();

    private static IList<FluentValidation.Results.ValidationFailure> Validate(Type validatorType, params (string Property, object? Value)[] values)
    {
        var requestType = RequestTypeOf(validatorType);
        var request = requestType.GetConstructor(Type.EmptyTypes) is not null
            ? Activator.CreateInstance(requestType)!
            : RuntimeHelpers.GetUninitializedObject(requestType);
        if (requestType.GetProperty("Timestamp") is { } timestamp)
        {
            timestamp.SetValue(request, timestamp.PropertyType == typeof(DateTime) ? DateTime.UtcNow : (object)DateTimeOffset.UtcNow);
        }

        foreach (var (property, value) in values)
        {
            requestType.GetProperty(property)!.SetValue(request, value);
        }

        var context = (IValidationContext)Activator.CreateInstance(typeof(ValidationContext<>).MakeGenericType(requestType), request)!;
        return ((IValidator)Activator.CreateInstance(validatorType)!).Validate(context).Errors;
    }
}
