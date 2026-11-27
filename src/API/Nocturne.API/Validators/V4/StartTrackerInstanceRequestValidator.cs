using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="StartTrackerInstanceRequest"/>: each string at the length of the column it is stored in.
/// </summary>
/// <seealso cref="Controllers.V4.Monitoring.TrackersController"/>
public class StartTrackerInstanceRequestValidator : AbstractValidator<StartTrackerInstanceRequest>
{
    public StartTrackerInstanceRequestValidator()
    {
        RuleFor(x => x.StartNotes).MaximumLength(ColumnLengths.TrackerInstance.StartNotes);
        RuleFor(x => x.StartTreatmentId).MaximumLength(ColumnLengths.TrackerInstance.StartTreatmentId);
    }
}
