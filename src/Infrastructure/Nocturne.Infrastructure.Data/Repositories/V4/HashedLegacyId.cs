namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// The SQL that finds a legacy id by the 24-hex hash <c>MongoObjectId.Coerce</c> gives an id that is
/// neither an ObjectId nor a uuid, for
/// <see cref="V4RepositoryBase{TModel,TEntity}.ResolveHashedLegacyIdsAsync"/>. A query must repeat
/// <see cref="Predicate"/> and <see cref="Key"/> exactly for Postgres to answer it from the partial
/// expression index <c>AddWriteBackIdentityTracking</c> builds on each treatment table.
/// </summary>
/// <remarks>
/// <c>legacy_id_wire_hash</c> is the migration's immutable wrapper over
/// <c>left(encode(sha256(convert_to(legacy_id, 'UTF8')), 'hex'), 24)</c>: <c>convert_to</c> is only
/// stable, which an index expression may not call directly. The predicate leaves out ObjectIds,
/// which go on the wire as they are; uuids pass it, and the caller drops them on the
/// <c>Coerce</c> check each hit gets.
/// </remarks>
internal static class HashedLegacyId
{
    public const string Predicate = "legacy_id !~ '^[0-9a-f]{24}$'";

    public const string Key = "public.legacy_id_wire_hash(legacy_id)";
}
