using System.Text.Json;

namespace Nocturne.Core.Models;

/// <summary>
/// Carries the <c>eventType</c> a treatment was uploaded with onto a bolus or carb intake it
/// decomposes into, beside <see cref="TreatmentClientId"/>, so it is served back as Nightscout
/// stores it: an insulin-only Meal Bolus is a Meal Bolus, not the Correction Bolus a bolus reads as
/// on its own.
/// </summary>
/// <remarks>
/// Kept only where it differs from what the record is served as without it, so the many records
/// uploaded under that default carry nothing.
/// </remarks>
public static class TreatmentUploadedEventType
{
    public const string Field = "eventType";

    /// <summary>
    /// <paramref name="record"/> with the event type of <paramref name="treatment"/> added, or
    /// unchanged when it carries none or carries <paramref name="servedWithout"/>.
    /// </summary>
    public static Dictionary<string, object?>? AddTo(
        Dictionary<string, object?>? record, Treatment treatment, string servedWithout)
    {
        if (string.IsNullOrWhiteSpace(treatment.EventType)
            || string.Equals(treatment.EventType, servedWithout, StringComparison.Ordinal))
            return record;

        record ??= new();
        record[Field] = treatment.EventType;
        return record;
    }

    /// <summary>The uploaded event type a record's additional properties hold, or null.</summary>
    public static string? Of(IReadOnlyDictionary<string, object?>? record) =>
        record?.GetValueOrDefault(Field) switch
        {
            string eventType => eventType,
            JsonElement { ValueKind: JsonValueKind.String } eventType => eventType.GetString(),
            _ => null,
        };
}
