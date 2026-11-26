using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates the <see cref="BodyWeight"/> body of the V4 body weight endpoints: the header rules of
/// <see cref="RecordColumnRules"/> (caps, and a DataSource with a SyncIdentifier), Device and EnteredBy at the
/// length of the sample's <see cref="RecordColumnLengths.HealthSampleLabel"/> columns, and CreatedAt at its column.
/// </summary>
/// <seealso cref="Controllers.V4.Health.BodyWeightController"/>
public class BodyWeightValidator : AbstractValidator<BodyWeight>
{
    public BodyWeightValidator()
    {
        RuleFor(x => x.Device).MaximumLength(RecordColumnLengths.HealthSampleLabel);
        RuleFor(x => x.EnteredBy).MaximumLength(RecordColumnLengths.HealthSampleLabel);
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
        RuleFor(x => x.DataSource).DataSourceRequiredWithSyncIdentifier(x => x.SyncIdentifier);
        RuleFor(x => x.CreatedAt).MaximumLength(ColumnLengths.BodyWeight.CreatedAt);
    }
}
