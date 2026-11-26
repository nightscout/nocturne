using FluentValidation;
using Nocturne.API.Models.Requests.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertCalibrationRequest"/> for the V4 sensor calibration upsert endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Timestamp must be a valid non-default <see cref="DateTimeOffset"/>.</description></item>
/// <item><description>The header caps of <see cref="RecordColumnRules"/>.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="UpsertCalibrationRequest"/>
/// <seealso cref="Controllers.V4.Glucose.CalibrationController"/>
public class UpsertCalibrationRequestValidator : AbstractValidator<UpsertCalibrationRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpsertCalibrationRequestValidator"/> class
    /// and configures all validation rules for calibration upserts.
    /// </summary>
    public UpsertCalibrationRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).WithinUploaderLabel();
        RuleFor(x => x.App).WithinUploaderLabel();
        RuleFor(x => x.DataSource).WithinProvenance();
    }
}
