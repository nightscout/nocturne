using System.Text.Json;

namespace Nocturne.Core.Models;

/// <summary>
/// Carries the <c>timestamp</c> a treatment was uploaded with between a <see cref="Treatment"/> and
/// the V4 records it decomposes into, beside <see cref="TreatmentClientId"/>, so it is served back
/// verbatim, with its JSON type, as Nightscout stores it.
/// </summary>
/// <remarks>
/// NightscoutKit drops a served treatment that has no string <c>timestamp</c>, so a Loop follower
/// (LoopCaregiver) sees none of Loop's own uploads without it. Nothing is invented for a treatment
/// uploaded without one: AAPS reads <c>timestamp</c> as a number, and a string it cannot parse
/// fails its whole page.
/// </remarks>
public static class TreatmentUploadedTimestamp
{
    public const string Field = "timestamp";

    /// <summary>
    /// <paramref name="record"/> with the uploaded timestamp of <paramref name="treatment"/> added, or
    /// unchanged when it carries none.
    /// </summary>
    public static Dictionary<string, object?>? AddTo(Dictionary<string, object?>? record, Treatment treatment)
    {
        if (Uploaded(treatment) is not { } timestamp)
            return record;

        record ??= new();
        record[Field] = timestamp;
        return record;
    }

    /// <summary>The uploaded timestamp of <paramref name="treatment"/> as stored, or null.</summary>
    public static JsonElement? Uploaded(Treatment treatment) =>
        treatment.RawTimestamp is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } raw
            ? raw.Clone()
            : null;

    /// <summary>The uploaded timestamp a record's additional properties hold, or null.</summary>
    public static JsonElement? Of(IReadOnlyDictionary<string, object?>? record) =>
        Read(record?.GetValueOrDefault(Field));

    /// <summary>The uploaded timestamp a span's metadata holds, or null.</summary>
    public static JsonElement? OfSpan(IDictionary<string, object>? metadata) =>
        metadata is not null && metadata.TryGetValue(Field, out var value) ? Read(value) : null;

    private static JsonElement? Read(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement je => je,
        _ => JsonSerializer.SerializeToElement(value),
    };
}
