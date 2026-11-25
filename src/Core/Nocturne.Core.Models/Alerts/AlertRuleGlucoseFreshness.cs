namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// The range of a rule's own glucose freshness bound, in minutes
/// (docs/alerts/engine-semantics.md §3). The engine rejects any other value.
/// </summary>
public static class AlertRuleGlucoseFreshness
{
    public const int MinMinutes = 1;

    public const int MaxMinutes = 1440;

    /// <summary>The bound a rule with none of its own gets; the engine's <c>DEFAULT_GLUCOSE_FRESHNESS</c>.</summary>
    public const int DefaultMinutes = 11;

    /// <summary>Whether <paramref name="minutes"/> is absent, the engine's default, or in range.</summary>
    public static bool IsValid(int? minutes) => minutes is null or (>= MinMinutes and <= MaxMinutes);

    public const string InvalidMessage = "GlucoseFreshnessMinutes must be a whole number of minutes from 1 to 1440";
}
