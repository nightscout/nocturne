using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for connector food entry operations
/// </summary>
public class ConnectorFoodEntryRepository : IConnectorFoodEntryRepository
{
    private readonly INocturneDbContextFactory _contexts;

    /// <inheritdoc cref="IConnectorFoodEntryRepository" />
    public ConnectorFoodEntryRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <inheritdoc />
    public async Task<ConnectorFoodEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await db.ConnectorFoodEntries
            .Include(e => e.Food)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        return entity != null ? MapToDomain(entity) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectorFoodEntry>> GetPendingInTimeRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entities = await db.ConnectorFoodEntries
            .Include(e => e.Food)
            .Where(e => e.Status == ConnectorFoodEntryStatus.Pending)
            .Where(e => e.ConsumedAt >= from && e.ConsumedAt <= to)
            .OrderBy(e => e.ConsumedAt)
            .ToListAsync(ct);

        return entities.Select(MapToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectorFoodEntry>> GetByIdsAsync(
        IEnumerable<Guid> ids,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var idList = ids.ToList();
        var entities = await db.ConnectorFoodEntries
            .Include(e => e.Food)
            .Where(e => idList.Contains(e.Id))
            .ToListAsync(ct);

        return entities.Select(MapToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task UpdateStatusAsync(
        Guid id,
        ConnectorFoodEntryStatus status,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var entity = await db.ConnectorFoodEntries
            .FirstOrDefaultAsync(e => e.Id == id, ct);

        if (entity != null)
        {
            entity.Status = status;
            entity.ResolvedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private static ConnectorFoodEntry MapToDomain(ConnectorFoodEntryEntity entity)
    {
        return new ConnectorFoodEntry
        {
            Id = entity.Id,
            ConnectorSource = entity.ConnectorSource,
            ExternalEntryId = entity.ExternalEntryId,
            ExternalFoodId = entity.ExternalFoodId,
            FoodId = entity.FoodId,
            Food = entity.Food != null ? FoodMapper.ToDomainModel(entity.Food) : null,
            ConsumedAt = entity.ConsumedAt,
            LoggedAt = entity.LoggedAt,
            MealName = entity.MealName,
            Carbs = entity.Carbs,
            Protein = entity.Protein,
            Fat = entity.Fat,
            Energy = entity.Energy,
            Servings = entity.Servings,
            ServingDescription = entity.ServingDescription,
            Status = entity.Status,
            ResolvedAt = entity.ResolvedAt,
        };
    }
}
