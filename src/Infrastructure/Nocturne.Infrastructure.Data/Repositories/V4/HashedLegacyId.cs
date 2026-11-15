namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// The SQL that finds a legacy id by the 24-hex hash <c>MongoObjectId.Coerce</c> gives an id that is
/// not an ObjectId. A query must repeat <see cref="Predicate"/> and <see cref="Key"/> exactly for
/// Postgres to answer it from the partial expression index <c>AddWriteBackIdentity</c> builds on each
/// treatment table.
/// </summary>
/// <remarks>
/// <c>legacy_id_wire_hash</c> is the migration's immutable wrapper over
/// <c>left(encode(sha256(convert_to(legacy_id, 'UTF8')), 'hex'), 24)</c>: <c>convert_to</c> is only
/// stable, which an index expression may not call directly. The predicate leaves out ObjectIds, which
/// go on the wire as they are. It has no regex quantifier, whose braces <c>FromSqlRaw</c> would read as
/// a parameter placeholder.
/// </remarks>
internal static class HashedLegacyId
{
    public static string Predicate(string column) =>
        $"NOT (length({column}) = 24 AND {column} ~ '^[0-9a-f]+$')";

    public static string Key(string column) => $"public.legacy_id_wire_hash({column})";
}
