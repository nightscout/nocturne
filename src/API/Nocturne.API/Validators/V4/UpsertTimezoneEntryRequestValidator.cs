using FluentValidation;
using Nocturne.API.Controllers.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertTimezoneEntryRequest"/>: the timezone at the length of its column.
/// </summary>
/// <seealso cref="TimezoneTimelineController"/>
public class UpsertTimezoneEntryRequestValidator : AbstractValidator<UpsertTimezoneEntryRequest>
{
    public UpsertTimezoneEntryRequestValidator()
    {
        RuleFor(x => x.Timezone).MaximumLength(ColumnLengths.TimezoneTimeline.Timezone);
    }
}
