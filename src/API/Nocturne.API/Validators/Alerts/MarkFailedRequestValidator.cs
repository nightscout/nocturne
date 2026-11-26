using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.Alerts;

/// <summary>
/// Validates <see cref="MarkFailedRequest"/>: the platform's message id at the length of the delivery column it is
/// stored in.
/// </summary>
/// <seealso cref="AlertsController"/>
public class MarkFailedRequestValidator : AbstractValidator<MarkFailedRequest>
{
    public MarkFailedRequestValidator()
    {
        RuleFor(x => x.PlatformMessageId).MaximumLength(ColumnLengths.AlertDelivery.PlatformMessageId);
    }
}
