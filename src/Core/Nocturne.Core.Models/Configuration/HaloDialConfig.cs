using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// Per-tenant configuration for the dashboard Halo Dial component. Defaults
/// are server-authored: a fresh tenant gets the values produced by the
/// parameterless constructor and never has to ship a payload to "see"
/// the default dial.
/// </summary>
public class HaloDialConfig
{
    /// <summary>
    /// Schema version of this saved configuration. Used by
    /// <c>HaloDialSchemaMigrator</c> to upgrade older blobs in place.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("colorMode")]
    public HaloDialColorMode ColorMode { get; set; } = HaloDialColorMode.Discrete;

    /// <summary>Minutes of history rendered on the ring, 0–360 in steps of 5.</summary>
    [JsonPropertyName("historyMinutes")]
    public int HistoryMinutes { get; set; } = 15;

    /// <summary>Minutes of prediction rendered on the ring, 0–120 in steps of 5.</summary>
    [JsonPropertyName("predictionMinutes")]
    public int PredictionMinutes { get; set; } = 45;

    [JsonPropertyName("predictionCurve")]
    public HaloDialPredictionCurve PredictionCurve { get; set; } = HaloDialPredictionCurve.Main;

    [JsonPropertyName("centerSub")]
    public HaloDialCenterSubElement CenterSub { get; set; } = HaloDialCenterSubElement.MinutesAndDelta;

    [JsonPropertyName("innerLeftArc")]
    public HaloDialArcElement? InnerLeftArc { get; set; } = HaloDialArcElement.Cob;

    [JsonPropertyName("innerRightArc")]
    public HaloDialArcElement? InnerRightArc { get; set; } = HaloDialArcElement.Iob;

    /// <summary>Capacity ceiling used to map IOB to the inner-arc sweep.</summary>
    [JsonPropertyName("iobMaxUnits")]
    public double IobMaxUnits { get; set; } = 8.0;

    /// <summary>Capacity ceiling used to map COB to the inner-arc sweep.</summary>
    [JsonPropertyName("cobMaxGrams")]
    public double CobMaxGrams { get; set; } = 80.0;

    [JsonPropertyName("corners")]
    public HaloDialCorners Corners { get; set; } = new();

    /// <summary>
    /// Per-element configuration keyed by element name (corner or arc element).
    /// Shape is element-specific and intentionally untyped on the C# side;
    /// the editor and renderer apply a discriminated union on the frontend.
    /// </summary>
    [JsonPropertyName("elementConfig")]
    public Dictionary<string, JsonElement> ElementConfig { get; set; } = new();
}

/// <summary>
/// The four corner-slot stacks on the Halo Dial. Each holds up to three
/// elements rendered top-to-bottom. Defaults reproduce the source design's
/// "loop dot top-right; direction + eventual + loop label bottom-right".
/// </summary>
public class HaloDialCorners
{
    [JsonPropertyName("tl")]
    public List<HaloDialCornerElement> Tl { get; set; } = new();

    [JsonPropertyName("tr")]
    public List<HaloDialCornerElement> Tr { get; set; } = new() { HaloDialCornerElement.LoopDot };

    [JsonPropertyName("bl")]
    public List<HaloDialCornerElement> Bl { get; set; } = new();

    [JsonPropertyName("br")]
    public List<HaloDialCornerElement> Br { get; set; } = new()
    {
        HaloDialCornerElement.Direction,
        HaloDialCornerElement.Eventual,
        HaloDialCornerElement.LoopLabel,
    };
}
