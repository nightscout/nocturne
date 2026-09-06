using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing patient device records (diabetes technology used by the patient) in the database.
/// </summary>
public class PatientDeviceRepository : IPatientDeviceRepository
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly ILogger<PatientDeviceRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PatientDeviceRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="logger">The logger instance.</param>
    public PatientDeviceRepository(
        ITenantDbContextFactory contextFactory,
        ILogger<PatientDeviceRepository> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets all patient device records.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of patient devices.</returns>
    public async Task<IEnumerable<PatientDevice>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        // Rank leads the sort so the management list's order IS the priority order — otherwise
        // drag/arrow reordering (which persists rank = list index) would snap back on refresh.
        // Unranked devices fall back to the previous current-then-recent ordering.
        var entities = await ctx.PatientDevices
            .AsNoTracking()
            .OrderBy(e => e.Rank == null)
            .ThenBy(e => e.Rank)
            .ThenByDescending(e => e.IsCurrent)
            .ThenByDescending(e => e.StartDate)
            .ToListAsync(ct);

        return entities.Select(PatientDeviceMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets all currently used patient devices.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of current patient devices.</returns>
    public async Task<IEnumerable<PatientDevice>> GetCurrentAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.PatientDevices
            .AsNoTracking()
            .Where(e => e.IsCurrent)
            .OrderBy(e => e.Rank == null)
            .ThenBy(e => e.Rank)
            .ThenByDescending(e => e.StartDate)
            .ToListAsync(ct);

        return entities.Select(PatientDeviceMapper.ToDomainModel);
    }

    /// <summary>
    /// Gets patient devices that were active within a date range.
    /// </summary>
    /// <param name="from">The start of the date range.</param>
    /// <param name="to">The end of the date range.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of patient devices active in the range.</returns>
    public async Task<IEnumerable<PatientDevice>> GetByDateRangeAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var fromDate = DateOnly.FromDateTime(from);
        var toDate = DateOnly.FromDateTime(to);

        var entities = await ctx.PatientDevices
            .AsNoTracking()
            .Where(e =>
                (e.StartDate == null || e.StartDate <= toDate) &&
                (e.EndDate == null || e.EndDate >= fromDate))
            .OrderBy(e => e.Rank == null)
            .ThenBy(e => e.Rank)
            .ThenByDescending(e => e.StartDate)
            .ToListAsync(ct);

        return entities.Select(PatientDeviceMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PatientDevice>> GetByDeviceIdAsync(Guid deviceId, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.PatientDevices
            .AsNoTracking()
            .Where(e => e.DeviceId == deviceId)
            .OrderByDescending(e => e.StartDate)
            .ToListAsync(ct);

        return entities.Select(PatientDeviceMapper.ToDomainModel).ToList();
    }

    /// <summary>
    /// Gets a patient device record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The patient device, or null if not found.</returns>
    public async Task<PatientDevice?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientDevices.FindAsync([id], ct);
        return entity is null ? null : PatientDeviceMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Creates a new patient device record.
    /// </summary>
    /// <param name="model">The patient device to create.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The created patient device record.</returns>
    public async Task<PatientDevice> CreateAsync(PatientDevice model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = PatientDeviceMapper.ToEntity(model);
        ctx.PatientDevices.Add(entity);
        await ctx.SaveChangesAsync(ct);
        return PatientDeviceMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Updates an existing patient device record.
    /// </summary>
    /// <param name="id">The unique identifier of the record to update.</param>
    /// <param name="model">The updated record data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The updated patient device record.</returns>
    public async Task<PatientDevice> UpdateAsync(Guid id, PatientDevice model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientDevices.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"PatientDevice {id} not found");

        PatientDeviceMapper.UpdateEntity(entity, model);
        await ctx.SaveChangesAsync(ct);
        return PatientDeviceMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a patient device record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientDevices.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"PatientDevice {id} not found");

        entity.DeletedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PatientDevice>> ReorderAsync(
        IReadOnlyList<(Guid Id, int Rank)> ranks, WriteOrigin origin, CancellationToken ct = default)
    {
        if (ranks.Count == 0) return [];

        await using var ctx = await _contextFactory.CreateAsync(ct);
        var rankById = ranks.ToDictionary(r => r.Id, r => r.Rank);
        var ids = rankById.Keys.ToList();
        var entities = await ctx.PatientDevices
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(ct);

        var changed = new List<PatientDevice>();
        foreach (var entity in entities)
        {
            var newRank = rankById[entity.Id];
            if (entity.Rank == newRank) continue;
            entity.Rank = newRank;
            changed.Add(PatientDeviceMapper.ToDomainModel(entity));
        }

        if (changed.Count > 0)
            await ctx.SaveChangesAsync(ct);
        return changed;
    }

    /// <inheritdoc />
    public async Task<PatientDevice> RestoreAsync(Guid id, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return PatientDeviceMapper.ToDomainModel(await ctx.RestoreDeletedAsync<PatientDeviceEntity>(id, nameof(PatientDevice), ct));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PatientDevice>> BulkRestoreAsync(IEnumerable<Guid> ids, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return (await ctx.RestoreDeletedAsync<PatientDeviceEntity>(ids, ct)).Select(PatientDeviceMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PatientDevice>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return (await ctx.GetDeletedAsync<PatientDeviceEntity>(limit, offset, ct)).Select(PatientDeviceMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.CountDeletedAsync<PatientDeviceEntity>(ct);
    }
}
