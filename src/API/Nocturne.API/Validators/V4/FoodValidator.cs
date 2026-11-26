using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates the <see cref="Food"/> body of the V4 food endpoints: each string at the length of the column it is stored in.
/// </summary>
public class FoodValidator : AbstractValidator<Food>
{
    public FoodValidator()
    {
        RuleFor(x => x.Type).MaximumLength(ColumnLengths.Food.Type);
        RuleFor(x => x.Category).MaximumLength(ColumnLengths.Food.Category);
        RuleFor(x => x.Subcategory).MaximumLength(ColumnLengths.Food.Subcategory);
        RuleFor(x => x.Name).MaximumLength(ColumnLengths.Food.Name);
        RuleFor(x => x.Unit).MaximumLength(ColumnLengths.Food.Unit);
    }
}
