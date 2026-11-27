using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates a <see cref="ConnectorFoodEntryImport"/>: each string at the length of the column it is stored in,
/// and its food through <see cref="ConnectorFoodImportValidator"/>.
/// </summary>
/// <seealso cref="Controllers.V4.Treatments.ConnectorFoodEntriesController"/>
public class ConnectorFoodEntryImportValidator : AbstractValidator<ConnectorFoodEntryImport>
{
    public ConnectorFoodEntryImportValidator()
    {
        RuleFor(x => x.ConnectorSource).MaximumLength(ColumnLengths.ConnectorFoodEntry.ConnectorSource);
        RuleFor(x => x.ExternalEntryId).MaximumLength(ColumnLengths.ConnectorFoodEntry.ExternalEntryId);
        RuleFor(x => x.ExternalFoodId).MaximumLength(ColumnLengths.ConnectorFoodEntry.ExternalFoodId);
        RuleFor(x => x.MealName).MaximumLength(ColumnLengths.ConnectorFoodEntry.MealName);
        RuleFor(x => x.ServingDescription).MaximumLength(ColumnLengths.ConnectorFoodEntry.ServingDescription);
        RuleFor(x => x.Food).SetValidator(new ConnectorFoodImportValidator());
    }
}
