using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Domain service for food breakdown operations linked to carb intake records.
/// </summary>
public interface ITreatmentFoodService
{
    /// <summary>
    /// Get food breakdown for a carb intake record.
    /// </summary>
    Task<TreatmentFoodBreakdown?> GetByCarbIntakeIdAsync(
        Guid carbIntakeId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get food breakdown entries for multiple carb intake records.
    /// </summary>
    Task<IEnumerable<TreatmentFood>> GetByCarbIntakeIdsAsync(
        IEnumerable<Guid> carbIntakeIds,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Add a new food breakdown entry.
    /// </summary>
    Task<TreatmentFood> AddAsync(
        TreatmentFood entry,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an existing food breakdown entry.
    /// </summary>
    Task<TreatmentFood?> UpdateAsync(
        TreatmentFood entry,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a food breakdown entry.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count how many food attribution entries reference a specific food.
    /// </summary>
    Task<int> CountByFoodIdAsync(Guid foodId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear food references for a specific food (set FoodId to null), keeping entries as "Other".
    /// </summary>
    Task<int> ClearFoodReferencesByFoodIdAsync(Guid foodId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all food attribution entries that reference a specific food.
    /// </summary>
    Task<int> DeleteByFoodIdAsync(Guid foodId, CancellationToken cancellationToken = default);
}
