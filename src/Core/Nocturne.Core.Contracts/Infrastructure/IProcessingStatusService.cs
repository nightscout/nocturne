using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Infrastructure;

/// <summary>
/// Service for tracking async processing status
/// </summary>
public interface IProcessingStatusService
{
    /// <param name="correlationId">The correlation ID to look up</param>
    /// <returns>Processing status or null if not found</returns>
    Task<ProcessingStatus?> GetStatusAsync(
        string correlationId,
        CancellationToken cancellationToken = default
    );

    /// <param name="status">The updated status</param>
    Task UpdateStatusAsync(
        string correlationId,
        ProcessingStatus status,
        CancellationToken cancellationToken = default
    );

    Task MarkCompletedAsync(
        string correlationId,
        object? results = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Marks processing as failed with error messages
    /// </summary>
    /// <param name="errors">Error messages</param>
    Task MarkFailedAsync(
        string correlationId,
        IEnumerable<string> errors,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Initializes processing status for a new correlation ID
    /// </summary>
    /// <param name="totalCount">Total number of items to process</param>
    Task InitializeAsync(
        string correlationId,
        int totalCount,
        CancellationToken cancellationToken = default
    );

    Task UpdateProgressAsync(
        string correlationId,
        int processedCount,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Waits for processing to complete with a timeout
    /// </summary>
    /// <param name="timeout">Maximum time to wait</param>
    /// <returns>Final processing status or null if timeout</returns>
    Task<ProcessingStatus?> WaitForCompletionAsync(
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    );
}
