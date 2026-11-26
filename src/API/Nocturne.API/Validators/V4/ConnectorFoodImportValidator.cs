using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates the <see cref="ConnectorFoodImport"/> of an imported food entry: each string at the length of the food
/// column it is stored in. The brand is stored as the food's category.
/// </summary>
/// <seealso cref="ConnectorFoodEntryImportValidator"/>
public class ConnectorFoodImportValidator : AbstractValidator<ConnectorFoodImport>
{
    public ConnectorFoodImportValidator()
    {
        RuleFor(x => x.ExternalId).MaximumLength(ColumnLengths.Food.ExternalId);
        RuleFor(x => x.Name).MaximumLength(ColumnLengths.Food.Name);
        RuleFor(x => x.BrandName).MaximumLength(ColumnLengths.Food.Category);
    }
}
