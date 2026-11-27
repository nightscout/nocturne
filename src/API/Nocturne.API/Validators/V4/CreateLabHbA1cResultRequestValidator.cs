using FluentValidation;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="CreateLabHbA1cResultRequest"/>: the note at the length of its column.
/// </summary>
/// <seealso cref="Controllers.V4.Analytics.LabHbA1cController"/>
public class CreateLabHbA1cResultRequestValidator : AbstractValidator<CreateLabHbA1cResultRequest>
{
    public CreateLabHbA1cResultRequestValidator()
    {
        RuleFor(x => x.Note).MaximumLength(ColumnLengths.LabHbA1cResult.Note);
    }
}
