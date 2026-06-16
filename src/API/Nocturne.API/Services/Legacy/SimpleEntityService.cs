using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Legacy;

/// <summary>
/// Abstract base for simple entity CRUD services that use <see cref="NocturneDbContext"/> directly
/// with document processing and SignalR broadcasting via <see cref="ISignalRBroadcastService"/>.
/// Eliminates boilerplate for services like <see cref="HeartRateService"/> and <see cref="StepCountService"/>
/// that follow the same get/create/update/delete + broadcast pattern.
/// </summary>
/// <typeparam name="TDomain">The domain model type (must implement <see cref="IProcessableDocument"/>).</typeparam>
/// <typeparam name="TEntity">The EF Core entity type stored in the database.</typeparam>
/// <seealso cref="ISignalRBroadcastService"/>
/// <seealso cref="IDocumentProcessingService"/>
public abstract class SimpleEntityService<TDomain, TEntity>
    where TDomain : class, IProcessableDocument
    where TEntity : class
{
    protected readonly NocturneDbContext DbContext;
    protected readonly IDocumentProcessingService DocumentProcessingService;
    protected readonly ISignalRBroadcastService SignalRBroadcastService;
    protected readonly ILogger Logger;

    /// <summary>
    /// Initializes a new instance of <see cref="SimpleEntityService{TDomain,TEntity}"/>.
    /// </summary>
    /// <param name="dbContext">The EF Core database context for entity access.</param>
    /// <param name="documentProcessingService">Service for HTML sanitization and document preprocessing.</param>
    /// <param name="signalRBroadcastService">Service for broadcasting storage events to connected clients.</param>
    /// <param name="logger">The logger instance.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is <see langword="null"/>.</exception>
    protected SimpleEntityService(
        NocturneDbContext dbContext,
        IDocumentProcessingService documentProcessingService,
        ISignalRBroadcastService signalRBroadcastService,
        ILogger logger
    )
    {
        DbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        DocumentProcessingService =
            documentProcessingService
            ?? throw new ArgumentNullException(nameof(documentProcessingService));
        SignalRBroadcastService =
            signalRBroadcastService
            ?? throw new ArgumentNullException(nameof(signalRBroadcastService));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The <see cref="Microsoft.EntityFrameworkCore.DbSet{TEntity}"/> for this entity type.</summary>
    protected abstract DbSet<TEntity> EntitySet { get; }

    /// <summary>The collection name used for <see cref="ISignalRBroadcastService"/> broadcasts (e.g., <c>"heartrate"</c>).</summary>
    protected abstract string CollectionName { get; }

    /// <summary>The entity type name used in log messages (e.g., <c>"heart rate"</c>).</summary>
    protected abstract string EntityTypeName { get; }

    /// <summary>Maps a database entity to its corresponding domain model.</summary>
    /// <param name="entity">The entity to map.</param>
    /// <returns>The corresponding <typeparamref name="TDomain"/> instance.</returns>
    protected abstract TDomain ToDomainModel(TEntity entity);

    /// <summary>Maps a domain model to its corresponding database entity for insertion.</summary>
    /// <param name="model">The domain model to map.</param>
    /// <returns>The corresponding <typeparamref name="TEntity"/> instance.</returns>
    protected abstract TEntity ToEntity(TDomain model);

    /// <summary>Applies updated values from a domain model onto an existing tracked entity.</summary>
    /// <param name="entity">The tracked entity to update in place.</param>
    /// <param name="model">The domain model carrying the new values.</param>
    protected abstract void UpdateEntity(TEntity entity, TDomain model);

    /// <summary>Returns an ordered queryable sorted by the entity's primary timestamp (descending).</summary>
    /// <param name="query">The base queryable to order.</param>
    /// <returns>An <see cref="IOrderedQueryable{TEntity}"/> sorted by timestamp descending.</returns>
    protected abstract IOrderedQueryable<TEntity> OrderByTimestamp(IQueryable<TEntity> query);

    /// <summary>Finds a single entity by its string ID, returning <see langword="null"/> if not found.</summary>
    /// <param name="id">The string ID to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching entity, or <see langword="null"/>.</returns>
    protected abstract Task<TEntity?> FindByIdAsync(
        string id,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Retrieves a page of entities ordered by timestamp descending.
    /// </summary>
    /// <param name="count">Maximum number of records to return.</param>
    /// <param name="skip">Number of records to skip for pagination.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An enumerable of <typeparamref name="TDomain"/> domain models.</returns>
    protected async Task<IEnumerable<TDomain>> GetAllAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            Logger.LogDebug(
                "Getting {EntityType} records with count: {Count}, skip: {Skip}",
                EntityTypeName,
                count,
                skip
            );

            var entities = await OrderByTimestamp(EntitySet)
                .Skip(skip)
                .Take(count)
                .ToListAsync(cancellationToken);

            return entities.Select(ToDomainModel);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting {EntityType} records", EntityTypeName);
            throw;
        }
    }

    /// <summary>
    /// Retrieves a single entity by its ID.
    /// </summary>
    /// <param name="id">The entity ID to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The domain model, or <see langword="null"/> if not found.</returns>
    protected async Task<TDomain?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            Logger.LogDebug("Getting {EntityType} record by ID: {Id}", EntityTypeName, id);

            var entity = await FindByIdAsync(id, cancellationToken);
            return entity is null ? null : ToDomainModel(entity);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Error getting {EntityType} record by ID: {Id}",
                EntityTypeName,
                id
            );
            throw;
        }
    }

    /// <summary>
    /// Processes, saves, and broadcasts the creation of multiple domain records.
    /// </summary>
    /// <remarks>
    /// Each item is passed through <see cref="IDocumentProcessingService.ProcessDocuments{T}"/> for
    /// sanitization before insertion. After saving, a <c>create</c> event is broadcast via
    /// <see cref="ISignalRBroadcastService.BroadcastStorageCreateAsync"/>.
    /// </remarks>
    /// <param name="items">The domain models to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created domain models with server-assigned IDs.</returns>
    protected async Task<IEnumerable<TDomain>> CreateManyAsync(
        IEnumerable<TDomain> items,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var itemList = items.ToList();
            Logger.LogDebug(
                "Creating {Count} {EntityType} records",
                itemList.Count,
                EntityTypeName
            );

            var processed = DocumentProcessingService.ProcessDocuments(itemList).ToList();
            var entities = processed.Select(ToEntity).ToList();

            // Intra-batch dedup on the sync key (keep the last occurrence) so a
            // batch that repeats a (DataSource, SyncIdentifier) doesn't try to
            // insert two rows that collide on the partial unique index.
            entities = entities
                .Select((entity, index) => (entity, index))
                .GroupBy(item => item.entity is ISyncDedupable dedup
                        && !string.IsNullOrEmpty(dedup.DataSource)
                        && !string.IsNullOrEmpty(dedup.SyncIdentifier)
                    ? $"sync|{dedup.DataSource}|{dedup.SyncIdentifier}"
                    : $"idx|{item.index}")
                .Select(group => group.Last().entity)
                .ToList();

            // Sync-identifier upsert: an entity whose (DataSource, SyncIdentifier)
            // matches an existing non-deleted row (the global query filter scopes
            // the lookup to this tenant) updates it in place; the rest are inserted.
            // This makes repeated uploads of the same measurement idempotent.
            //
            // The existing rows for the whole batch are pre-loaded in ONE query
            // keyed on the sync identifiers present (the
            // (tenant_id, data_source, sync_identifier) index keeps it cheap) —
            // one round trip instead of one per record, which matters for the
            // historical backfill on a first permission grant.
            var syncIdentifiers = entities
                .Select(e => (e as ISyncDedupable)?.SyncIdentifier)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var existingByKey = new Dictionary<string, TEntity>(StringComparer.Ordinal);
            if (syncIdentifiers.Count > 0)
            {
                var candidates = await EntitySet
                    .Where(e => syncIdentifiers.Contains(
                        EF.Property<string>(e, nameof(ISyncDedupable.SyncIdentifier))))
                    .ToListAsync(cancellationToken);
                foreach (var candidate in candidates)
                {
                    if (candidate is ISyncDedupable d
                        && !string.IsNullOrEmpty(d.DataSource)
                        && !string.IsNullOrEmpty(d.SyncIdentifier))
                    {
                        existingByKey[$"{d.DataSource}|{d.SyncIdentifier}"] = candidate;
                    }
                }
            }

            var resultEntities = new List<TEntity>(entities.Count);
            var toInsert = new List<TEntity>();
            foreach (var entity in entities)
            {
                if (entity is ISyncDedupable dedup
                    && !string.IsNullOrEmpty(dedup.DataSource)
                    && !string.IsNullOrEmpty(dedup.SyncIdentifier)
                    && existingByKey.TryGetValue($"{dedup.DataSource}|{dedup.SyncIdentifier}", out var existing))
                {
                    UpdateEntity(existing, ToDomainModel(entity));
                    resultEntities.Add(existing);
                }
                else
                {
                    toInsert.Add(entity);
                    resultEntities.Add(entity);
                }
            }

            if (toInsert.Count > 0)
                await EntitySet.AddRangeAsync(toInsert, cancellationToken);
            await DbContext.SaveChangesAsync(cancellationToken);

            var result = resultEntities.Select(ToDomainModel).ToList();

            await SignalRBroadcastService.BroadcastStorageCreateAsync(
                CollectionName,
                new { collection = CollectionName, data = result, count = result.Count }
            );

            Logger.LogDebug(
                "Successfully created {Count} {EntityType} records",
                result.Count,
                EntityTypeName
            );
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error creating {EntityType} records", EntityTypeName);
            throw;
        }
    }

    /// <summary>
    /// Updates an existing entity and broadcasts the change.
    /// </summary>
    /// <param name="id">The ID of the entity to update.</param>
    /// <param name="item">The domain model carrying the updated values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated domain model, or <see langword="null"/> if no entity with the given <paramref name="id"/> exists.</returns>
    protected async Task<TDomain?> UpdateOneAsync(
        string id,
        TDomain item,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            Logger.LogDebug("Updating {EntityType} record with ID: {Id}", EntityTypeName, id);

            var entity = await FindByIdAsync(id, cancellationToken);
            if (entity is null)
            {
                Logger.LogDebug(
                    "{EntityType} record with ID {Id} not found for update",
                    EntityTypeName,
                    id
                );
                return null;
            }

            UpdateEntity(entity, item);
            await DbContext.SaveChangesAsync(cancellationToken);

            var result = ToDomainModel(entity);

            await SignalRBroadcastService.BroadcastStorageUpdateAsync(
                CollectionName,
                new { collection = CollectionName, data = result, id }
            );

            Logger.LogDebug(
                "Successfully updated {EntityType} record with ID: {Id}",
                EntityTypeName,
                id
            );
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Error updating {EntityType} record with ID: {Id}",
                EntityTypeName,
                id
            );
            throw;
        }
    }

    /// <summary>
    /// Deletes an entity by ID and broadcasts the deletion.
    /// </summary>
    /// <param name="id">The ID of the entity to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> if the entity was found and deleted; <see langword="false"/> if not found.</returns>
    protected async Task<bool> DeleteOneAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            Logger.LogDebug("Deleting {EntityType} record with ID: {Id}", EntityTypeName, id);

            var entity = await FindByIdAsync(id, cancellationToken);
            if (entity is null)
            {
                Logger.LogDebug(
                    "{EntityType} record with ID {Id} not found for deletion",
                    EntityTypeName,
                    id
                );
                return false;
            }

            if (entity is ISoftDeletable softDeletable)
                softDeletable.DeletedAt = DateTime.UtcNow;
            else
                EntitySet.Remove(entity);
            await DbContext.SaveChangesAsync(cancellationToken);

            await SignalRBroadcastService.BroadcastStorageDeleteAsync(
                CollectionName,
                new { collection = CollectionName, id }
            );

            Logger.LogDebug(
                "Successfully deleted {EntityType} record with ID: {Id}",
                EntityTypeName,
                id
            );
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Error deleting {EntityType} record with ID: {Id}",
                EntityTypeName,
                id
            );
            throw;
        }
    }
}
