using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Domain service implementation for food breakdown operations linked to carb intake records.
/// </summary>
public class TreatmentFoodService : ITreatmentFoodService
{
    private readonly NocturneDbContext _context;
    private readonly TreatmentFoodRepository _treatmentFoodRepository;
    private readonly ILogger<TreatmentFoodService> _logger;

    public TreatmentFoodService(
        NocturneDbContext context,
        TreatmentFoodRepository treatmentFoodRepository,
        ILogger<TreatmentFoodService> logger
    )
    {
        _context = context;
        _treatmentFoodRepository = treatmentFoodRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TreatmentFoodBreakdown?> GetByCarbIntakeIdAsync(
        Guid carbIntakeId,
        CancellationToken cancellationToken = default
    )
    {
        var carbIntakeEntity = await _context
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
}
