using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="CreateClockFaceRequest"/>: the name at the length of its column.
/// </summary>
public class CreateClockFaceRequestValidator : AbstractValidator<CreateClockFaceRequest>
{
    public CreateClockFaceRequestValidator()
    {
        RuleFor(x => x.Name).MaximumLength(ColumnLengths.ClockFace.Name);
    }
}
