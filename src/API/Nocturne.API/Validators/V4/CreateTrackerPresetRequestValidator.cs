using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="CreateTrackerPresetRequest"/>: each string at the length of the column it is stored in.
/// </summary>
/// <seealso cref="Controllers.V4.Monitoring.TrackersController"/>
public class CreateTrackerPresetRequestValidator : AbstractValidator<CreateTrackerPresetRequest>
{
    public CreateTrackerPresetRequestValidator()
    {
        RuleFor(x => x.Name).MaximumLength(ColumnLengths.TrackerPreset.Name);
        RuleFor(x => x.DefaultStartNotes).MaximumLength(ColumnLengths.TrackerPreset.DefaultStartNotes);
    }
}
