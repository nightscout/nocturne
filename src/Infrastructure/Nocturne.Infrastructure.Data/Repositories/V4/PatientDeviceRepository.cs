using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing patient device records (diabetes technology used by the patient) in the database.
/// </summary>
public class PatientDeviceRepository : IPatientDeviceRepository
{
    private readonly NocturneDbContext _context;
    private readonly ILogger<PatientDeviceRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PatientDeviceRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="logger">The logger instance.</param>
    public PatientDeviceRepository(
        NocturneDbContext context,
        ILogger<PatientDeviceRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Gets all patient device records.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A collection of patient devices.</returns>
    public async Task<IEnumerable<PatientDevice>> GetAllAsync(CancellationToken ct = default)
    {
        var entities = await _context.PatientDevices
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
        var entities = await _context.PatientDevices
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
        var fromDate = DateOnly.FromDateTime(from);
        var toDate = DateOnly.FromDateTime(to);

        var entities = await _context.PatientDevices
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
        var entities = await _context.PatientDevices
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
        var entity = await _context.PatientDevices.FindAsync([id], ct);
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
        var entity = PatientDeviceMapper.ToEntity(model);
        _context.PatientDevices.Add(entity);
        await _context.SaveChangesAsync(ct);
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
        var entity = await _context.PatientDevices.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"PatientDevice {id} not found");

        PatientDeviceMapper.UpdateEntity(entity, model);
        await _context.SaveChangesAsync(ct);
        return PatientDeviceMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Deletes a patient device record by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.PatientDevices.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"PatientDevice {id} not found");

        _context.PatientDevices.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }
}
