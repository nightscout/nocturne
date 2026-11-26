using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// What every body of the V4 profile endpoints is validated for: a timestamp, the header caps of
/// <see cref="RecordColumnRules"/>, and ProfileName and LegacyId at their columns.
/// </summary>
/// <typeparam name="T">The therapy settings or schedule model the endpoint binds.</typeparam>
/// <seealso cref="Controllers.V4.Profiles.ProfileController"/>
public abstract class ProfileRecordValidator<T> : AbstractValidator<T>
    where T : V4RecordBase, IProfileScoped
{
    protected ProfileRecordValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).WithinUploaderLabel();
        RuleFor(x => x.App).WithinUploaderLabel();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.LegacyId).MaximumLength(255);
        RuleFor(x => x.ProfileName).MaximumLength(RecordColumnLengths.ScheduleProfileName);
    }
}

/// <summary>Validates the <see cref="BasalSchedule"/> body of the V4 profile endpoints.</summary>
public class BasalScheduleValidator : ProfileRecordValidator<BasalSchedule>;

/// <summary>Validates the <see cref="CarbRatioSchedule"/> body of the V4 profile endpoints.</summary>
public class CarbRatioScheduleValidator : ProfileRecordValidator<CarbRatioSchedule>;

/// <summary>Validates the <see cref="SensitivitySchedule"/> body of the V4 profile endpoints.</summary>
public class SensitivityScheduleValidator : ProfileRecordValidator<SensitivitySchedule>;

/// <summary>Validates the <see cref="TargetRangeSchedule"/> body of the V4 profile endpoints.</summary>
public class TargetRangeScheduleValidator : ProfileRecordValidator<TargetRangeSchedule>;

/// <summary>
/// Validates the <see cref="TherapySettings"/> body of the V4 profile endpoints, and caps Timezone,
/// Units, EnteredBy and StartDate at their columns.
/// </summary>
public class TherapySettingsValidator : ProfileRecordValidator<TherapySettings>
{
    public TherapySettingsValidator()
    {
        RuleFor(x => x.Timezone).MaximumLength(64);
        RuleFor(x => x.Units).MaximumLength(10);
        RuleFor(x => x.EnteredBy).MaximumLength(100);
        RuleFor(x => x.StartDate).MaximumLength(50);
    }
}
