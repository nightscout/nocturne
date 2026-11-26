using FluentValidation;
using Nocturne.API.Controllers.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpdateCoachMarkRequest"/>: the status at the length of its column.
/// </summary>
/// <seealso cref="CoachMarkController"/>
public class UpdateCoachMarkRequestValidator : AbstractValidator<UpdateCoachMarkRequest>
{
    public UpdateCoachMarkRequestValidator()
    {
        RuleFor(x => x.Status).MaximumLength(ColumnLengths.CoachMarkState.Status);
    }
}
