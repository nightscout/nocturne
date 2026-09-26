using System.Text.Json;

namespace Nocturne.Core.Models;

/// <summary>
/// Carries an uploader's own lowercase <c>id</c> (Trio's CoreData UUID) between a
/// <see cref="Treatment"/> and the V4 records it decomposes into, so the projected treatment
/// still answers the <c>DELETE /api/v1/treatments?find[id][$eq]=…</c> Trio edits and deletes with.
/// </summary>
/// <remarks>
/// Legacy Nightscout keeps <c>id</c> as a plain, non-unique field, and so must we: it is never an
/// identity (<see cref="Treatment.Id"/>). Trio stamps every carb equivalent of one fat/protein entry
/// with the same <c>id</c>, so keying the upsert on it would collapse them into one record.
/// It does tell client records apart where the identity cannot: see <see cref="IsDifferentRecord"/>.
/// </remarks>
public static class TreatmentClientId
{
    public const string Field = "id";

    public static Dictionary<string, object?>? ToRecord(Treatment treatment) =>
        Present(treatment.AdditionalProperties?.GetValueOrDefault(Field)) is { } id
            ? new() { [Field] = id }
            : null;

    public static Dictionary<string, object>? ToTreatment(IReadOnlyDictionary<string, object?>? record) =>
        Present(record?.GetValueOrDefault(Field)) is { } id
            ? new() { [Field] = id }
            : null;

    /// <summary>
    /// Gives <paramref name="update"/> the client id <paramref name="stored"/> holds when the update
    /// names none, so a replace that omits the field does not strand the record from its client.
    /// </summary>
    public static void KeepStored(Treatment update, Treatment stored)
    {
        if (ToRecord(update) is null && ToTreatment(ToRecord(stored)) is { } kept)
            (update.AdditionalProperties ??= new())[Field] = kept[Field];
    }

    /// <summary>The client id inside a serialized additional-properties object, or null.</summary>
    public static string? Of(string? additionalPropertiesJson)
    {
        if (string.IsNullOrEmpty(additionalPropertiesJson))
            return null;

        using var doc = JsonDocument.Parse(additionalPropertiesJson);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty(Field, out var id)
            && Present(id) is not null
                ? id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText()
                : null;
    }

    /// <summary>Marks a legacy id the decomposer derived from a treatment's content.</summary>
    public const string SyntheticIdPrefix = "syn-";

    /// <summary>
    /// Whether two records that share <paramref name="legacyId"/> are nonetheless different client
    /// records. A Trio edit re-uploads under a fresh <c>id</c> with the deleted entry's time and often
    /// its values, so its content-derived identity repeats, and a user tombstone must not swallow it.
    /// Only a synthetic identity can be shared that way: an <c>_id</c> or <c>syncIdentifier</c> is
    /// the uploader naming the record, and the user's delete of it stands. A side without a client id
    /// (a pre-upgrade row, an uploader that sends none) is taken to be the same record.
    /// </summary>
    public static bool IsDifferentRecord(string legacyId, string? incoming, string? stored) =>
        legacyId.StartsWith(SyntheticIdPrefix, StringComparison.Ordinal)
        && incoming is not null
        && stored is not null
        && !string.Equals(incoming, stored, StringComparison.Ordinal);

    private static object? Present(object? id) =>
        id is null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } ? null : id;
}
