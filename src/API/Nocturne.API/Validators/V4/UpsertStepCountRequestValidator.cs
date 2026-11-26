using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertStepCountRequest"/> for the V4 step count endpoint: a timestamp, the
/// header caps of <see cref="RecordColumnRules"/>, and Device and App at the length of the sample's
/// <see cref="RecordColumnLengths.HealthSampleLabel"/> columns, which are not cut on save.
/// </summary>
/// <seealso cref="Controllers.V4.Health.StepCountController"/>
public class UpsertStepCountRequestValidator : AbstractValidator<UpsertStepCountRequest>
{
    public UpsertStepCountRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).MaximumLength(RecordColumnLengths.HealthSampleLabel);
        RuleFor(x => x.App).MaximumLength(RecordColumnLengths.HealthSampleLabel);
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
    }
}
