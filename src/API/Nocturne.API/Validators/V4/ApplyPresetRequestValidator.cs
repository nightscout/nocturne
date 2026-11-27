using FluentValidation;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="ApplyPresetRequest"/>: the notes at the length of the started instance's notes column.
/// </summary>
/// <seealso cref="Controllers.V4.Monitoring.TrackersController"/>
public class ApplyPresetRequestValidator : AbstractValidator<ApplyPresetRequest>
{
    public ApplyPresetRequestValidator()
    {
        RuleFor(x => x.OverrideNotes).MaximumLength(ColumnLengths.TrackerInstance.StartNotes);
    }
}
