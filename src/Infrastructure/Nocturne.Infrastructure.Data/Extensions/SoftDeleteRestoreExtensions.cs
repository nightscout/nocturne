using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The soft-delete restore quadrant — restore one, restore many, page the trash, count it — over the
/// narrowest constraint it actually needs. Reads and writes only
/// <see cref="ITenantScoped.TenantId"/>, <see cref="ISoftDeletable.DeletedAt"/> and the key, so the
/// span-shaped and timestamp-less types that <see cref="IV4TimeSeriesEntity"/> keeps off
/// <see cref="Repositories.V4.V4RepositoryBase{TModel,TEntity}"/> share the base's implementation
/// rather than retyping it.
/// </summary>
/// <remarks>
/// Parameterless <c>IgnoreQueryFilters()</c> drops <see cref="NocturneDbContext.TenantFilterKey"/>
/// along with the soft-delete filter, so the tenant predicate is re-applied by hand — without it a
/// restore would reach across tenants. The key is read through <see cref="EF.Property{TProperty}"/>
/// because <c>PatientDeviceEntity</c> and <c>PatientInsulinEntity</c> declare it on no interface.
/// </remarks>
public static class SoftDeleteRestoreExtensions
{
    private const string IdProperty = "Id";

    /// <summary>
    /// Clears <see cref="ISoftDeletable.DeletedAt"/> on this tenant's soft-deleted row with the given
    /// key and returns the tracked entity.
    /// </summary>
    /// <param name="ctx">Tenant-pinned context.</param>
    /// <param name="id">Key of the soft-deleted row.</param>
    /// <param name="recordType">Domain type name, for the not-found message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">No soft-deleted row with that key in this tenant.</exception>
    public static async Task<TEntity> RestoreDeletedAsync<TEntity>(
        this NocturneDbContext ctx, Guid id, string recordType, CancellationToken ct = default)
        where TEntity : class, ITenantScoped, ISoftDeletable
    {
        var entity = await ctx.DeletedRows<TEntity>()
            .Where(e => EF.Property<Guid>(e, IdProperty) == id)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Soft-deleted {recordType} {id} not found");

        entity.DeletedAt = null;
        await ctx.SaveChangesAsync(ct);
        return entity;
    }

    /// <summary>
    /// Clears <see cref="ISoftDeletable.DeletedAt"/> on every soft-deleted row of this tenant whose key
    /// is in <paramref name="ids"/>, and returns those rows. Keys that are unknown or already live are
    /// silently skipped.
    /// </summary>
    public static async Task<List<TEntity>> RestoreDeletedAsync<TEntity>(
        this NocturneDbContext ctx, IEnumerable<Guid> ids, CancellationToken ct = default)
        where TEntity : class, ITenantScoped, ISoftDeletable
    {
        var idSet = ids.ToHashSet();
        var entities = await ctx.DeletedRows<TEntity>()
            .Where(e => idSet.Contains(EF.Property<Guid>(e, IdProperty)))
            .ToListAsync(ct);

        foreach (var entity in entities)
            entity.DeletedAt = null;

        await ctx.SaveChangesAsync(ct);
        return entities;
    }

    /// <summary>Pages this tenant's soft-deleted rows, newest deletion first.</summary>
    public static Task<List<TEntity>> GetDeletedAsync<TEntity>(
        this NocturneDbContext ctx, int limit, int offset, CancellationToken ct = default)
        where TEntity : class, ITenantScoped, ISoftDeletable
        => ctx.DeletedRows<TEntity>()
            .OrderByDescending(e => e.DeletedAt)
            .Skip(offset).Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);

    /// <summary>Counts this tenant's soft-deleted rows.</summary>
    public static Task<int> CountDeletedAsync<TEntity>(
        this NocturneDbContext ctx, CancellationToken ct = default)
        where TEntity : class, ITenantScoped, ISoftDeletable
        => ctx.DeletedRows<TEntity>().CountAsync(ct);

    private static IQueryable<TEntity> DeletedRows<TEntity>(this NocturneDbContext ctx)
        where TEntity : class, ITenantScoped, ISoftDeletable
        => ctx.Set<TEntity>().IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null);
}
