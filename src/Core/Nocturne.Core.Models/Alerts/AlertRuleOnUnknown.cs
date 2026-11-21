namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// What an alert rule's unknown root means to its excursion tracker
/// (docs/alerts/engine-semantics.md §6.4).
/// </summary>
/// <remarks>
/// Stored as plain strings rather than an enum column, for the reason given on
/// <see cref="AlertRuleReviewReasons"/>.
/// </remarks>
public static class AlertRuleOnUnknown
{
    /// <summary>The tracker is not advanced: an open excursion stays open, and nothing opens.</summary>
    public const string Hold = "hold";

    /// <summary>An unknown root is read as true.</summary>
    public const string Met = "met";

    /// <summary>An unknown root is read as false.</summary>
    public const string NotMet = "not_met";

    /// <summary>Whether <paramref name="value"/> is exactly one of the three words.</summary>
    public static bool IsKnown(string? value) => value is Hold or Met or NotMet;
}
