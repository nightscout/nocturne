using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.Alerts;

/// <summary>
/// Validates <see cref="MarkDeliveredRequest"/>: the platform's message and thread ids at the length of the delivery
/// columns they are stored in.
/// </summary>
/// <seealso cref="AlertsController"/>
public class MarkDeliveredRequestValidator : AbstractValidator<MarkDeliveredRequest>
{
    public MarkDeliveredRequestValidator()
    {
        RuleFor(x => x.PlatformMessageId).MaximumLength(ColumnLengths.AlertDelivery.PlatformMessageId);
        RuleFor(x => x.PlatformThreadId).MaximumLength(ColumnLengths.AlertDelivery.PlatformThreadId);
    }
}
