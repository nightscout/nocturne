using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services;

/// <summary>
/// Domain service implementation for heart rate record operations with WebSocket broadcasting.
/// Heart rate data is stored directly in the heart_rates table.
/// </summary>
public class HeartRateService : IHeartRateService
{
    private readonly NocturneDbContext _dbContext;
    private readonly IDocumentProcessingService _documentProcessingService;
    private readonly ISignalRBroadcastService _signalRBroadcastService;
    private readonly ILogger<HeartRateService> _logger;

    public HeartRateService(
        NocturneDbContext dbContext,
        IDocumentProcessingService documentProcessingService,
        ISignalRBroadcastService signalRBroadcastService,
        ILogger<HeartRateService> logger
    )
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _documentProcessingService =
            documentProcessingService
            ?? throw new ArgumentNullException(nameof(documentProcessingService));
        _signalRBroadcastService =
            signalRBroadcastService
            ?? throw new ArgumentNullException(nameof(signalRBroadcastService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<HeartRate>> GetHeartRatesAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Getting heart rate records with count: {Count}, skip: {Skip}",
                count,
                skip
            );

            var entities = await _dbContext.HeartRates
                .OrderByDescending(h => h.Mills)
                .Skip(skip)
                .Take(count)
                .ToListAsync(cancellationToken);

            return entities.Select(HeartRateMapper.ToDomainModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting heart rate records");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<HeartRate?> GetHeartRateByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Getting heart rate record by ID: {Id}", id);

            var entity = Guid.TryParse(id, out var guid)
                ? await _dbContext.HeartRates.FirstOrDefaultAsync(
                    h => h.Id == guid,
                    cancellationToken
                )
                : await _dbContext.HeartRates.FirstOrDefaultAsync(
                    h => h.OriginalId == id,
                    cancellationToken
                );

            return entity is null ? null : HeartRateMapper.ToDomainModel(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting heart rate record by ID: {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<HeartRate>> CreateHeartRatesAsync(
        IEnumerable<HeartRate> heartRates,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var heartRateList = heartRates.ToList();
            _logger.LogDebug("Creating {Count} heart rate records", heartRateList.Count);

            var processed = _documentProcessingService.ProcessDocuments(heartRateList).ToList();

            var entities = processed.Select(HeartRateMapper.ToEntity).ToList();
            await _dbContext.HeartRates.AddRangeAsync(entities, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var result = entities.Select(HeartRateMapper.ToDomainModel).ToList();

            await _signalRBroadcastService.BroadcastStorageCreateAsync(
                "heartrate",
                new { collection = "heartrate", data = result, count = result.Count }
            );

            _logger.LogDebug("Successfully created {Count} heart rate records", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating heart rate records");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<HeartRate?> UpdateHeartRateAsync(
        string id,
        HeartRate heartRate,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Updating heart rate record with ID: {Id}", id);

            var entity = Guid.TryParse(id, out var guid)
                ? await _dbContext.HeartRates.FirstOrDefaultAsync(
                    h => h.Id == guid,
                    cancellationToken
                )
                : await _dbContext.HeartRates.FirstOrDefaultAsync(
                    h => h.OriginalId == id,
                    cancellationToken
                );

            if (entity is null)
            {
                _logger.LogDebug("Heart rate record with ID {Id} not found for update", id);
                return null;
            }

            HeartRateMapper.UpdateEntity(entity, heartRate);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var result = HeartRateMapper.ToDomainModel(entity);

            await _signalRBroadcastService.BroadcastStorageUpdateAsync(
                "heartrate",
                new { collection = "heartrate", data = result, id = id }
            );

            _logger.LogDebug("Successfully updated heart rate record with ID: {Id}", id);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating heart rate record with ID: {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteHeartRateAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Deleting heart rate record with ID: {Id}", id);

            var entity = Guid.TryParse(id, out var guid)
                ? await _dbContext.HeartRates.FirstOrDefaultAsync(
                    h => h.Id == guid,
                    cancellationToken
                )
                : await _dbContext.HeartRates.FirstOrDefaultAsync(
                    h => h.OriginalId == id,
                    cancellationToken
                );

            if (entity is null)
            {
                _logger.LogDebug("Heart rate record with ID {Id} not found for deletion", id);
                return false;
            }

            _dbContext.HeartRates.Remove(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _signalRBroadcastService.BroadcastStorageDeleteAsync(
                "heartrate",
                new { collection = "heartrate", id = id }
            );

            _logger.LogDebug("Successfully deleted heart rate record with ID: {Id}", id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting heart rate record with ID: {Id}", id);
            throw;
        }
    }
}
