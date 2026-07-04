using FluentValidation;
using Nocturne.API.Models.Requests.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="CreateReservoirReportRequest"/> for the V4 reservoir report endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Units must be greater than 0 and at most 1,000 (largest reservoirs hold 300 units).</description></item>
/// <item><description>Timestamp must not be in the future (beyond 5 minutes of clock skew).</description></item>
/// <item><description>Kind must be a valid <see cref="ReservoirReportKind"/> enum value.</description></item>
/// <item><description>Device and App capped at 500 characters.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="CreateReservoirReportRequest"/>
/// <seealso cref="Controllers.V4.Devices.ReservoirReportsController"/>
public class CreateReservoirReportRequestValidator : AbstractValidator<CreateReservoirReportRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CreateReservoirReportRequestValidator"/> class
    /// and configures all validation rules for reservoir reports.
    /// </summary>
    public CreateReservoirReportRequestValidator()
    {
        RuleFor(x => x.Units).GreaterThan(0).LessThanOrEqualTo(1000);
        // A future-dated report would become the newest anchor and suppress real
        // readings until overtaken; allow only small clock skew.
        RuleFor(x => x.Timestamp)
            .Must(t => t is null || t <= DateTimeOffset.UtcNow.AddMinutes(5))
            .WithMessage("Timestamp must not be in the future");
        RuleFor(x => x.Kind).IsInEnum().WithMessage("Kind must be a valid reservoir report kind");
        RuleFor(x => x.Device).MaximumLength(500).When(x => x.Device is not null);
        RuleFor(x => x.App).MaximumLength(500).When(x => x.App is not null);
    }
}
