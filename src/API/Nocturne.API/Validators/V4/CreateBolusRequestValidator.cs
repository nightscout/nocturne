using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="CreateBolusRequest"/> for the V4 bolus creation endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Timestamp must be a valid non-default <see cref="DateTimeOffset"/>.</description></item>
/// <item><description>DataSource and SyncIdentifier capped at their column lengths (<see cref="RecordColumnLengths"/>); Device and App at 500; InsulinType at 200.</description></item>
/// <item><description>Insulin must be non-negative.</description></item>
/// <item><description>Duration, when provided, must be non-negative.</description></item>
/// <item><description>BolusType and Kind must be valid enum values.</description></item>
/// <item><description>DataSource is required when SyncIdentifier is supplied (composite key constraint).</description></item>
/// <item><description>CorrelationId, when supplied, must be a non-empty GUID.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="CreateBolusRequest"/>
/// <seealso cref="Controllers.V4.Treatments.BolusController"/>
public class CreateBolusRequestValidator : AbstractValidator<CreateBolusRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CreateBolusRequestValidator"/> class
    /// and configures all validation rules for bolus creation.
    /// </summary>
    public CreateBolusRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).MaximumLength(500).When(x => x.Device is not null);
        RuleFor(x => x.App).MaximumLength(500).When(x => x.App is not null);
        RuleFor(x => x.DataSource).MaximumLength(RecordColumnLengths.Provenance).When(x => x.DataSource is not null);
        RuleFor(x => x.Insulin).GreaterThanOrEqualTo(0).WithMessage("Insulin must be >= 0");
        RuleFor(x => x.Duration).GreaterThanOrEqualTo(0).When(x => x.Duration is not null)
            .WithMessage("Duration must be >= 0");
        RuleFor(x => x.BolusType).IsInEnum().When(x => x.BolusType is not null);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.SyncIdentifier).MaximumLength(RecordColumnLengths.SyncIdentifier).When(x => x.SyncIdentifier is not null);
        RuleFor(x => x.InsulinType).MaximumLength(200).When(x => x.InsulinType is not null);
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
