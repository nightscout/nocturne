using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;

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
        var entities = await ctx.PatientDevices
            .AsNoTracking()
            .OrderByDescending(e => e.IsCurrent)
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
            .OrderByDescending(e => e.StartDate)
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
            .OrderByDescending(e => e.StartDate)
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
    public async Task<PatientDevice> CreateAsync(PatientDevice model, CancellationToken ct = default)
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
    public async Task<PatientDevice> UpdateAsync(Guid id, PatientDevice model, CancellationToken ct = default)
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
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientDevices.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"PatientDevice {id} not found");

        entity.DeletedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<PatientDevice> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientDevices.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.Id == id && e.DeletedAt != null)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Soft-deleted PatientDevice {id} not found");
        entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return PatientDeviceMapper.ToDomainModel(entity);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PatientDevice>> BulkRestoreAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var idSet = ids.ToHashSet();
        var entities = await ctx.PatientDevices.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && idSet.Contains(e.Id) && e.DeletedAt != null)
            .ToListAsync(ct);
        foreach (var entity in entities)
            entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return entities.Select(PatientDeviceMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PatientDevice>> GetDeletedAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entities = await ctx.PatientDevices.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt)
            .Skip(offset).Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
        return entities.Select(PatientDeviceMapper.ToDomainModel);
    }

    /// <inheritdoc />
    public async Task<int> CountDeletedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        return await ctx.PatientDevices.IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null)
            .CountAsync(ct);
    }
}
