using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for StateSpan operations
/// </summary>
public class StateSpanRepository
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the StateSpanRepository class
    /// </summary>
    public StateSpanRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get state spans with optional filtering
    /// </summary>
    public async Task<IEnumerable<StateSpan>> GetStateSpansAsync(
        StateSpanCategory? category = null,
        string? state = null,
        long? from = null,
        long? to = null,
        string? source = null,
        bool? active = null,
        int count = 100,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        var query = _context.StateSpans.AsQueryable();

        if (category.HasValue)
            query = query.Where(s => s.Category == category.Value.ToString());

        if (!string.IsNullOrEmpty(state))
            query = query.Where(s => s.State == state);

        if (!string.IsNullOrEmpty(source))
            query = query.Where(s => s.Source == source);

        if (from.HasValue)
            query = query.Where(s => s.EndMills == null || s.EndMills >= from.Value);

        if (to.HasValue)
            query = query.Where(s => s.StartMills <= to.Value);

        if (active.HasValue)
        {
            if (active.Value)
                query = query.Where(s => s.EndMills == null);
            else
                query = query.Where(s => s.EndMills != null);
        }

        var entities = await query
            .OrderByDescending(s => s.StartMills)
            .Skip(skip)
            .Take(count)
            .ToListAsync(cancellationToken);

        return entities.Select(StateSpanMapper.ToDomainModel);
    }

    /// <summary>
    /// Get a specific state span by ID
    /// </summary>
    public async Task<StateSpan?> GetStateSpanByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.StateSpans.FirstOrDefaultAsync(
            s => s.OriginalId == id,
            cancellationToken);

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId,
                cancellationToken);
        }

        return entity != null ? StateSpanMapper.ToDomainModel(entity) : null;
    }

    /// <summary>
    /// Create or update a state span (upsert by originalId)
    /// </summary>
    public async Task<StateSpan> UpsertStateSpanAsync(
        StateSpan stateSpan,
        CancellationToken cancellationToken = default)
    {
        StateSpanEntity? entity = null;

        // Check for existing by originalId
        if (!string.IsNullOrEmpty(stateSpan.OriginalId))
        {
            entity = await _context.StateSpans.FirstOrDefaultAsync(
                s => s.OriginalId == stateSpan.OriginalId,
                cancellationToken);
        }

        if (entity != null)
        {
            StateSpanMapper.UpdateEntity(entity, stateSpan);
        }
        else
        {
            entity = StateSpanMapper.ToEntity(stateSpan);
            _context.StateSpans.Add(entity);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return StateSpanMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Bulk upsert state spans (for connector imports)
    /// </summary>
    public async Task<int> BulkUpsertAsync(
        IEnumerable<StateSpan> stateSpans,
        CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var span in stateSpans)
        {
            await UpsertStateSpanAsync(span, cancellationToken);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Update an existing state span
    /// </summary>
    public async Task<StateSpan?> UpdateStateSpanAsync(
        string id,
        StateSpan stateSpan,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.StateSpans.FirstOrDefaultAsync(
            s => s.OriginalId == id,
            cancellationToken);

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId,
                cancellationToken);
        }

        if (entity == null)
            return null;

        StateSpanMapper.UpdateEntity(entity, stateSpan);
        await _context.SaveChangesAsync(cancellationToken);
        return StateSpanMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Delete a state span
    /// </summary>
    public async Task<bool> DeleteStateSpanAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.StateSpans.FirstOrDefaultAsync(
            s => s.OriginalId == id,
            cancellationToken);

        if (entity == null && Guid.TryParse(id, out var guidId))
        {
            entity = await _context.StateSpans.FirstOrDefaultAsync(
                s => s.Id == guidId,
                cancellationToken);
        }

        if (entity == null)
            return false;

        _context.StateSpans.Remove(entity);
        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    /// <summary>
    /// Delete all state spans with the specified data source
    /// </summary>
    public async Task<long> DeleteBySourceAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        var deletedCount = await _context.StateSpans
            .Where(s => s.Source == source)
            .ExecuteDeleteAsync(cancellationToken);
        return deletedCount;
    }

    /// <summary>
    /// Get state spans by category
    /// </summary>
    public async Task<IEnumerable<StateSpan>> GetByCategory(
        StateSpanCategory category,
        long? from = null,
        long? to = null,
        CancellationToken cancellationToken = default)
    {
        return await GetStateSpansAsync(
            category: category,
            from: from,
            to: to,
            cancellationToken: cancellationToken);
    }
}
