using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpdateTempBasalRequest"/> for the V4 temp basal update endpoint.
/// </summary>
/// <seealso cref="UpdateTempBasalRequest"/>
/// <seealso cref="Controllers.V4.Treatments.TempBasalController"/>
public class UpdateTempBasalRequestValidator : AbstractValidator<UpdateTempBasalRequest>
{
    public UpdateTempBasalRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).MaximumLength(RecordColumnLengths.Provenance).When(x => x.Device is not null);
        RuleFor(x => x.App).MaximumLength(RecordColumnLengths.Provenance).When(x => x.App is not null);
        RuleFor(x => x.DataSource).MaximumLength(RecordColumnLengths.Provenance).When(x => x.DataSource is not null);
        RuleFor(x => x.SyncIdentifier).MaximumLength(RecordColumnLengths.SyncIdentifier).When(x => x.SyncIdentifier is not null);
        RuleFor(x => x.PumpRecordId).MaximumLength(RecordColumnLengths.PumpRecordId).When(x => x.PumpRecordId is not null);
        RuleFor(x => x.Rate).GreaterThanOrEqualTo(0).WithMessage("Rate must be >= 0");
        RuleFor(x => x.DurationMinutes).GreaterThanOrEqualTo(0).When(x => x.DurationMinutes is not null)
            .WithMessage("DurationMinutes must be >= 0");
        RuleFor(x => x.DataSource)
            .NotEmpty()
            .When(x => !string.IsNullOrEmpty(x.SyncIdentifier))
            .WithMessage("DataSource is required when SyncIdentifier is supplied.");
        RuleFor(x => x.CorrelationId)
            .Must(id => id != Guid.Empty)
            .When(x => x.CorrelationId.HasValue)
            .WithMessage("CorrelationId must be a non-empty GUID when supplied.");
    }
}
