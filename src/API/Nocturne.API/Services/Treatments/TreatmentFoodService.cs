using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// Domain service for food breakdown operations linked to v4 carb intake records.
/// Manages <see cref="TreatmentFood"/> entries that associate specific food items with a
/// <see cref="CarbIntake"/>, including portion, carb grams, and time offset.
/// </summary>
/// <seealso cref="ITreatmentFoodService"/>
public class TreatmentFoodService : ITreatmentFoodService
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly ITreatmentFoodRepository _treatmentFoodRepository;
    private readonly ILogger<TreatmentFoodService> _logger;

    public TreatmentFoodService(
        INocturneDbContextFactory contexts,
        ITreatmentFoodRepository treatmentFoodRepository,
        ILogger<TreatmentFoodService> logger
    )
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _treatmentFoodRepository = treatmentFoodRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TreatmentFoodBreakdown?> GetByCarbIntakeIdAsync(
        Guid carbIntakeId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var carbIntakeEntity = await db
            .Set<CarbIntakeEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == carbIntakeId, cancellationToken);

        if (carbIntakeEntity == null)
        {
            return null;
        }

        var entries = await _treatmentFoodRepository.GetByCarbIntakeIdAsync(
            carbIntakeId,
            cancellationToken
        );

        var attributedCarbs = entries.Sum(entry => entry.Carbs);
        var totalCarbs = (decimal)carbIntakeEntity.Carbs;

        return new TreatmentFoodBreakdown
        {
            CarbIntakeId = carbIntakeEntity.Id,
            Foods = entries.ToList(),
            IsAttributed = entries.Count > 0,
            AttributedCarbs = attributedCarbs,
            UnspecifiedCarbs = totalCarbs - attributedCarbs,
        };
    }

    /// <inheritdoc />
    public async Task<IEnumerable<TreatmentFood>> GetByCarbIntakeIdsAsync(
        IEnumerable<Guid> carbIntakeIds,
        CancellationToken cancellationToken = default
    )
    {
        return await _treatmentFoodRepository.GetByCarbIntakeIdsAsync(
            carbIntakeIds,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<TreatmentFood> AddAsync(
        TreatmentFood entry,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Creating food entry for carb intake {CarbIntakeId}", entry.CarbIntakeId);
        return await _treatmentFoodRepository.CreateAsync(entry, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TreatmentFood?> UpdateAsync(
        TreatmentFood entry,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Updating food entry {EntryId}", entry.Id);
        return await _treatmentFoodRepository.UpdateAsync(entry, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting food entry {EntryId}", id);
        return await _treatmentFoodRepository.DeleteAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CountByFoodIdAsync(Guid foodId, CancellationToken cancellationToken = default)
    {
        return await _treatmentFoodRepository.CountByFoodIdAsync(foodId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> ClearFoodReferencesByFoodIdAsync(Guid foodId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Clearing food references for food {FoodId}", foodId);
        return await _treatmentFoodRepository.ClearFoodReferencesByFoodIdAsync(foodId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> DeleteByFoodIdAsync(Guid foodId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting all attribution entries for food {FoodId}", foodId);
        return await _treatmentFoodRepository.DeleteByFoodIdAsync(foodId, cancellationToken);
    }
}
