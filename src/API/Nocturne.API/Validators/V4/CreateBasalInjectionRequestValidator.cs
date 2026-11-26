using FluentValidation;
using Nocturne.API.Models.Requests.V4;

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
        RuleFor(x => x.Device).WithinUploaderLabel();
        RuleFor(x => x.App).WithinUploaderLabel();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
        RuleFor(x => x.DataSource).DataSourceRequiredWithSyncIdentifier(x => x.SyncIdentifier);
        RuleFor(x => x.Notes).MaximumLength(4096);
    }
}
