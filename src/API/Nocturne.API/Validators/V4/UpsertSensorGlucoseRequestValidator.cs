using FluentValidation;
using Nocturne.API.Models.Requests.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertSensorGlucoseRequest"/> for the V4 sensor glucose (CGM) upsert endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Timestamp must be a valid non-default <see cref="DateTimeOffset"/>.</description></item>
/// <item><description>The header rules of <see cref="RecordColumnRules"/>: caps on DataSource, SyncIdentifier, Device and App, and DataSource required when SyncIdentifier is supplied.</description></item>
/// <item><description>Mgdl must be between 0 and 10,000 (mg/dL range guard).</description></item>
/// <item><description>Direction, when provided, must be a valid <see cref="Core.Models.V4.GlucoseDirection"/> enum value.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="UpsertSensorGlucoseRequest"/>
/// <seealso cref="Controllers.V4.Glucose.SensorGlucoseController"/>
public class UpsertSensorGlucoseRequestValidator : AbstractValidator<UpsertSensorGlucoseRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpsertSensorGlucoseRequestValidator"/> class
    /// and configures all validation rules for sensor glucose upserts.
    /// </summary>
    public UpsertSensorGlucoseRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).WithinUploaderLabel();
        RuleFor(x => x.App).WithinUploaderLabel();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
        RuleFor(x => x.DataSource).DataSourceRequiredWithSyncIdentifier(x => x.SyncIdentifier);
        RuleFor(x => x.Mgdl).InclusiveBetween(0, 10000).WithMessage("Mgdl must be between 0 and 10000");
        RuleFor(x => x.Direction).IsInEnum().When(x => x.Direction is not null);
        RuleFor(x => x.GlucoseProcessing)
            .Must(v => Enum.TryParse<Core.Models.V4.GlucoseProcessing>(v, ignoreCase: true, out _))
            .When(x => x.GlucoseProcessing is not null)
            .WithMessage("GlucoseProcessing must be 'Smoothed' or 'Unsmoothed'");
        RuleFor(x => x.SmoothedMgdl)
            .InclusiveBetween(0, 10000)
            .When(x => x.SmoothedMgdl is not null);
        RuleFor(x => x.UnsmoothedMgdl)
            .InclusiveBetween(0, 10000)
            .When(x => x.UnsmoothedMgdl is not null);
    }
}
