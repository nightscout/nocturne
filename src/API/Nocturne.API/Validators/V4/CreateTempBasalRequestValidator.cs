using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="CreateTempBasalRequest"/> for the V4 temp basal write endpoints.
/// </summary>
/// <remarks>
/// A cancel uses only its timestamp, so every other rule is skipped for it. String caps are the
/// column lengths on <c>TempBasalEntity</c>.
/// </remarks>
/// <seealso cref="CreateTempBasalRequest"/>
/// <seealso cref="Controllers.V4.Treatments.TempBasalController"/>
public class CreateTempBasalRequestValidator : AbstractValidator<CreateTempBasalRequest>
{
    public CreateTempBasalRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        When(x => !x.IsCancel, () =>
        {
            RuleFor(x => x.Device).WithinProvenance();
            RuleFor(x => x.App).WithinProvenance();
            RuleFor(x => x.DataSource).WithinProvenance();
            RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
            RuleFor(x => x.PumpRecordId).MaximumLength(RecordColumnLengths.PumpRecordId).When(x => x.PumpRecordId is not null);
            RuleFor(x => x.Rate).GreaterThanOrEqualTo(0).WithMessage("Rate must be >= 0");
            RuleFor(x => x.DurationMinutes).GreaterThanOrEqualTo(0).When(x => x.DurationMinutes is not null)
                .WithMessage("DurationMinutes must be >= 0");
            RuleFor(x => x.DataSource).DataSourceRequiredWithSyncIdentifier(x => x.SyncIdentifier);
            RuleFor(x => x.CorrelationId)
                .Must(id => id != Guid.Empty)
                .When(x => x.CorrelationId.HasValue)
                .WithMessage("CorrelationId must be a non-empty GUID when supplied.");
        });
    }
}
