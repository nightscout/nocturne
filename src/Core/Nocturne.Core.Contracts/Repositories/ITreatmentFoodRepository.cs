using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// Repository port for <see cref="TreatmentFood"/> breakdown operations linked to carb intake records.
/// </summary>
/// <seealso cref="TreatmentFood"/>
/// <seealso cref="Food"/>
/// <seealso cref="IFoodRepository"/>
public interface ITreatmentFoodRepository
{
    /// <summary>
    /// Get food breakdown entries for a carb intake record.
    /// </summary>
    Task<IReadOnlyList<TreatmentFood>> GetByCarbIntakeIdAsync(
        Guid carbIntakeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get food breakdown entries for multiple carb intake records.
    /// </summary>
    Task<IReadOnlyList<TreatmentFood>> GetByCarbIntakeIdsAsync(
        IEnumerable<Guid> carbIntakeIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a food breakdown entry.
    /// </summary>
    Task<TreatmentFood> CreateAsync(
        TreatmentFood entry,
        CancellationToken cancellationToken = default);

    /// <summary>Creates food breakdown entries in one save.</summary>
    Task CreateManyAsync(
        IReadOnlyCollection<TreatmentFood> entries,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update a food breakdown entry.
    /// </summary>
    Task<TreatmentFood?> UpdateAsync(
        TreatmentFood entry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a food breakdown entry.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count how many food attribution entries reference a specific food.
    /// </summary>
    Task<int> CountByFoodIdAsync(
        Guid foodId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear food references for a specific food (set FoodId to null), keeping the attribution entries as "Other".
    /// </summary>
    Task<int> ClearFoodReferencesByFoodIdAsync(
        Guid foodId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all food attribution entries that reference a specific food.
    /// </summary>
    Task<int> DeleteByFoodIdAsync(
        Guid foodId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes the breakdowns of <paramref name="carbIntakeIds"/> and writes
    /// <paramref name="entries"/> in their place in one transaction. For the sample-data seeder's idempotent
    /// re-seed only.
    /// </summary>
    Task ReplaceForCarbIntakesAsync(
        IReadOnlyCollection<Guid> carbIntakeIds,
        IReadOnlyCollection<TreatmentFood> entries,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get recently used foods ordered by last usage.
    /// </summary>
    Task<IReadOnlyList<Food>> GetRecentFoodsAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
