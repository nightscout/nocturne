namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Marks an entity whose creates should be deduplicated by an upstream sync key.
/// When both <see cref="DataSource"/> and <see cref="SyncIdentifier"/> are present,
/// a create that matches an existing (non-deleted) row for the same tenant updates
/// that row in place rather than inserting a duplicate — making repeated uploads of
/// the same measurement idempotent.
/// </summary>
/// <remarks>
/// Backed by a partial unique index on <c>(tenant_id, data_source, sync_identifier)</c>
/// filtered to <c>sync_identifier IS NOT NULL AND deleted_at IS NULL</c>, mirroring the
/// V4 treatment entities. <c>SimpleEntityService</c> performs the upsert for any entity
/// implementing this interface.
/// </remarks>
public interface ISyncDedupable
{
    /// <summary>Origin data source identifier (the first half of the dedup key).</summary>
    string? DataSource { get; set; }

    /// <summary>Stable per-source identifier for the measurement (the second half of the dedup key).</summary>
    string? SyncIdentifier { get; set; }
}
