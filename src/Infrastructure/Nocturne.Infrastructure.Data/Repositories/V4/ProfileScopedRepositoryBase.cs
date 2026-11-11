using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> for the <see cref="IProfileScoped"/> siblings a
/// legacy profile document decomposes into, with the batch lookups of
/// <see cref="IProfileScopedRepository{TRecord}"/>.
/// </summary>
/// <typeparam name="TModel">The V4 domain record type.</typeparam>
/// <typeparam name="TEntity">The EF entity type backing <typeparamref name="TModel"/>.</typeparam>
public abstract class ProfileScopedRepositoryBase<TModel, TEntity> : V4RepositoryBase<TModel, TEntity>
    where TModel : class, IV4Record, IProfileScoped
    where TEntity : class, IProfileScopedEntity, IAuditable, ISystemTimestamped
{
    /// <inheritdoc />
    protected ProfileScopedRepositoryBase(
        INocturneDbContextFactory contextFactory,
        IAuditContext auditContext,
        ILogger logger,
        IV4RecordBroadcaster<TModel>? broadcaster = null)
        : base(contextFactory, auditContext, logger, broadcaster)
    {
    }

    /// <inheritdoc cref="IProfileScopedRepository{TRecord}.GetByProfileNameAsync" />
    public async Task<IEnumerable<TModel>> GetByProfileNameAsync(
        string profileName, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.Set<TEntity>()
            .AsNoTracking()
            .Where(e => e.ProfileName == profileName)
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    /// <inheritdoc cref="IProfileScopedRepository{TRecord}.GetActiveAtAsync" />
    public async Task<TModel?> GetActiveAtAsync(
        string profileName, DateTime timestamp, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.Set<TEntity>()
            .AsNoTracking()
            .Where(e => e.ProfileName == profileName && e.Timestamp <= timestamp)
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : ToDomain(entity);
    }

    /// <inheritdoc cref="IProfileScopedRepository{TRecord}.GetByCorrelationIdAsync" />
    public async Task<IEnumerable<TModel>> GetByCorrelationIdAsync(
        Guid correlationId, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.Set<TEntity>()
            .AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    /// <inheritdoc cref="IProfileScopedRepository{TRecord}.DeleteByLegacyIdPrefixAsync" />
    public async Task<int> DeleteByLegacyIdPrefixAsync(
        string prefix, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await ctx.AuditedSoftDeleteAsync(
            ctx.Set<TEntity>().Where(e => e.LegacyId != null && e.LegacyId.StartsWith(prefix)),
            AuditContext, $"legacy_id_prefix={prefix}", ct);
    }

    /// <inheritdoc cref="IProfileScopedRepository{TRecord}.GetByCorrelationIdsAsync" />
    public async Task<IEnumerable<TModel>> GetByCorrelationIdsAsync(
        IEnumerable<Guid> correlationIds, CancellationToken ct = default)
    {
        var ids = correlationIds.Distinct().ToArray();
        if (ids.Length == 0) return [];

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.Set<TEntity>()
            .AsNoTracking()
            .Where(e => e.CorrelationId != null && ids.Contains(e.CorrelationId.Value))
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    /// <inheritdoc cref="IProfileScopedRepository{TRecord}.GetNewestByProfileNamesAsync" />
    public async Task<IEnumerable<TModel>> GetNewestByProfileNamesAsync(
        IEnumerable<string> profileNames, CancellationToken ct = default)
    {
        var names = profileNames.Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return [];

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.Set<TEntity>()
            .AsNoTracking()
            .Where(e => names.Contains(e.ProfileName))
            .GroupBy(e => e.ProfileName)
            .Select(g => g.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id).First())
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    /// <inheritdoc cref="IProfileScopedRepository{TRecord}.GetByProfileNamesAsync" />
    public async Task<IEnumerable<TModel>> GetByProfileNamesAsync(
        IEnumerable<string> profileNames, CancellationToken ct = default)
    {
        var names = profileNames.Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return [];

        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entities = await ctx.Set<TEntity>()
            .AsNoTracking()
            .Where(e => names.Contains(e.ProfileName))
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }
}
