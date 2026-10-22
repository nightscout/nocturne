using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// Repository for compression low suggestion operations
/// </summary>
public class CompressionLowRepository : ICompressionLowRepository
{
    private readonly INocturneDbContextFactory _contexts;

    /// <inheritdoc cref="ICompressionLowRepository" />
    public CompressionLowRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>
    /// Get suggestions with optional filtering
    /// </summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="nightOf">Optional night filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of compression low suggestions.</returns>
    public async Task<IEnumerable<CompressionLowSuggestion>> GetSuggestionsAsync(
        CompressionLowStatus? status = null,
        DateOnly? nightOf = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var query = db.CompressionLowSuggestions.AsQueryable();

        if (status.HasValue)
            query = query.Where(s => s.Status == status.Value.ToString());

        if (nightOf.HasValue)
            query = query.Where(s => s.NightOf == nightOf.Value);

        var entities = await query
            .OrderByDescending(s => s.NightOf)
            .ThenByDescending(s => s.StartMills)
            .ToListAsync(cancellationToken);

        return entities.Select(CompressionLowMapper.ToDomainModel);
    }

    /// <summary>
    /// Get a specific suggestion by ID
    /// </summary>
    /// <param name="id">The unique identifier of the suggestion.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The suggestion, or null if not found.</returns>
    public async Task<CompressionLowSuggestion?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db.CompressionLowSuggestions
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        return entity != null ? CompressionLowMapper.ToDomainModel(entity) : null;
    }

    /// <summary>
    /// Create a new suggestion
    /// </summary>
    /// <param name="suggestion">The suggestion to create.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created suggestion.</returns>
    public async Task<CompressionLowSuggestion> CreateAsync(
        CompressionLowSuggestion suggestion,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = CompressionLowMapper.ToEntity(suggestion);
        db.CompressionLowSuggestions.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return CompressionLowMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task CreateManyAsync(
        IReadOnlyCollection<CompressionLowSuggestion> suggestions,
        CancellationToken cancellationToken = default)
    {
        if (suggestions.Count == 0)
            return;

        await using var db = await _contexts.CreateAsync(cancellationToken);
        db.CompressionLowSuggestions.AddRange(suggestions.Select(CompressionLowMapper.ToEntity));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Update an existing suggestion
    /// </summary>
    /// <param name="suggestion">The suggestion with updated data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated suggestion, or null if not found.</returns>
    public async Task<CompressionLowSuggestion?> UpdateAsync(
        CompressionLowSuggestion suggestion,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db.CompressionLowSuggestions
            .FirstOrDefaultAsync(s => s.Id == suggestion.Id, cancellationToken);

        if (entity == null)
            return null;

        CompressionLowMapper.UpdateEntity(entity, suggestion);
        await db.SaveChangesAsync(cancellationToken);
        return CompressionLowMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Count pending suggestions for a night
    /// </summary>
    /// <param name="nightOf">The night to check.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The count of pending suggestions.</returns>
    public async Task<int> CountPendingForNightAsync(
        DateOnly nightOf,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return await db.CompressionLowSuggestions
            .CountAsync(s => s.NightOf == nightOf && s.Status == "Pending", cancellationToken);
    }

    /// <summary>
    /// Check if active (Pending or Accepted) suggestions exist for a night.
    /// Dismissed suggestions do not block re-detection.
    /// </summary>
    /// <param name="nightOf">The night to check.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if active suggestions exist, otherwise false.</returns>
    public async Task<bool> ActiveSuggestionsExistForNightAsync(
        DateOnly nightOf,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return await db.CompressionLowSuggestions
            .AnyAsync(s => s.NightOf == nightOf
                && (s.Status == "Pending" || s.Status == "Accepted"),
                cancellationToken);
    }

    /// <summary>
    /// Delete a suggestion by ID
    /// </summary>
    /// <param name="id">The unique identifier of the suggestion to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the suggestion was deleted, otherwise false.</returns>
    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entity = await db.CompressionLowSuggestions
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (entity == null)
            return false;

        db.CompressionLowSuggestions.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
