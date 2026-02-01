namespace Nocturne.Connectors.Core.Models;

/// <summary>
/// Represents a generic treatment event for classification purposes.
/// Connectors map their connector-specific events to this common format.
/// </summary>
public record TreatmentEvent
{
    /// <summary>
    /// Unique identifier for this event (used for matching and lookup).
    /// </summary>
    public required string EventKey { get; init; }

    /// <summary>
    /// Timestamp in Unix milliseconds.
    /// </summary>
    public required long TimestampMs { get; init; }

    /// <summary>
    /// Carbohydrate amount in grams (for carb events).
    /// </summary>
    public double? Carbs { get; init; }

    /// <summary>
    /// Insulin amount in units (for insulin events).
    /// </summary>
    public double? Insulin { get; init; }

    /// <summary>
    /// Carbs embedded in the bolus data (e.g., from bolus calculator).
    /// Takes precedence over standalone carb events when matching.
    /// </summary>
    public double? EmbeddedCarbs { get; init; }
}

/// <summary>
/// Configuration options for treatment classification and matching.
/// </summary>
public record TreatmentClassificationOptions
{
    /// <summary>
    /// Default matching window (45 minutes) for backward compatibility with Glooko.
    /// </summary>
    public static readonly int DefaultMatchingWindowMs = 45 * 60 * 1000;

    /// <summary>
    /// Time window in milliseconds for matching carbs to insulin.
    /// Default is 45 minutes (2,700,000 ms) for backward compatibility with Glooko.
    /// </summary>
    public int MatchingWindowMs { get; init; } = DefaultMatchingWindowMs;

    /// <summary>
    /// Whether to suppress standalone carb events when they're matched to insulin.
    /// When true, matched carb events should not generate separate Carb Correction treatments.
    /// Default is true.
    /// </summary>
    public bool SuppressMatchedCarbEvents { get; init; } = true;

    /// <summary>
    /// Whether embedded carbs in insulin events take precedence over standalone carb events.
    /// When true, if an insulin event has embedded carbs, nearby standalone carb events are suppressed.
    /// Default is true.
    /// </summary>
    public bool EmbeddedCarbsTakePrecedence { get; init; } = true;
}

/// <summary>
/// Result of pre-processing carb and insulin events for matching.
/// Contains the mappings needed to classify treatments and suppress duplicates.
/// </summary>
public class TreatmentClassificationContext
{
    /// <summary>
    /// Maps insulin event keys to their matched carb amounts.
    /// </summary>
    public Dictionary<string, double> InsulinToCarbMatches { get; } = new();

    /// <summary>
    /// Set of carb event timestamps (ms) that have been matched and should be suppressed.
    /// </summary>
    public HashSet<long> SuppressedCarbTimestamps { get; } = new();

    /// <summary>
    /// Set of carb event keys that have been matched to an insulin event.
    /// </summary>
    public HashSet<string> MatchedCarbEventKeys { get; } = new();

    /// <summary>
    /// The options used to create this context.
    /// </summary>
    public required TreatmentClassificationOptions Options { get; init; }

    /// <summary>
    /// Checks if a carb event at the given timestamp should be suppressed.
    /// </summary>
    public bool ShouldSuppressCarbEvent(long timestampMs)
    {
        return Options.SuppressMatchedCarbEvents && SuppressedCarbTimestamps.Contains(timestampMs);
    }

    /// <summary>
    /// Checks if a carb event with the given key has been matched to an insulin event.
    /// </summary>
    public bool IsCarbEventMatched(string eventKey)
    {
        return MatchedCarbEventKeys.Contains(eventKey);
    }

    /// <summary>
    /// Gets the matched carbs for an insulin event, if any.
    /// </summary>
    public double? GetMatchedCarbs(string insulinEventKey)
    {
        return InsulinToCarbMatches.TryGetValue(insulinEventKey, out var carbs) ? carbs : null;
    }
}
