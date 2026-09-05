using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Constants;

namespace Nocturne.Infrastructure.Cache.Services;

/// <summary>
/// Tracks async processing status in process memory. State is per-node and does not survive a
/// restart, so a correlation ID minted by one node is unknown to any other.
/// </summary>
public class MemoryProcessingStatusService : IProcessingStatusService
{
    private readonly ILogger<MemoryProcessingStatusService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, ProcessingStatus> _statusCache;
    private readonly ITimer _cleanupTimer;
    private readonly TimeSpan _defaultTtl = CacheConstants.DefaultTtl.ProcessingStatus;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public MemoryProcessingStatusService(
        ILogger<MemoryProcessingStatusService> logger,
        TimeProvider timeProvider
    )
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _statusCache = new ConcurrentDictionary<string, ProcessingStatus>();

        _cleanupTimer = _timeProvider.CreateTimer(
            CleanupExpiredEntries,
            null,
            CacheConstants.CleanupIntervals.StatusCleanup,
            CacheConstants.CleanupIntervals.StatusCleanup
        );
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public Task<ProcessingStatus?> GetStatusAsync(
        string correlationId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (_statusCache.TryGetValue(correlationId, out var status))
            {
                // Check if expired
                if (status.StartedAt.Add(_defaultTtl) < UtcNow)
                {
                    _statusCache.TryRemove(correlationId, out _);
                    return Task.FromResult<ProcessingStatus?>(null);
                }

                return Task.FromResult<ProcessingStatus?>(status);
            }

            return Task.FromResult<ProcessingStatus?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error retrieving processing status for correlation ID: {CorrelationId}",
                correlationId
            );
            return Task.FromResult<ProcessingStatus?>(null);
        }
    }

    /// <inheritdoc />
    public Task UpdateStatusAsync(
        string correlationId,
        ProcessingStatus status,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _statusCache.AddOrUpdate(correlationId, status, (key, existing) => status);

            _logger.LogDebug(
                "Updated processing status for correlation ID: {CorrelationId} to {Status}",
                correlationId,
                status.Status
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error updating processing status for correlation ID: {CorrelationId}",
                correlationId
            );
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task MarkCompletedAsync(
        string correlationId,
        object? results = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var status = await GetStatusAsync(correlationId, cancellationToken);
            if (status == null)
            {
                _logger.LogWarning(
                    "Cannot mark as completed - processing status not found for correlation ID: {CorrelationId}",
                    correlationId
                );
                return;
            }

            status.Status = CacheConstants.ProcessingStatus.Completed;
            status.CompletedAt = UtcNow;
            status.Progress = 100;
            if (results != null)
            {
                status.Results = results;
            }

            await UpdateStatusAsync(correlationId, status, cancellationToken);

            _logger.LogInformation(
                "Marked processing as completed for correlation ID: {CorrelationId}",
                correlationId
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error marking processing as completed for correlation ID: {CorrelationId}",
                correlationId
            );
        }
    }

    /// <inheritdoc />
    public async Task MarkFailedAsync(
        string correlationId,
        IEnumerable<string> errors,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var status = await GetStatusAsync(correlationId, cancellationToken);
            if (status == null)
            {
                _logger.LogWarning(
                    "Cannot mark as failed - processing status not found for correlation ID: {CorrelationId}",
                    correlationId
                );
                return;
            }

            status.Status = CacheConstants.ProcessingStatus.Failed;
            status.CompletedAt = UtcNow;
            status.Errors = errors.ToList();

            await UpdateStatusAsync(correlationId, status, cancellationToken);

            _logger.LogWarning(
                "Marked processing as failed for correlation ID: {CorrelationId} with {ErrorCount} errors",
                correlationId,
                status.Errors.Count
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error marking processing as failed for correlation ID: {CorrelationId}",
                correlationId
            );
        }
    }

    /// <inheritdoc />
    public Task InitializeAsync(
        string correlationId,
        int totalCount,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var status = new ProcessingStatus
            {
                CorrelationId = correlationId,
                Status = CacheConstants.ProcessingStatus.Pending,
                Progress = 0,
                ProcessedCount = 0,
                TotalCount = totalCount,
                StartedAt = UtcNow,
            };

            _statusCache.AddOrUpdate(correlationId, status, (key, existing) => status);

            _logger.LogDebug(
                "Initialized processing status for correlation ID: {CorrelationId} with {TotalCount} items",
                correlationId,
                totalCount
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error initializing processing status for correlation ID: {CorrelationId}",
                correlationId
            );
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task UpdateProgressAsync(
        string correlationId,
        int processedCount,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var status = await GetStatusAsync(correlationId, cancellationToken);
            if (status == null)
            {
                _logger.LogWarning(
                    "Cannot update progress - processing status not found for correlation ID: {CorrelationId}",
                    correlationId
                );
                return;
            }

            status.ProcessedCount = processedCount;
            status.Status = CacheConstants.ProcessingStatus.Processing;

            // Calculate progress percentage
            if (status.TotalCount > 0)
            {
                status.Progress = Math.Min((processedCount * 100) / status.TotalCount, 100);
            }

            await UpdateStatusAsync(correlationId, status, cancellationToken);

            _logger.LogDebug(
                "Updated progress for correlation ID: {CorrelationId} to {ProcessedCount}/{TotalCount} ({Progress}%)",
                correlationId,
                processedCount,
                status.TotalCount,
                status.Progress
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error updating progress for correlation ID: {CorrelationId}",
                correlationId
            );
        }
    }

    /// <inheritdoc />
    public async Task<ProcessingStatus?> WaitForCompletionAsync(
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token
            );

            var startTimestamp = _timeProvider.GetTimestamp();
            while (!cts.Token.IsCancellationRequested)
            {
                var status = await GetStatusAsync(correlationId, cts.Token);
                if (
                    status?.Status
                    is CacheConstants.ProcessingStatus.Completed
                        or CacheConstants.ProcessingStatus.Failed
                )
                {
                    _logger.LogDebug(
                        "Processing completed for correlation ID: {CorrelationId} after {ElapsedTime}ms",
                        correlationId,
                        _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds
                    );
                    return status;
                }

                try
                {
                    await Task.Delay(PollInterval, _timeProvider, cts.Token);
                }
                catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
                {
                    break;
                }
            }

            _logger.LogWarning(
                "Timeout waiting for processing completion for correlation ID: {CorrelationId} after {Timeout}ms",
                correlationId,
                timeout.TotalMilliseconds
            );
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error waiting for processing completion for correlation ID: {CorrelationId}",
                correlationId
            );
            return null;
        }
    }

    /// <summary>
    /// Cleanup expired entries from the cache
    /// </summary>
    private void CleanupExpiredEntries(object? state)
    {
        try
        {
            var cutoffTime = UtcNow.Subtract(_defaultTtl);
            var expiredKeys = _statusCache
                .Where(kvp => kvp.Value.StartedAt < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _statusCache.TryRemove(key, out _);
            }

            if (expiredKeys.Count > 0)
            {
                _logger.LogDebug(
                    "Cleaned up {Count} expired processing status entries",
                    expiredKeys.Count
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during processing status cleanup");
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }
}
