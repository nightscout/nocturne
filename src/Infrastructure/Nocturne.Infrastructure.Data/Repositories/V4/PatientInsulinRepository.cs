using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing patient insulin records (insulin types used) in the database.
/// </summary>
public class PatientInsulinRepository : IPatientInsulinRepository
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly ILogger<PatientInsulinRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PatientInsulinRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="logger">The logger instance.</param>
    public PatientInsulinRepository(
        ITenantDbContextFactory contextFactory,
        ILogger<PatientInsulinRepository> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets all patient insulin records.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of patient insulins.</returns>
    public async Task<IEnumerable<PatientInsulin>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.PatientInsulins
            .AsNoTracking()
            .OrderByDescending(e => e.IsCurrent)
            .ThenByDescending(e => e.StartDate)
            .ToListAsync(ct);

        return entities.Select(PatientInsulinMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets all currently used patient insulins.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of current patient insulins.</returns>
    public async Task<IEnumerable<PatientInsulin>> GetCurrentAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.PatientInsulins
            .AsNoTracking()
            .Where(e => e.IsCurrent)
            .OrderByDescending(e => e.StartDate)
            .ToListAsync(ct);

        return entities.Select(PatientInsulinMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets a patient insulin record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The patient insulin record, or null if not found.</returns>
    public async Task<PatientInsulin?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientInsulins.FindAsync([id], ct);
        return entity is null ? null : PatientInsulinMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new patient insulin record.
    /// </summary>
    /// <param name="model">The patient insulin to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created patient insulin record.</returns>
    public async Task<PatientInsulin> CreateAsync(PatientInsulin model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = PatientInsulinMapper.ToEntity(model);
        ctx.PatientInsulins.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return PatientInsulinMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing patient insulin record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated patient insulin record.</returns>
    public async Task<PatientInsulin> UpdateAsync(Guid id, PatientInsulin model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientInsulins.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"PatientInsulin {id} not found");

        PatientInsulinMapper.UpdateEntity(entity, model);
        await ctx.SaveChangesAsync(ct);
        return PatientInsulinMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a patient insulin record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientInsulins.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"PatientInsulin {id} not found");

        entity.DeletedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<PatientInsulin> RestoreAsync(Guid id, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientInsulins.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.Id == id && e.DeletedAt != null)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Soft-deleted PatientInsulin {id} not found");
        entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return PatientInsulinMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PatientInsulin>> BulkRestoreAsync(IEnumerable<Guid> ids, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var idSet = ids.ToHashSet();
        var entities = await ctx.PatientInsulins.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && idSet.Contains(e.Id) && e.DeletedAt != null)
            .ToListAsync(ct);
        foreach (var entity in entities)
            entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return entities.Select(PatientInsulinMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PatientInsulin>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.PatientInsulins.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt)
            .Skip(offset).Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
        return entities.Select(PatientInsulinMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.PatientInsulins.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .CountAsync(ct);
    }

    /// <summary>
    /// Gets the primary bolus insulin currently in use.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The primary bolus insulin, or null if none found.</returns>
    public async Task<PatientInsulin?> GetPrimaryBolusInsulinAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientInsulins
            .AsNoTracking()
            .Where(e => e.IsCurrent && e.IsPrimary && (e.Role == "Bolus" || e.Role == "Both"))
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : PatientInsulinMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets the primary basal insulin currently in use.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The primary basal insulin, or null if none found.</returns>
    public async Task<PatientInsulin?> GetPrimaryBasalInsulinAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientInsulins
            .AsNoTracking()
            .Where(e => e.IsCurrent && e.IsPrimary && (e.Role == "Basal" || e.Role == "Both"))
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : PatientInsulinMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Sets a specific insulin record as primary, clearing other primary flags in the same role scope.
    /// </summary>
    /// <param name="insulinId">The unique identifier of the insulin record to set as primary.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task SetPrimaryAsync(Guid insulinId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var target = await ctx.PatientInsulins.FindAsync([insulinId], ct)
            ?? throw new KeyNotFoundException($"PatientInsulin {insulinId} not found");

        // Determine which roles need to have their primary cleared
        var rolesToClear = target.Role switch
        {
            "Bolus" => new[] { "Bolus", "Both" },
            "Basal" => new[] { "Basal", "Both" },
            "Both" => new[] { "Bolus", "Basal", "Both" },
            _ => new[] { target.Role }
        };

        // Clear IsPrimary on all other insulins that share the same role scope
        var conflicting = await ctx.PatientInsulins
            .Where(e => e.Id != insulinId && e.IsPrimary && rolesToClear.Contains(e.Role))
            .ToListAsync(ct);

        foreach (var entity in conflicting)
        {
            entity.IsPrimary = false;
            entity.SysUpdatedAt = DateTime.UtcNow;
        }

        target.IsPrimary = true;
        target.SysUpdatedAt = DateTime.UtcNow;

        await ctx.SaveChangesAsync(ct);
    }
}
