using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpdateCarbIntakeRequest"/> for the V4 carb intake update endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Timestamp must be a valid non-default <see cref="DateTimeOffset"/>.</description></item>
/// <item><description>DataSource and SyncIdentifier capped at their column lengths (<see cref="RecordColumnLengths"/>); Device and App at 500.</description></item>
/// <item><description>Carbs must be non-negative.</description></item>
/// <item><description>AbsorptionTime, when provided, must be non-negative.</description></item>
/// <item><description>DataSource is required when SyncIdentifier is supplied (composite key constraint).</description></item>
/// <item><description>CorrelationId, when supplied, must be a non-empty GUID.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="UpdateCarbIntakeRequest"/>
/// <seealso cref="Controllers.V4.Treatments.CarbIntakeController"/>
public class UpdateCarbIntakeRequestValidator : AbstractValidator<UpdateCarbIntakeRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateCarbIntakeRequestValidator"/> class
    /// and configures all validation rules for carb intake updates.
    /// </summary>
    public UpdateCarbIntakeRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).MaximumLength(500).When(x => x.Device is not null);
        RuleFor(x => x.App).MaximumLength(500).When(x => x.App is not null);
        RuleFor(x => x.DataSource).MaximumLength(RecordColumnLengths.Provenance).When(x => x.DataSource is not null);
        RuleFor(x => x.Carbs).GreaterThanOrEqualTo(0).WithMessage("Carbs must be >= 0");
        RuleFor(x => x.SyncIdentifier).MaximumLength(RecordColumnLengths.SyncIdentifier).When(x => x.SyncIdentifier is not null);
        RuleFor(x => x.AbsorptionTime).GreaterThanOrEqualTo(0).When(x => x.AbsorptionTime is not null)
            .WithMessage("AbsorptionTime must be >= 0");
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
