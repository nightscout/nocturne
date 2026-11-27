using FluentValidation;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="CarbIntakeFoodRequest"/>: the note at the length of its column.
/// </summary>
/// <seealso cref="Controllers.V4.Treatments.NutritionController"/>
public class CarbIntakeFoodRequestValidator : AbstractValidator<CarbIntakeFoodRequest>
{
    public CarbIntakeFoodRequestValidator()
    {
        RuleFor(x => x.Note).MaximumLength(ColumnLengths.TreatmentFood.Note);
    }
}
