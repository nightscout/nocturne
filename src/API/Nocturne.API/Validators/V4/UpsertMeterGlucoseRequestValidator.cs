using FluentValidation;
using Nocturne.API.Models.Requests.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertMeterGlucoseRequest"/> for the V4 meter glucose upsert endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Timestamp must be a valid non-default <see cref="DateTimeOffset"/>.</description></item>
/// <item><description>The header caps of <see cref="RecordColumnRules"/>.</description></item>
/// <item><description>Mgdl must be between 0 and 10,000 (mg/dL range guard).</description></item>
/// </list>
/// </remarks>
/// <seealso cref="UpsertMeterGlucoseRequest"/>
/// <seealso cref="Controllers.V4.Glucose.MeterGlucoseController"/>
public class UpsertMeterGlucoseRequestValidator : AbstractValidator<UpsertMeterGlucoseRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpsertMeterGlucoseRequestValidator"/> class
    /// and configures all validation rules for meter glucose upserts.
    /// </summary>
    public UpsertMeterGlucoseRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).WithinUploaderLabel();
        RuleFor(x => x.App).WithinUploaderLabel();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.Mgdl).InclusiveBetween(0, 10000).WithMessage("Mgdl must be between 0 and 10000");
    }
}
