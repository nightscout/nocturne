using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertSensorGlucoseRequest"/> for the V4 sensor glucose (CGM) upsert endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Timestamp must be a valid non-default <see cref="DateTimeOffset"/>.</description></item>
/// <item><description>DataSource capped at its column length (<see cref="RecordColumnLengths"/>); Device and App at 500.</description></item>
/// <item><description>SyncIdentifier capped at its column length (<see cref="RecordColumnLengths"/>) and requires DataSource.</description></item>
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
        RuleFor(x => x.Device).MaximumLength(500).When(x => x.Device is not null);
        RuleFor(x => x.App).MaximumLength(500).When(x => x.App is not null);
        RuleFor(x => x.DataSource).MaximumLength(RecordColumnLengths.Provenance).When(x => x.DataSource is not null);
        RuleFor(x => x.SyncIdentifier).MaximumLength(RecordColumnLengths.SyncIdentifier).When(x => x.SyncIdentifier is not null);
        RuleFor(x => x.DataSource)
            .NotEmpty()
            .When(x => !string.IsNullOrEmpty(x.SyncIdentifier))
            .WithMessage("DataSource is required when SyncIdentifier is supplied.");
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
