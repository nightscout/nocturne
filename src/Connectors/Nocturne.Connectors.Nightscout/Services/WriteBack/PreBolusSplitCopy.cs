using System.Globalization;
using System.Text.Json;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Nightscout.Services.WriteBack;

/// <summary>
/// The second document Nightscout stored when an earlier write-back sent a treatment with its
/// <c>preBolus</c> (<see cref="TreatmentWriteBackJson"/>): <c>{created_at + preBolus minutes,
/// eventType, carbs, notes}</c>, upserted by <c>created_at</c> and <c>eventType</c> under a minted
/// <c>_id</c>. Once the treatment is written again with its carbs, that copy counts them twice.
/// </summary>
/// <remarks>
/// A document is taken for the copy only in exactly that shape, at that slot. Nightscout's own upsert
/// replaced whatever else was held at that <c>created_at</c> and <c>eventType</c> with it, and every
/// uploader's document carries a field the copy never has (<c>enteredBy</c>, <c>insulin</c>, an
/// <c>identifier</c>).
/// </remarks>
internal static class PreBolusSplitCopy
{
    private static readonly HashSet<string> CopyFields =
        ["_id", "created_at", "eventType", "carbs", "insulin", "notes"];

    /// <summary>
    /// The <c>created_at</c> Nightscout gave the copy, or null when <paramref name="treatment"/> has no
    /// <c>preBolus</c> to have split it on.
    /// </summary>
    public static string? CreatedAt(Treatment treatment)
    {
        if (treatment.PreBolus is not { } preBolus || Math.Round(preBolus) == 0 || treatment.EventType is null)
            return null;

        // prepareData times the document by eventTime when it parses, else by created_at.
        var basis = Parse(treatment.EventTime) ?? Parse(treatment.CreatedAt);
        return basis is { } at ? Iso(at.AddMinutes(Math.Round(preBolus))) : null;
    }

    public static string Query(string endpoint, Treatment treatment, string createdAt)
    {
        var at = Uri.EscapeDataString(createdAt);
        return $"{endpoint}.json?find[created_at][$gte]={at}&find[created_at][$lte]={at}"
            + $"&find[eventType]={Uri.EscapeDataString(treatment.EventType!)}&count=10";
    }

    /// <summary>The <c>_id</c> of each copy in <paramref name="body"/>, a treatment find's answer.</summary>
    public static IReadOnlyList<string> Ids(string body, Treatment treatment, string createdAt)
    {
        var ids = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return ids;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (IsCopy(element, treatment, createdAt))
                    ids.Add(element.GetProperty("_id").GetString()!);
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return ids;
    }

    private static bool IsCopy(JsonElement document, Treatment treatment, string createdAt)
    {
        if (document.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in document.EnumerateObject())
        {
            if (!CopyFields.Contains(property.Name))
                return false;
        }

        return document.TryGetProperty("_id", out var id)
            && id.ValueKind == JsonValueKind.String
            && MongoObjectId.IsObjectId(id.GetString())
            && document.TryGetProperty("eventType", out var eventType)
            && eventType.ValueKind == JsonValueKind.String
            && eventType.GetString() == treatment.EventType
            && document.TryGetProperty("created_at", out var at)
            && at.ValueKind == JsonValueKind.String
            && Parse(at.GetString()) is { } stamped
            && Iso(stamped) == createdAt
            && (!document.TryGetProperty("insulin", out var insulin)
                || insulin.ValueKind == JsonValueKind.Null
                || (insulin.ValueKind == JsonValueKind.Number && insulin.GetDouble() == 0));
    }

    private static DateTimeOffset? Parse(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    private static string Iso(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
