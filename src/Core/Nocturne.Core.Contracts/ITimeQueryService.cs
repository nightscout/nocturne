using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for handling time-based queries and advanced data slicing
/// Provides 1:1 compatibility with legacy JavaScript time pattern endpoints
/// </summary>
public interface ITimeQueryService
{
    /// <summary>
    /// Execute a time-based query with pattern matching
    /// Implements /api/v1/times/:prefix/:regex functionality
    /// </summary>
    /// <param name="prefix">Time prefix pattern (e.g., "2015-04", "20{14..15}")</param>
    /// <param name="regex">Time regex pattern (e.g., "T{13..18}:{00..15}")</param>
    /// <param name="storage">Storage type ("entries", "treatments", "devicestatus")</param>
    /// <param name="fieldName">Field to match patterns against (default "dateString")</param>
    /// <param name="queryParameters">Additional query parameters from request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Entries matching the time patterns</returns>
    /// <exception cref="ArgumentException">Thrown when the storage type is not supported.</exception>
    Task<IEnumerable<Entry>> ExecuteTimeQueryAsync(
        string? prefix,
        string? regex,
        string storage = "entries",
        string fieldName = "dateString",
        Dictionary<string, object>? queryParameters = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Execute an advanced slice query with field and type filtering
    /// Implements /api/v1/slice/:storage/:field/:type/:prefix/:regex functionality
    /// </summary>
    /// <param name="storage">Storage type ("entries", "treatments", "devicestatus")</param>
    /// <param name="field">Field to perform pattern matching on</param>
    /// <param name="type">Entry type filter</param>
    /// <param name="prefix">Pattern prefix</param>
    /// <param name="regex">Pattern regex</param>
    /// <param name="queryParameters">Additional query parameters from request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Entries matching the slice criteria</returns>
    /// <exception cref="ArgumentException">Thrown when the storage type is not supported.</exception>
    Task<IEnumerable<Entry>> ExecuteSliceQueryAsync(
        string storage,
        string field,
        string? type,
        string? prefix,
        string? regex,
        Dictionary<string, object>? queryParameters = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Generate debug information for time pattern queries
    /// Implements /api/v1/times/echo/:prefix/:regex functionality
    /// </summary>
    /// <param name="prefix">Time prefix pattern</param>
    /// <param name="regex">Time regex pattern</param>
    /// <param name="storage">Storage type</param>
    /// <param name="fieldName">Field name</param>
    /// <param name="queryParameters">Additional query parameters</param>
    /// <returns>Debug information about the generated patterns and query</returns>
    TimeQueryEcho GenerateTimeQueryEcho(
        string? prefix,
        string? regex,
        string storage = "entries",
        string fieldName = "dateString",
        Dictionary<string, object>? queryParameters = null
    );
}
