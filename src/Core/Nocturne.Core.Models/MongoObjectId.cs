using System.Security.Cryptography;
using System.Text;

namespace Nocturne.Core.Models;

/// <summary>
/// Helpers for presenting record identifiers as 24-character hex MongoDB ObjectIds on the
/// legacy V1/V3 API surface. AAPS validates every record id with a strict <c>[0-9a-f]{24}</c>
/// check and, on failure, falls back to parsing the id as a <c>Long</c> — a Nocturne UUID
/// (36 chars) satisfies neither and crashes the sync with <c>NumberFormatException</c>.
///
/// <para>The conversion is deterministic and reversible enough to round-trip: a UUID maps to
/// the first 24 hex chars of its canonical form, which <see cref="TryGetGuidPrefixRange"/>
/// turns back into a uuid range for lookup. A real incoming ObjectId is preserved verbatim.</para>
/// </summary>
public static class MongoObjectId
{
    /// <summary>The 24-character lowercase-hex shape AAPS's <c>isObjectId()</c> accepts.</summary>
    public static bool IsObjectId(string? value)
    {
        if (value is null || value.Length != 24)
            return false;

        foreach (var c in value)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHex)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The first 24 hex chars of a UUID's canonical (dashless) form. Reversible to a uuid range
    /// via <see cref="TryGetGuidPrefixRange"/>.
    /// </summary>
    public static string FromGuid(Guid id) => id.ToString("N").Substring(0, 24);

    /// <summary>
    /// Coerces any identifier into a 24-hex ObjectId for the wire:
    /// an existing ObjectId passes through, a UUID becomes its 24-hex prefix, and anything else
    /// (e.g. a synthetic or non-UUID legacy id) is hashed deterministically to 24 hex.
    /// Null/empty is returned unchanged so callers can keep their own null handling.
    /// </summary>
    public static string? Coerce(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return id;

        if (IsObjectId(id))
            return id;

        if (Guid.TryParse(id, out var guid))
            return FromGuid(guid);

        // Non-UUID, non-ObjectId legacy strings: hash to a stable 24-hex value so the wire is
        // always AAPS-safe. These do not round-trip to a record (see the LegacyId lookup path).
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexStringLower(hash.AsSpan(0, 12));
    }

    /// <summary>
    /// Turns a 24-hex ObjectId derived from a UUID back into the uuid range that contains the
    /// source record: <c>[objectId + "00000000", objectId + "ffffffff"]</c>. Postgres orders
    /// <c>uuid</c> byte-wise (= hex-prefix order), so a range query selects the source UUID.
    /// Returns false when the input is not a valid ObjectId.
    /// </summary>
    public static bool TryGetGuidPrefixRange(string? objectId, out Guid low, out Guid high)
    {
        low = default;
        high = default;
        if (!IsObjectId(objectId))
            return false;

        low = Guid.ParseExact(objectId + "00000000", "N");
        high = Guid.ParseExact(objectId + "ffffffff", "N");
        return true;
    }
}
