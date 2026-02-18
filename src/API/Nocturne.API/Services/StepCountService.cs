using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services;

/// <summary>
/// Domain service implementation for step count record operations with WebSocket broadcasting.
/// Step count data is stored directly in the step_counts table.
/// </summary>
public class StepCountService : IStepCountService
{
    private readonly NocturneDbContext _dbContext;
    private readonly IDocumentProcessingService _documentProcessingService;
    private readonly ISignalRBroadcastService _signalRBroadcastService;
    private readonly ILogger<StepCountService> _logger;

    public StepCountService(
        NocturneDbContext dbContext,
        IDocumentProcessingService documentProcessingService,
        ISignalRBroadcastService signalRBroadcastService,
        ILogger<StepCountService> logger
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
    public async Task<IEnumerable<StepCount>> GetStepCountsAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Getting step count records with count: {Count}, skip: {Skip}",
                count,
                skip
            );

            var entities = await _dbContext.StepCounts
                .OrderByDescending(s => s.Mills)
                .Skip(skip)
                .Take(count)
                .ToListAsync(cancellationToken);

            return entities.Select(StepCountMapper.ToDomainModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting step count records");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<StepCount?> GetStepCountByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Getting step count record by ID: {Id}", id);

            var entity = Guid.TryParse(id, out var guid)
                ? await _dbContext.StepCounts.FirstOrDefaultAsync(
                    s => s.Id == guid,
                    cancellationToken
                )
                : await _dbContext.StepCounts.FirstOrDefaultAsync(
                    s => s.OriginalId == id,
                    cancellationToken
                );

            return entity is null ? null : StepCountMapper.ToDomainModel(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting step count record by ID: {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StepCount>> CreateStepCountsAsync(
        IEnumerable<StepCount> stepCounts,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var stepCountList = stepCounts.ToList();
            _logger.LogDebug("Creating {Count} step count records", stepCountList.Count);

            var processed = _documentProcessingService.ProcessDocuments(stepCountList).ToList();

            var entities = processed.Select(StepCountMapper.ToEntity).ToList();
            await _dbContext.StepCounts.AddRangeAsync(entities, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var result = entities.Select(StepCountMapper.ToDomainModel).ToList();

            await _signalRBroadcastService.BroadcastStorageCreateAsync(
                "stepcount",
                new { collection = "stepcount", data = result, count = result.Count }
            );

            _logger.LogDebug("Successfully created {Count} step count records", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating step count records");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<StepCount?> UpdateStepCountAsync(
        string id,
        StepCount stepCount,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Updating step count record with ID: {Id}", id);

            var entity = Guid.TryParse(id, out var guid)
                ? await _dbContext.StepCounts.FirstOrDefaultAsync(
                    s => s.Id == guid,
                    cancellationToken
                )
                : await _dbContext.StepCounts.FirstOrDefaultAsync(
                    s => s.OriginalId == id,
                    cancellationToken
                );

            if (entity is null)
            {
                _logger.LogDebug("Step count record with ID {Id} not found for update", id);
                return null;
            }

            StepCountMapper.UpdateEntity(entity, stepCount);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var result = StepCountMapper.ToDomainModel(entity);

            await _signalRBroadcastService.BroadcastStorageUpdateAsync(
                "stepcount",
                new { collection = "stepcount", data = result, id = id }
            );

            _logger.LogDebug("Successfully updated step count record with ID: {Id}", id);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating step count record with ID: {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteStepCountAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Deleting step count record with ID: {Id}", id);

            var entity = Guid.TryParse(id, out var guid)
                ? await _dbContext.StepCounts.FirstOrDefaultAsync(
                    s => s.Id == guid,
                    cancellationToken
                )
                : await _dbContext.StepCounts.FirstOrDefaultAsync(
                    s => s.OriginalId == id,
                    cancellationToken
                );

            if (entity is null)
            {
                _logger.LogDebug("Step count record with ID {Id} not found for deletion", id);
                return false;
            }

            _dbContext.StepCounts.Remove(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _signalRBroadcastService.BroadcastStorageDeleteAsync(
                "stepcount",
                new { collection = "stepcount", id = id }
            );

            _logger.LogDebug("Successfully deleted step count record with ID: {Id}", id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting step count record with ID: {Id}", id);
            throw;
        }
    }
}
