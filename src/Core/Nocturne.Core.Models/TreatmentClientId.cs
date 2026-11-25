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
        KeptField.Add(null, Field, KeptField.Value(treatment.AdditionalProperties, Field));

    public static Dictionary<string, object>? ToTreatment(IReadOnlyDictionary<string, object?>? record) =>
        KeptField.Value(record, Field) is { } id
            ? new() { [Field] = id }
            : null;

    public static void KeepInMetadata(Dictionary<string, object> metadata, Treatment treatment)
    {
        if (KeptField.Value(treatment.AdditionalProperties, Field) is { } id)
            metadata[Field] = id;
    }

    public static void RestoreFromMetadata(Treatment treatment, IDictionary<string, object>? metadata)
    {
        if (metadata is not null && metadata.TryGetValue(Field, out var stored) && KeptField.Present(stored) is { } id)
            (treatment.AdditionalProperties ??= new())[Field] = id;
    }

    /// <summary>
    /// Gives <paramref name="update"/> the client id <paramref name="stored"/> holds when the update
    /// names none, so a replace that omits the field does not strand the record from its client.
    /// </summary>
    public static void KeepStored(Treatment update, Treatment stored)
    {
        if (ToRecord(update) is null && ToTreatment(ToRecord(stored)) is { } kept)
            (update.AdditionalProperties ??= new())[Field] = kept[Field];
    }

    /// <summary>The client id <paramref name="treatment"/> carries, or null.</summary>
    public static string? Of(Treatment treatment) =>
        KeptField.AnyKindAsText(treatment.AdditionalProperties, Field);

    /// <summary>The client id inside a serialized additional-properties object, or null.</summary>
    public static string? Of(string? additionalPropertiesJson)
    {
        if (string.IsNullOrEmpty(additionalPropertiesJson))
            return null;

        using var doc = JsonDocument.Parse(additionalPropertiesJson);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty(Field, out var id)
                ? KeptField.AnyKindAsText(id)
                : null;
    }

    /// <summary>Marks a legacy id the decomposer derived from a treatment's content.</summary>
    public const string SyntheticIdPrefix = "syn-";

    /// <summary>Whether <paramref name="legacyId"/> was derived from a treatment's content rather than sent by its client.</summary>
    public static bool IsSynthetic(string legacyId) =>
        legacyId.StartsWith(SyntheticIdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether two records that share <paramref name="legacyId"/> are nonetheless different client
    /// records. A Trio edit re-uploads under a fresh <c>id</c> with the deleted entry's time and often
    /// its values, so its content-derived identity repeats, and a user tombstone must not swallow it.
    /// Only a synthetic identity can be shared that way: an <c>_id</c> or <c>syncIdentifier</c> is
    /// the uploader naming the record, and the user's delete of it stands. A side without a client id
    /// (a pre-upgrade row, an uploader that sends none) is taken to be the same record.
    /// </summary>
    public static bool IsDifferentRecord(string legacyId, string? incoming, string? stored) =>
        IsSynthetic(legacyId)
        && incoming is not null
        && stored is not null
        && !string.Equals(incoming, stored, StringComparison.Ordinal);
}
