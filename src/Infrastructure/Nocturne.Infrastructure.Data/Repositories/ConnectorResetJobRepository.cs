using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.Connectors;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IConnectorResetJobRepository"/> over <see cref="NocturneDbContext"/>, one platform
/// context per call, saving each write.
/// </summary>
/// <remarks>Registered transient, like the factory it holds.</remarks>
public class ConnectorResetJobRepository : IConnectorResetJobRepository
{
    private readonly INocturneDbContextFactory _contexts;

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    public ConnectorResetJobRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <inheritdoc/>
    public virtual async Task<ConnectorResetJobRecord?> FindAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = await _contexts.CreatePlatformAsync(ct);
        var job = await db.ConnectorResetJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        return job is null ? null : new ConnectorResetJobRecord(
            job.Id,
            job.TenantId,
            job.TenantSlug,
            job.State,
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt,
            job.ErrorMessage,
            job.TotalConnectors,
            job.CompletedConnectors,
            job.ConnectorsJson);
    }

    /// <inheritdoc/>
    public virtual async Task SaveAsync(ConnectorResetJobRecord job, CancellationToken ct)
    {
        await using var db = await _contexts.CreatePlatformAsync(ct);
        var entity = await db.ConnectorResetJobs.FindAsync([job.Id], ct);
        if (entity is null)
        {
            entity = new ConnectorResetJobEntity { Id = job.Id };
            db.ConnectorResetJobs.Add(entity);
        }

        entity.TenantId = job.TenantId;
        entity.TenantSlug = job.TenantSlug;
        entity.State = job.State;
        entity.CreatedAt = job.CreatedAt;
        entity.StartedAt = job.StartedAt;
        entity.CompletedAt = job.CompletedAt;
        entity.ErrorMessage = job.ErrorMessage;
        entity.TotalConnectors = job.TotalConnectors;
        entity.CompletedConnectors = job.CompletedConnectors;
        entity.ConnectorsJson = job.ConnectorsJson;

        await db.SaveChangesAsync(ct);
    }
}
