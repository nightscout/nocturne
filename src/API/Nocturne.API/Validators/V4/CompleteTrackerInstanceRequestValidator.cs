using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="CompleteTrackerInstanceRequest"/>: each string at the length of the column it is stored in.
/// </summary>
/// <seealso cref="Controllers.V4.Monitoring.TrackersController"/>
public class CompleteTrackerInstanceRequestValidator : AbstractValidator<CompleteTrackerInstanceRequest>
{
    public CompleteTrackerInstanceRequestValidator()
    {
        RuleFor(x => x.CompletionNotes).MaximumLength(ColumnLengths.TrackerInstance.CompletionNotes);
        RuleFor(x => x.CompleteTreatmentId).MaximumLength(ColumnLengths.TrackerInstance.CompleteTreatmentId);
    }
}
