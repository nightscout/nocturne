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
/// <item><description>The header rules of <see cref="RecordColumnRules"/>: caps on DataSource, SyncIdentifier, Device and App, and DataSource required when SyncIdentifier is supplied; InsulinType at <see cref="ColumnLengths.Bolus.InsulinType"/>.</description></item>
/// <item><description>Insulin must be non-negative.</description></item>
/// <item><description>Duration, when provided, must be non-negative.</description></item>
/// <item><description>BolusType and Kind must be valid enum values.</description></item>
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
        RuleFor(x => x.Device).WithinProvenance();
        RuleFor(x => x.App).WithinProvenance();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.Insulin).GreaterThanOrEqualTo(0).WithMessage("Insulin must be >= 0");
        RuleFor(x => x.Duration).GreaterThanOrEqualTo(0).When(x => x.Duration is not null)
            .WithMessage("Duration must be >= 0");
        RuleFor(x => x.BolusType).IsInEnum().When(x => x.BolusType is not null);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
        RuleFor(x => x.InsulinType).MaximumLength(ColumnLengths.Bolus.InsulinType).When(x => x.InsulinType is not null);
        RuleFor(x => x.DataSource).DataSourceRequiredWithSyncIdentifier(x => x.SyncIdentifier);
        RuleFor(x => x.CorrelationId)
            .Must(id => id != Guid.Empty)
            .When(x => x.CorrelationId.HasValue)
            .WithMessage("CorrelationId must be a non-empty GUID when supplied.");
    }
}
