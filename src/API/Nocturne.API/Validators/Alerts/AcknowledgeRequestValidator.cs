using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.Alerts;

/// <summary>
/// Validates <see cref="AcknowledgeRequest"/>: the acknowledger at the length of the excursion column it is stored
/// in when the caller carries no name of its own.
/// </summary>
/// <seealso cref="AlertsController"/>
public class AcknowledgeRequestValidator : AbstractValidator<AcknowledgeRequest>
{
    public AcknowledgeRequestValidator()
    {
        RuleFor(x => x.AcknowledgedBy).MaximumLength(ColumnLengths.AlertExcursion.AcknowledgedBy);
    }
}
