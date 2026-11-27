using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates a <see cref="CreateNotificationThresholdRequest"/> of a tracker definition: the description at the
/// length of its column.
/// </summary>
/// <seealso cref="CreateTrackerDefinitionRequestValidator"/>
/// <seealso cref="UpdateTrackerDefinitionRequestValidator"/>
public class CreateNotificationThresholdRequestValidator : AbstractValidator<CreateNotificationThresholdRequest>
{
    public CreateNotificationThresholdRequestValidator()
    {
        RuleFor(x => x.Description).MaximumLength(ColumnLengths.TrackerNotificationThreshold.Description);
    }
}
