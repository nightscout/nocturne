using FluentValidation;
using Nocturne.API.Models.Requests.V4;

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
        RuleFor(x => x.Timestamp).NotEqual(default(DateTimeOffset)).WithMessage("Timestamp is required");
        When(x => !x.IsCancel, () =>
        {
            RuleFor(x => x.Device).MaximumLength(256).When(x => x.Device is not null);
            RuleFor(x => x.App).MaximumLength(256).When(x => x.App is not null);
            RuleFor(x => x.DataSource).MaximumLength(256).When(x => x.DataSource is not null);
            RuleFor(x => x.SyncIdentifier).MaximumLength(256).When(x => x.SyncIdentifier is not null);
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
        });
    }
}
