using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// Repository for managing the single patient record in the database.
/// </summary>
public class PatientRecordRepository : IPatientRecordRepository
{
    private readonly INocturneDbContextFactory _contextFactory;
    private readonly ILogger<PatientRecordRepository> _logger;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="PatientRecordRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="time">The clock that stamps a new record.</param>
    public PatientRecordRepository(
        INocturneDbContextFactory contextFactory,
        ILogger<PatientRecordRepository> logger,
        TimeProvider time)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _time = time;
    }

    /// <summary>
    /// Gets the patient record.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The patient record, or null if none exists.</returns>
    public async Task<PatientRecord?> GetAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : PatientRecordMapper.ToDomainModel(entity);
    }

    /// <summary>
    /// Gets the existing patient record or creates a new one if it doesn't exist.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The patient record.</returns>
    public async Task<PatientRecord> GetOrCreateAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientRecords.FirstOrDefaultAsync(ct);

        if (entity is not null)
            return PatientRecordMapper.ToDomainModel(entity);

        _logger.LogInformation("No patient record found, creating empty record");

        var now = _time.GetUtcNow().UtcDateTime;
        var model = new PatientRecord
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = now,
            ModifiedAt = now,
        };

        var newEntity = PatientRecordMapper.ToEntity(model);
        ctx.PatientRecords.Add(newEntity);
        await ctx.SaveChangesAsync(ct);

        return PatientRecordMapper.ToDomainModel(newEntity);
    }

    /// <summary>
    /// Updates the patient record.
    /// </summary>
    /// <returns>The updated patient record.</returns>
    public async Task<PatientRecord> UpdateAsync(PatientRecord model, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(ct);
        var entity = await ctx.PatientRecords.FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Patient record not found");

        PatientRecordMapper.UpdateEntity(entity, model);
        await ctx.SaveChangesAsync(ct);

        return PatientRecordMapper.ToDomainModel(entity);
    }
}
