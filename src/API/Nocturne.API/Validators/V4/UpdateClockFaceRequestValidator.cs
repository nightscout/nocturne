using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpdateClockFaceRequest"/>: the name at the length of its column.
/// </summary>
public class UpdateClockFaceRequestValidator : AbstractValidator<UpdateClockFaceRequest>
{
    public UpdateClockFaceRequestValidator()
    {
        RuleFor(x => x.Name).MaximumLength(ColumnLengths.ClockFace.Name);
    }
}
