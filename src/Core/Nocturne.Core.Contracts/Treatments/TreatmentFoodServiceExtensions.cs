using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Treatments;

public static class TreatmentFoodServiceExtensions
{
    /// <summary>
    /// Sets <see cref="CarbIntake.FoodType"/> on each intake from one batched read of their food lines.
    /// </summary>
    public static async Task ApplyFoodTypesAsync(
        this ITreatmentFoodService foods,
        IReadOnlyCollection<CarbIntake> carbIntakes,
        CancellationToken ct = default)
    {
        if (carbIntakes.Count == 0)
            return;

        var lines = await foods.GetByCarbIntakeIdsAsync(carbIntakes.Select(c => c.Id), ct);
        TreatmentFood.ApplyFoodTypes(carbIntakes, lines.ToLookup(f => f.CarbIntakeId));
    }
}
