using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpdateTrackerDefinitionRequest"/>: each string at the length of the column it is stored in,
/// and each notification threshold through <see cref="CreateNotificationThresholdRequestValidator"/>.
/// </summary>
/// <seealso cref="Controllers.V4.Monitoring.TrackersController"/>
public class UpdateTrackerDefinitionRequestValidator : AbstractValidator<UpdateTrackerDefinitionRequest>
{
    public UpdateTrackerDefinitionRequestValidator()
    {
        RuleFor(x => x.Name).MaximumLength(ColumnLengths.TrackerDefinition.Name);
        RuleFor(x => x.Description).MaximumLength(ColumnLengths.TrackerDefinition.Description);
        RuleFor(x => x.Icon).MaximumLength(ColumnLengths.TrackerDefinition.Icon);
        RuleFor(x => x.TriggerNotesContains).MaximumLength(ColumnLengths.TrackerDefinition.TriggerNotesContains);
        RuleFor(x => x.StartEventType).MaximumLength(ColumnLengths.TrackerDefinition.StartEventType);
        RuleFor(x => x.CompletionEventType).MaximumLength(ColumnLengths.TrackerDefinition.CompletionEventType);
        RuleForEach(x => x.NotificationThresholds).SetValidator(new CreateNotificationThresholdRequestValidator());
    }
}
