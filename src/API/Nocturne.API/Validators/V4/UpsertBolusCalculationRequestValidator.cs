using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertBolusCalculationRequest"/> for the V4 bolus calculation upsert endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Timestamp must be a valid non-default <see cref="DateTimeOffset"/>.</description></item>
/// <item><description>DataSource capped at its column length (<see cref="RecordColumnLengths"/>); Device and App at 500; BloodGlucoseInputSource at 200.</description></item>
/// <item><description>CarbInput and InsulinRecommendation, when provided, must be non-negative.</description></item>
/// <item><description>CarbRatio, when provided, must be strictly positive (division denominator).</description></item>
/// <item><description>CalculationType, when provided, must be a valid enum value.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="UpsertBolusCalculationRequest"/>
/// <seealso cref="Controllers.V4.Treatments.BolusCalculationController"/>
public class UpsertBolusCalculationRequestValidator : AbstractValidator<UpsertBolusCalculationRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpsertBolusCalculationRequestValidator"/> class
    /// and configures all validation rules for bolus calculation upserts.
    /// </summary>
    public UpsertBolusCalculationRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).MaximumLength(500).When(x => x.Device is not null);
        RuleFor(x => x.App).MaximumLength(500).When(x => x.App is not null);
        RuleFor(x => x.DataSource).MaximumLength(RecordColumnLengths.Provenance).When(x => x.DataSource is not null);
        RuleFor(x => x.CarbInput).GreaterThanOrEqualTo(0).When(x => x.CarbInput is not null)
            .WithMessage("CarbInput must be >= 0");
        RuleFor(x => x.CarbRatio).GreaterThan(0).When(x => x.CarbRatio is not null)
            .WithMessage("CarbRatio must be > 0");
        RuleFor(x => x.InsulinRecommendation).GreaterThanOrEqualTo(0).When(x => x.InsulinRecommendation is not null)
            .WithMessage("InsulinRecommendation must be >= 0");
        RuleFor(x => x.CalculationType).IsInEnum().When(x => x.CalculationType is not null);
        RuleFor(x => x.BloodGlucoseInputSource).MaximumLength(200).When(x => x.BloodGlucoseInputSource is not null);
    }
}
