using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="CreateBasalInjectionRequest"/> for the V4 basal injection endpoint: a timestamp,
/// the header caps of <see cref="RecordColumnRules"/>, a DataSource when a SyncIdentifier is supplied,
/// and Notes at its column length.
/// </summary>
/// <seealso cref="Controllers.V4.Treatments.BasalInjectionController"/>
public class CreateBasalInjectionRequestValidator : AbstractValidator<CreateBasalInjectionRequest>
{
    public CreateBasalInjectionRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).WithinProvenance();
        RuleFor(x => x.App).WithinProvenance();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
        RuleFor(x => x.DataSource).DataSourceRequiredWithSyncIdentifier(x => x.SyncIdentifier);
        RuleFor(x => x.Notes).MaximumLength(ColumnLengths.BasalInjection.Notes);
    }
}
