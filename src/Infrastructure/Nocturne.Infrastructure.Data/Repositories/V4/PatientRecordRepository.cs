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
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly ILogger<PatientRecordRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PatientRecordRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    /// <param name="logger">The logger instance.</param>
    public PatientRecordRepository(
        ITenantDbContextFactory contextFactory,
        ILogger<PatientRecordRepository> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
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

        var model = new PatientRecord
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };

        var newEntity = PatientRecordMapper.ToEntity(model);
        ctx.PatientRecords.Add(newEntity);
        await ctx.SaveChangesAsync(ct);

        return PatientRecordMapper.ToDomainModel(newEntity);
    }

    /// <summary>
    /// Updates the patient record.
    /// </summary>
    /// <param name="model">The updated patient record data.</param>
    /// <param name="ct">The cancellation token.</param>
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
