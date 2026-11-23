using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Treatments;

/// <inheritdoc cref="IMealMatchLinker"/>
public class MealMatchLinker(
    IConnectorFoodEntryRepository foodEntryRepository,
    ICarbIntakeRepository carbIntakeRepository,
    ILogger<MealMatchLinker> logger
) : IMealMatchLinker
{
    public async Task<AcceptMatchOutcome> LinkAsync(
        Guid foodEntryId,
        Guid carbIntakeId,
        decimal carbs,
        int timeOffsetMinutes,
        CancellationToken ct = default)
    {
        var foodEntry = await foodEntryRepository.GetByIdAsync(foodEntryId, ct);
        if (foodEntry == null)
        {
            logger.LogWarning("Food entry {FoodEntryId} not found", foodEntryId);
            return AcceptMatchOutcome.NotFound;
        }

        // treatment_foods.carb_intake_id carries no foreign key, so an unknown id here would
        // persist a row no breakdown can ever resolve, and flip the food entry to Matched,
        // so it never resurfaces as a suggestion.
        var carbIntake = await carbIntakeRepository.GetByIdAsync(carbIntakeId, ct);
        if (carbIntake == null)
        {
            logger.LogWarning("Carb intake {CarbIntakeId} not found", carbIntakeId);
            return AcceptMatchOutcome.NotFound;
        }

        var treatmentFood = new TreatmentFood
        {
            Id = Guid.CreateVersion7(),
            CarbIntakeId = carbIntakeId,
            FoodId = foodEntry.FoodId,
            Portions = foodEntry.Servings,
            Carbs = carbs,
            TimeOffsetMinutes = timeOffsetMinutes,
            FoodName = foodEntry.Food?.Name ?? foodEntry.MealName,
            CarbsPerPortion = foodEntry.Servings > 0 ? carbs / foodEntry.Servings : null,
        };

        var outcome = await foodEntryRepository.AcceptMatchAsync(foodEntryId, treatmentFood, ct);

        logger.LogInformation(
            "Accepted meal match for food entry {FoodEntryId} and carb intake {CarbIntakeId}: {Outcome}",
            foodEntryId,
            carbIntakeId,
            outcome);
        return outcome;
    }
}
