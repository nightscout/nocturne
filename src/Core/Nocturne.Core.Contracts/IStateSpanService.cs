using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Domain service for StateSpan operations
/// </summary>
public interface IStateSpanService
{
    /// <summary>
    /// Get state spans with optional filtering
    /// </summary>
    /// <param name="category">Optional category filter</param>
    /// <param name="state">Optional state filter</param>
    /// <param name="from">Optional start time in milliseconds since Unix epoch</param>
    /// <param name="to">Optional end time in milliseconds since Unix epoch</param>
    /// <param name="source">Optional source filter</param>
    /// <param name="active">Optional active status filter</param>
    /// <param name="count">Maximum number of state spans to return</param>
    /// <param name="skip">Number of state spans to skip for pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of state spans</returns>
    Task<IEnumerable<StateSpan>> GetStateSpansAsync(
        StateSpanCategory? category = null,
        string? state = null,
        long? from = null,
        long? to = null,
        string? source = null,
        bool? active = null,
        int count = 100,
        int skip = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a specific state span by ID
    /// </summary>
    /// <param name="id">State span ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>State span if found, null otherwise</returns>
    Task<StateSpan?> GetStateSpanByIdAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or update a state span (upsert by originalId)
    /// </summary>
    /// <param name="stateSpan">State span to create or update</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created or updated state span</returns>
    Task<StateSpan> UpsertStateSpanAsync(
        StateSpan stateSpan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a state span
    /// </summary>
    /// <param name="id">State span ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteStateSpanAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get temp basals as Treatments for v1-v3 API compatibility
    /// </summary>
    /// <param name="from">Optional start time in milliseconds since Unix epoch</param>
    /// <param name="to">Optional end time in milliseconds since Unix epoch</param>
    /// <param name="count">Maximum number of treatments to return</param>
    /// <param name="skip">Number of treatments to skip for pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of treatments representing temp basals</returns>
    Task<IEnumerable<Treatment>> GetTempBasalsAsTreatmentsAsync(
        long? from = null,
        long? to = null,
        int count = 100,
        int skip = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a temp basal from a Treatment (v1-v3 API ingest)
    /// </summary>
    /// <param name="treatment">Treatment to convert to a temp basal state span</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created state span</returns>
    Task<StateSpan> CreateTempBasalFromTreatmentAsync(
        Treatment treatment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing state span
    /// </summary>
    /// <param name="id">State span ID to update</param>
    /// <param name="stateSpan">Updated state span data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated state span if successful, null otherwise</returns>
    Task<StateSpan?> UpdateStateSpanAsync(
        string id,
        StateSpan stateSpan,
        CancellationToken cancellationToken = default);
}
