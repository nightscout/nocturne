namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// The SQL that finds a uuid-shaped legacy id by the 24-hex prefix it goes out on the wire under
/// (<c>MongoObjectId.FromGuid</c>), for
/// <see cref="V4RepositoryBase{TModel,TEntity}.ResolveUuidLegacyIdsAsync"/>. A query must repeat
/// <see cref="Predicate"/> and <see cref="Key"/> exactly for Postgres to answer it from the partial
/// expression index <c>AddWriteBackIdentityTracking</c> builds on each table it serves.
/// </summary>
/// <remarks>
/// The predicate admits every spelling <see cref="Guid.TryParse(string, out Guid)"/> reads as a
/// uuid short of the hex-struct form: any case, dashed or not, braced or parenthesised. The key
/// strips everything but hex digits and lowercases, which is the canonical dashless form for all of
/// them, so its first 24 characters are the prefix. The caller still parses each hit.
/// </remarks>
internal static class UuidLegacyIdPrefix
{
    public const string Predicate =
        "legacy_id ~* '^[{(]?[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}[})]?$'";

    public const string Key = "left(regexp_replace(lower(legacy_id), '[^0-9a-f]', '', 'g'), 24)";
}
