using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents the glucose trend direction indicators used by Nightscout.
/// These values indicate the rate and direction of glucose change.
/// 1:1 Legacy JavaScript compatibility with ClientApp/lib/plugins/direction.js
/// </summary>
/// <remarks>
/// Three of the legacy wire spellings contain spaces and so cannot be member names. The wire form of
/// every value is <see cref="DirectionExtensions.ToWireString(Direction)"/>; <see cref="JsonStringEnumConverter"/>
/// ignores per-member naming attributes, so member names — not wire spellings — are what this enum
/// serialises as on its own.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Direction
{
    /// <summary>
    /// No direction information available
    /// </summary>
    NONE,

    /// <summary>
    /// Rising very rapidly (>3 mg/dL per minute)
    /// </summary>
    TripleUp,

    /// <summary>
    /// Rising rapidly (2-3 mg/dL per minute)
    /// </summary>
    DoubleUp,

    /// <summary>
    /// Rising (1-2 mg/dL per minute)
    /// </summary>
    SingleUp,

    /// <summary>
    /// Rising slowly (0.5-1 mg/dL per minute)
    /// </summary>
    FortyFiveUp,

    /// <summary>
    /// Stable (change less than 0.5 mg/dL per minute)
    /// </summary>
    Flat,

    /// <summary>
    /// Falling slowly (0.5-1 mg/dL per minute)
    /// </summary>
    FortyFiveDown,

    /// <summary>
    /// Falling (1-2 mg/dL per minute)
    /// </summary>
    SingleDown,

    /// <summary>
    /// Falling rapidly (2-3 mg/dL per minute)
    /// </summary>
    DoubleDown,

    /// <summary>
    /// Falling very rapidly (>3 mg/dL per minute)
    /// </summary>
    TripleDown,

    /// <summary>
    /// CGM cannot determine direction due to insufficient data
    /// </summary>
    NotComputable,

    /// <summary>
    /// Rate of change is outside measurable range
    /// </summary>
    RateOutOfRange,

    /// <summary>
    /// CGM sensor error or malfunction
    /// </summary>
    CgmError,
}

/// <summary>
/// Extension methods for Direction enum
/// </summary>
public static class DirectionExtensions
{
    /// <summary>
    /// The legacy Nightscout wire spelling of each <see cref="Direction"/>, from
    /// <c>ClientApp/lib/plugins/direction.js</c>. Sole source of truth for the string form: v1/v3
    /// responses, the <c>entries</c> SignalR broadcast and the CSV/TSV export all read it, and
    /// <see cref="TryParse"/> indexes it.
    /// </summary>
    private static readonly Dictionary<Direction, string> WireNames = new()
    {
        { Direction.NONE, "NONE" },
        { Direction.TripleUp, nameof(Direction.TripleUp) },
        { Direction.DoubleUp, nameof(Direction.DoubleUp) },
        { Direction.SingleUp, nameof(Direction.SingleUp) },
        { Direction.FortyFiveUp, nameof(Direction.FortyFiveUp) },
        { Direction.Flat, nameof(Direction.Flat) },
        { Direction.FortyFiveDown, nameof(Direction.FortyFiveDown) },
        { Direction.SingleDown, nameof(Direction.SingleDown) },
        { Direction.DoubleDown, nameof(Direction.DoubleDown) },
        { Direction.TripleDown, nameof(Direction.TripleDown) },
        { Direction.NotComputable, "NOT COMPUTABLE" },
        { Direction.RateOutOfRange, "RATE OUT OF RANGE" },
        { Direction.CgmError, "CGM ERROR" },
    };

    private static readonly Dictionary<string, Direction> ByName = BuildNameIndex();

    /// <summary>
    /// Gets the legacy Nightscout wire spelling of a direction — <c>"NOT COMPUTABLE"</c> rather than
    /// the member name <c>NotComputable</c>. Use this wherever a direction crosses the v1/v2/v3
    /// surface; <c>ToString()</c> emits the member name, which Nightscout clients do not recognise.
    /// </summary>
    public static string ToWireString(this Direction direction) =>
        WireNames.TryGetValue(direction, out var name) ? name : WireNames[Direction.NONE];

    /// <summary>
    /// Parses a direction string, accepting both the legacy wire spelling
    /// (<c>"NOT COMPUTABLE"</c>) and the member name (<c>"NotComputable"</c>), case-insensitively.
    /// </summary>
    /// <returns><see langword="true"/> when <paramref name="value"/> names a direction.</returns>
    public static bool TryParse(string? value, out Direction direction)
    {
        if (string.IsNullOrEmpty(value))
        {
            direction = Direction.NONE;
            return false;
        }

        return ByName.TryGetValue(value, out direction);
    }

    /// <summary>
    /// Parses a direction string as <see cref="TryParse"/> does, falling back to
    /// <see cref="Direction.NONE"/> for null, empty and unrecognised values.
    /// </summary>
    public static Direction Parse(string? value) =>
        TryParse(value, out var direction) ? direction : Direction.NONE;

    private static Dictionary<string, Direction> BuildNameIndex()
    {
        var index = new Dictionary<string, Direction>(StringComparer.OrdinalIgnoreCase);

        foreach (var direction in Enum.GetValues<Direction>())
        {
            index[direction.ToString()] = direction;
            index[direction.ToWireString()] = direction;
        }

        return index;
    }

    /// <summary>
    /// Converts a <see cref="Direction"/> enum value to the Nightscout/Dexcom trend number (0-9).
    /// Used by the pebble endpoint and other legacy integrations.
    /// </summary>
    /// <param name="direction">The glucose trend direction to convert</param>
    /// <returns>
    /// An integer 0-9 matching the Dexcom trend number convention:
    /// 0=None, 1=DoubleUp, 2=SingleUp, 3=FortyFiveUp, 4=Flat,
    /// 5=FortyFiveDown, 6=SingleDown, 7=DoubleDown, 8=NotComputable, 9=RateOutOfRange
    /// </returns>
    public static int ToTrendNumber(this Direction direction)
    {
        return direction switch
        {
            Direction.NONE => 0,
            Direction.DoubleUp => 1,
            Direction.SingleUp => 2,
            Direction.FortyFiveUp => 3,
            Direction.Flat => 4,
            Direction.FortyFiveDown => 5,
            Direction.SingleDown => 6,
            Direction.DoubleDown => 7,
            Direction.TripleUp => 1,      // Map to DoubleUp (closest)
            Direction.TripleDown => 7,    // Map to DoubleDown (closest)
            Direction.NotComputable => 8,
            Direction.RateOutOfRange => 9,
            Direction.CgmError => 8,      // Map to NotComputable
            _ => 8
        };
    }

    /// <summary>
    /// Parses a direction string to the corresponding Dexcom trend number (0-9).
    /// Accepts both spellings via <see cref="TryParse"/>.
    /// </summary>
    /// <param name="direction">Direction string to parse; null or empty returns 8 (NotComputable)</param>
    /// <returns>Dexcom trend number (0-9); returns 8 (NotComputable) for any unrecognized input</returns>
    public static int ParseToTrendNumber(string? direction) =>
        TryParse(direction, out var parsed) ? parsed.ToTrendNumber() : 8;
}
