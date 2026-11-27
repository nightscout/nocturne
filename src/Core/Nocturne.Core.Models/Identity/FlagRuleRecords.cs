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

    /// <summary>A rule of a team the viewer is staff of, applied to everyone the team follows.</summary>
    Team,
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

    /// <summary>The most rules one team may hold.</summary>
    public const int PerTeam = 20;

    /// <summary>The levels a rule may give. Urgent is kept for the fixed set.</summary>
    public static readonly IReadOnlyList<NotificationUrgency> Levels =
        [NotificationUrgency.Info, NotificationUrgency.Warn, NotificationUrgency.Hazard];

    public const NotificationUrgency DefaultLevel = NotificationUrgency.Warn;
}

/// <summary>What a manager's write of a team's flag rule found.</summary>
public enum TeamFlagRuleChange
{
    /// <summary>The rule was written.</summary>
    Changed,

    /// <summary>The caller does not manage the team, or the team holds no such rule.</summary>
    NotFound,

    /// <summary>The team already holds <see cref="FlagRuleLimits.PerTeam"/> rules.</summary>
    LimitReached,
}

/// <summary>A manager's write of a team's flag rule, and the rule as written when <see cref="Change"/> is changed.</summary>
public sealed record TeamFlagRuleWrite(TeamFlagRuleChange Change, FlagRule? Rule);
