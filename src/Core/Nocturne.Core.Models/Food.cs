using System.Reflection;
using System.Text.Json.Serialization;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.Serializers;

namespace Nocturne.Core.Models;

/// <summary>
/// Nightscout food record for the API.
/// Compatible with the legacy Nightscout food collection.
/// Records with <c>type="quickpick"</c> are composite shortcuts containing multiple <see cref="QuickPickFood"/> items.
/// </summary>
/// <seealso cref="QuickPickFood"/>
/// <seealso cref="ConnectorFoodEntry"/>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class Food
{
    /// <summary>The field names a find on the food collection may name: the wire names of its properties.</summary>
    private static readonly IReadOnlySet<string> WireFields = typeof(Food)
        .GetProperties()
        .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Why a find cannot select foods exactly, or null when it can: it names a field the collection does
    /// not have, or uses an operator <see cref="FindQuery.Matches"/> cannot apply. Either could widen a
    /// delete past what the find names.
    /// </summary>
    public static string? FindRefusal(FindQuery query)
    {
        var unsupported = query.Fields.Where(f => !WireFields.Contains(f)).Order().ToList();
        if (unsupported.Count > 0)
            return $"The food collection cannot be filtered on: {string.Join(", ", unsupported)}.";

        return query.HasInexactCondition ? "The find uses an operator the food collection cannot apply." : null;
    }

    /// <summary>
    /// MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    public string? Id { get; set; }

    /// <summary>
    /// Type of record ("food" or "quickpick")
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "food";

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("subcategory")]
    public string Subcategory { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("portion")]
    public double Portion { get; set; }

    /// <summary>
    /// Carbohydrates in grams per portion
    /// </summary>
    [JsonPropertyName("carbs")]
    public double Carbs { get; set; }

    /// <summary>
    /// Fat content in grams per portion
    /// </summary>
    [JsonPropertyName("fat")]
    public double Fat { get; set; }

    /// <summary>
    /// Protein content in grams per portion
    /// </summary>
    [JsonPropertyName("protein")]
    public double Protein { get; set; }

    /// <summary>
    /// Energy content in kilojoules per portion
    /// </summary>
    [JsonPropertyName("energy")]
    public double Energy { get; set; }

    /// <summary>
    /// Glycemic index (1-3: 1=low, 2=medium, 3=high)
    /// </summary>
    [JsonPropertyName("gi")]
    public int Gi { get; set; } = 2;

    /// <summary>
    /// Unit of measurement (g, ml, pcs, oz)
    /// </summary>
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "g";

    // QuickPick specific properties

    /// <summary>
    /// Foods included in a quickpick (only for type="quickpick")
    /// </summary>
    [JsonPropertyName("foods")]
    public List<QuickPickFood>? Foods { get; set; }

    /// <summary>
    /// Whether to hide after use (only for type="quickpick")
    /// </summary>
    [JsonPropertyName("hideafteruse")]
    [JsonConverter(typeof(FlexibleNonNullableBooleanJsonConverter))]
    public bool HideAfterUse { get; set; }

    /// <summary>
    /// Whether the quickpick is hidden (only for type="quickpick")
    /// </summary>
    [JsonPropertyName("hidden")]
    [JsonConverter(typeof(FlexibleNonNullableBooleanJsonConverter))]
    public bool Hidden { get; set; }

    /// <summary>
    /// Display position for quickpicks (only for type="quickpick")
    /// </summary>
    [JsonPropertyName("position")]
    public int Position { get; set; } = 99999;

    /// <summary>
    /// Creation timestamp (ISO 8601 format)
    /// This is set by the server when a record is created
    /// </summary>
    [JsonPropertyName("created_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedAt { get; set; }

    /// <summary>
    /// The server write stamp (Unix milliseconds) a v3 history read pages on, moved by every edit and
    /// by the delete.
    /// </summary>
    [JsonPropertyName("srvModified")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SrvModified { get; set; }

    /// <summary>The server creation stamp (Unix milliseconds).</summary>
    [JsonPropertyName("srvCreated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SrvCreated { get; set; }

    /// <summary>
    /// <c>false</c> on a deleted document, which a v3 history read still returns so a syncing client
    /// learns of the delete; unset on a live one, as Nightscout leaves it.
    /// </summary>
    [JsonPropertyName("isValid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsValid { get; set; }
}

/// <summary>
/// Food item within a quickpick
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class QuickPickFood
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("portion")]
    public double Portion { get; set; }

    /// <summary>
    /// Carbohydrates in grams per portion
    /// </summary>
    [JsonPropertyName("carbs")]
    public double Carbs { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "g";

    /// <summary>
    /// Number of portions for this food in the quickpick
    /// </summary>
    [JsonPropertyName("portions")]
    public double Portions { get; set; } = 1.0;
}
