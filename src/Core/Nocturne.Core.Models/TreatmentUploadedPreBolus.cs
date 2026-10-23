using System.Text.Json;

namespace Nocturne.Core.Models;

/// <summary>
/// Carries the <c>preBolus</c> a treatment was uploaded with onto the bolus it decomposes into,
/// beside <see cref="TreatmentUploadedEventType"/>, so the bolus is served back with it as Nightscout
/// stores it.
/// </summary>
public static class TreatmentUploadedPreBolus
{
    public const string Field = "preBolus";

    /// <summary>
    /// <paramref name="record"/> with the preBolus of <paramref name="treatment"/> added, or unchanged
    /// when it carries none.
    /// </summary>
    public static Dictionary<string, object?>? AddTo(Dictionary<string, object?>? record, Treatment treatment)
    {
        if (treatment.PreBolus is not { } preBolus)
            return record;

        record ??= new();
        record[Field] = preBolus;
        return record;
    }

    /// <summary>The uploaded preBolus a record's additional properties hold, or null.</summary>
    public static double? Of(IReadOnlyDictionary<string, object?>? record) =>
        record?.GetValueOrDefault(Field) switch
        {
            double preBolus => preBolus,
            JsonElement { ValueKind: JsonValueKind.Number } preBolus => preBolus.GetDouble(),
            _ => null,
        };
}
