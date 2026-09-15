using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core-backed store for manually-entered lab HbA1c results. Deliberately outside the V4
/// sync/dedup family — these rows are never uploaded by a device or connector, only typed in
/// once or twice a year, so there is nothing to deduplicate.
/// </summary>
public class LabHbA1cResultRepository : ILabHbA1cResultRepository
{
    private readonly ITenantDbContextFactory _factory;

    public LabHbA1cResultRepository(ITenantDbContextFactory factory)
    {
        _factory = factory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LabHbA1cResult>> GetAllAsync(CancellationToken ct = default)
    {
        await using var context = await _factory.CreateAsync(ct);
        var entities = await context
            .LabHbA1cResults.OrderBy(e => e.MeasuredAt)
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<LabHbA1cResult> CreateAsync(LabHbA1cResult result, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateAsync(ct);
        var entity = new LabHbA1cResultEntity
        {
            Id = Guid.CreateVersion7(),
            MeasuredAt = result.MeasuredAt,
            ValuePercent = result.ValuePercent,
            Note = result.Note,
            CreatedAt = DateTime.UtcNow,
        };
        context.LabHbA1cResults.Add(entity);
        await context.SaveChangesAsync(ct);
        return ToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await _factory.CreateAsync(ct);
        var entity = await context.LabHbA1cResults.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null)
            return false;

        entity.DeletedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
        return true;
    }

    private static LabHbA1cResult ToDomain(LabHbA1cResultEntity entity) => new()
    {
        Id = entity.Id,
        MeasuredAt = entity.MeasuredAt,
        ValuePercent = entity.ValuePercent,
        Note = entity.Note,
        CreatedAt = entity.CreatedAt,
    };
}
