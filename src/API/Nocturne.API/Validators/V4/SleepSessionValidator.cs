using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates the <see cref="SleepSession"/> body of the V4 sleep session endpoints: each string at the length of the column it is stored in.
/// </summary>
/// <seealso cref="Controllers.V4.SleepController"/>
public class SleepSessionValidator : AbstractValidator<SleepSession>
{
    public SleepSessionValidator()
    {
        RuleFor(x => x.Timezone).MaximumLength(ColumnLengths.SleepSession.Timezone);
        RuleFor(x => x.SourceDevice).MaximumLength(ColumnLengths.SleepSession.SourceDevice);
        RuleFor(x => x.SourceApp).MaximumLength(ColumnLengths.SleepSession.SourceApp);
        RuleFor(x => x.OriginalId).MaximumLength(ColumnLengths.SleepSession.OriginalId);
    }
}
