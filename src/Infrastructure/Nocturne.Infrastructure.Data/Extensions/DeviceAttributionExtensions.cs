using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The unattributed-backlog read and the batch back-stamp write, shared by every repository whose
/// records carry a <c>patient_device_id</c>.
/// </summary>
public static class DeviceAttributionExtensions
{
    /// <summary>
    /// Unattributed rows within the time window, newest first, capped at <paramref name="limit"/>.
    /// Span-shaped types that key on a start timestamp (and so are not
    /// <see cref="IV4TimeSeriesEntity"/>) window with <see cref="UnattributedNewestFirstAsync{TEntity}"/>.
    /// </summary>
    public static Task<List<TEntity>> GetUnattributedAsync<TEntity>(
        this NocturneDbContext ctx,
        DateTime? from,
        DateTime? to,
        int limit,
        CancellationToken ct,
        Expression<Func<TEntity, bool>>? filter = null)
        where TEntity : class, IV4TimeSeriesEntity, IDeviceAttributedEntity
    {
        var query = ctx.Set<TEntity>().AsNoTracking();
        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
        if (filter is not null) query = query.Where(filter);
        return query.UnattributedNewestFirstAsync(e => e.Timestamp, limit, ct);
    }

    /// <summary>
    /// Narrows an already-windowed query to its unattributed rows and takes the newest
    /// <paramref name="limit"/> by <paramref name="orderBy"/>.
    /// </summary>
    public static Task<List<TEntity>> UnattributedNewestFirstAsync<TEntity>(
        this IQueryable<TEntity> windowed,
        Expression<Func<TEntity, DateTime>> orderBy,
        int limit,
        CancellationToken ct)
        where TEntity : class, IDeviceAttributedEntity
        => windowed
            .Where(e => e.PatientDeviceId == null)
            .OrderByDescending(orderBy)
            .Take(limit)
            .ToListAsync(ct);

    /// <summary>
    /// Sets <c>patient_device_id</c> on the given record ids in one batch, ignoring ids absent for
    /// this tenant. Returns the number of rows updated.
    /// </summary>
    public static async Task<int> SetPatientDeviceIdsAsync<TEntity>(
        this NocturneDbContext ctx,
        IReadOnlyDictionary<Guid, Guid> patientDeviceIdByRecordId,
        CancellationToken ct)
        where TEntity : class, IDeviceAttributedEntity
    {
        if (patientDeviceIdByRecordId.Count == 0) return 0;

        var ids = patientDeviceIdByRecordId.Keys.ToList();
        var entities = await ctx.Set<TEntity>().Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        foreach (var entity in entities)
            entity.PatientDeviceId = patientDeviceIdByRecordId[entity.Id];

        return entities.Count > 0 ? await ctx.SaveChangesAsync(ct) : 0;
    }
}
