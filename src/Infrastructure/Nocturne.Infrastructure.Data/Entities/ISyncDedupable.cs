namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Marks an entity keyed by an upstream sync key: the <see cref="ISourcedEntity.DataSource"/> the row
/// came from plus that source's own <see cref="SyncIdentifier"/> for the record. When both are present
/// the pair names the record across re-uploads, so a create matching an existing row for the same
/// tenant updates that row in place rather than inserting a duplicate — making repeated uploads of the
/// same measurement idempotent — and a delete can be issued against the upstream key alone, without
/// knowing the Nocturne id.
/// </summary>
/// <remarks>
/// The types that upsert on the key back it with a partial unique index on
/// <c>(tenant_id, data_source, sync_identifier)</c> filtered to
/// <c>sync_identifier IS NOT NULL AND deleted_at IS NULL</c>;
/// <see cref="NocturneDbContext.SyncDedupedEntities"/> is the authoritative list. <c>SimpleEntityService</c>
/// and <see cref="Repositories.V4.SyncUpsertRepositoryBase{TModel,TEntity}"/> perform the upsert;
/// <see cref="Repositories.V4.SyncKeyedRepositoryBase{TModel,TEntity}"/> the keyed lookup and delete.
/// </remarks>
public interface ISyncDedupable : ISourcedEntity
{
    /// <summary>Stable per-source identifier for the measurement (the second half of the dedup key).</summary>
    string? SyncIdentifier { get; set; }
}
