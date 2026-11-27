using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Identity;

/// <summary>How a flag rule compares a person's figure with its value. Both are strict.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FlagRuleComparison>))]
public enum FlagRuleComparison
{
    Below,

    Above,
}

/// <summary>Who a flag rule belongs to, which a reason it gives states.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FlagRuleSource>))]
public enum FlagRuleSource
{
    /// <summary>The viewer's own rule, applied to everyone they follow.</summary>
    Yours,
}

/// <summary>
/// One flag fact compared with one value: a dataset field of the reports engine, over the last
/// <see cref="Days"/> days for a period fact or as it is now for a live fact.
/// </summary>
/// <param name="Value">In the fact's unit; a glucose value is in mg/dL.</param>
/// <param name="Level">Info, Warn or Hazard (<see cref="FlagRuleLimits.Levels"/>).</param>
public sealed record FlagRule(
    Guid Id,
    string Dataset,
    string Field,
    int? Days,
    FlagRuleComparison Comparison,
    double Value,
    NotificationUrgency Level,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>A flag rule as it is created or changed.</summary>
public sealed record FlagRuleDraft(
    string Dataset,
    string Field,
    int? Days,
    FlagRuleComparison Comparison,
    double Value,
    NotificationUrgency Level);

public static class FlagRuleLimits
{
    /// <summary>The most rules one viewer may hold.</summary>
    public const int PerViewer = 20;

    /// <summary>The levels a rule may give. Urgent is kept for the fixed set.</summary>
    public static readonly IReadOnlyList<NotificationUrgency> Levels =
        [NotificationUrgency.Info, NotificationUrgency.Warn, NotificationUrgency.Hazard];

    public const NotificationUrgency DefaultLevel = NotificationUrgency.Warn;
}
