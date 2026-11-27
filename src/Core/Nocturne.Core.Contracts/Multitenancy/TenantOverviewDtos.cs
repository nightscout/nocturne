using System.Text.Json.Serialization;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;

namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>Response envelope for the cross-tenant overview.</summary>
/// <param name="HiddenCount">
/// For one group's overview, how many people of the group, with access of the group's team that has not ended,
/// the caller cannot read. A number only: nothing of those people is in any response. Null for the overview of
/// everyone the caller follows.
/// </param>
/// <param name="PeopleWithRulesPending">
/// How many people the caller's own flag rules are still being checked for. Those people carry only the fixed reasons
/// until a later read; a page re-reads sooner while this is above zero. Zero when the read leaves the rules out.
/// </param>
public record TenantOverviewResponse(List<TenantOverviewItem> Tenants, int? HiddenCount, int PeopleWithRulesPending);

/// <summary>
/// The flag rules a read of people judges beside the fixed reasons every view of them has. A read that names none
/// reads <see cref="Fixed"/>, so a caller that does not know of rules starts no judging of them.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OverviewRuleSet>))]
public enum OverviewRuleSet
{
    /// <summary>The fixed reasons only, for a view that is not the caller's alone, such as the wall.</summary>
    Fixed,

    /// <summary>The fixed reasons and the caller's own flag rules, for a view only the caller sees.</summary>
    All,
}

/// <summary>
/// Per-tenant overview entry. <paramref name="ActiveAlertCount"/> and
/// <paramref name="HighestActiveSeverity"/> are null when the caller's effective
/// permissions on the tenant do not satisfy alerts read.
/// </summary>
/// <param name="RecentReadings">Canonical readings of the last 3 hours, oldest first, at most one per
/// 5-minute bucket. Empty when there are none.</param>
/// <param name="Therapy">Null when the caller's effective permissions on the tenant do not satisfy
/// treatments read.</param>
/// <param name="Devices">Null when the caller's effective permissions on the tenant do not satisfy
/// devices read.</param>
/// <param name="Trackers">Null when the caller's effective permissions on the tenant do not satisfy
/// alerts read.</param>
/// <param name="Attention">Why the person needs attention, most severe first. Empty when they do not.</param>
/// <param name="FirstGroupId">
/// On the overview of everyone the caller follows, the first of the caller's own groups that holds the person, in
/// the order the groups read lists them; null when none does, and on every other read. One id rather than the
/// person's groups, so the caller learns only the heading the person is listed under.
/// </param>
public record TenantOverviewItem(
    Guid TenantId,
    string Slug,
    string DisplayName,
    DateTime? LastReadingAt,
    TenantOverviewReading? Latest,
    GlucoseStatus Status,
    TenantOverviewThresholds Thresholds,
    int? ActiveAlertCount,
    AlertRuleSeverity? HighestActiveSeverity,
    List<TenantOverviewPoint> RecentReadings,
    TenantOverviewTherapy? Therapy,
    TenantOverviewDevices? Devices,
    List<V4TrackerStatus>? Trackers,
    List<TenantOverviewAttention> Attention,
    Guid? FirstGroupId);

/// <summary>A glucose reading on the overview trace.</summary>
public record TenantOverviewPoint(DateTime Timestamp, double Mgdl);

/// <summary>Insulin and carbs on board now. A figure is zero while its <c>Known</c> flag is false.</summary>
public record TenantOverviewTherapy(double Iob, bool IobKnown, double Cob, bool CobKnown);

/// <summary>Latest pump snapshot values. Each is null when no snapshot reported it.</summary>
public record TenantOverviewDevices(double? ReservoirUnits, int? PumpBatteryPercent, DateTime? SnapshotAt);

/// <summary>Why a person needs attention.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TenantOverviewAttentionKind>))]
public enum TenantOverviewAttentionKind
{
    /// <summary>The status is urgent low, low or urgent high.</summary>
    GlucoseOutOfRange,

    /// <summary>The status is stale, or the person has no reading.</summary>
    NoRecentReading,

    /// <summary>An alert excursion is active.</summary>
    ActiveAlert,

    /// <summary>A tracker is at warn urgency or above.</summary>
    TrackerDue,

    /// <summary>A figure met one of the caller's own flag rules (<see cref="TenantOverviewAttention.Rule"/>).</summary>
    FlagRule,
}

/// <summary>
/// A reason a person needs attention. The value fields a kind does not use are null.
/// </summary>
/// <param name="MinutesSinceReading">For <see cref="TenantOverviewAttentionKind.NoRecentReading"/>:
/// null when the caller can see no reading time for the person.</param>
/// <param name="TrackerName">For <see cref="TenantOverviewAttentionKind.TrackerDue"/>.</param>
/// <param name="HoursUntilEvent">For <see cref="TenantOverviewAttentionKind.TrackerDue"/>: null for a
/// tracker that is not an event.</param>
public record TenantOverviewAttention(
    TenantOverviewAttentionKind Kind,
    NotificationUrgency Level,
    int? MinutesSinceReading = null,
    string? TrackerName = null,
    double? HoursUntilEvent = null)
{
    /// <summary>For <see cref="TenantOverviewAttentionKind.FlagRule"/>: the rule and the figure that met it.</summary>
    public TenantOverviewRuleReason? Rule { get; init; }
}

/// <summary>
/// A figure of a person that met one of the caller's own flag rules. A fact, stated as it is: never advice.
/// </summary>
/// <param name="Label">The fact's label, as the flag facts list gives it.</param>
/// <param name="Unit">The unit of <paramref name="Value"/> and <paramref name="RuleValue"/>: mg/dL for glucose.</param>
/// <param name="Range">For a glucose time figure, the target range it is measured against: consensus or own.</param>
/// <param name="Value">The person's figure.</param>
/// <param name="Days">The last N days a period fact covers; null for a fact as it is now.</param>
/// <param name="From">The start of the period the figure was read over; null for a fact as it is now.</param>
/// <param name="To">The end of that period.</param>
/// <param name="Bound">For an own-range fact, the person's own target range the figure was measured against.</param>
/// <param name="Coverage">
/// The percent of the period the data covers, only when it is below the mark a figure is judged sufficient at.
/// </param>
public record TenantOverviewRuleReason(
    Guid RuleId,
    FlagRuleSource Source,
    string Dataset,
    string Field,
    string Label,
    string? Unit,
    string? Range,
    double Value,
    FlagRuleComparison Comparison,
    double RuleValue,
    int? Days,
    DateTime? From,
    DateTime? To,
    TenantOverviewRuleBound? Bound,
    double? Coverage);

/// <summary>A person's own target range, in mg/dL.</summary>
/// <param name="VariesOverDay">The range changes over the day, so <paramref name="Low"/> and <paramref name="High"/> are its widest edges.</param>
public record TenantOverviewRuleBound(double Low, double High, bool VariesOverDay);

/// <summary>Latest canonical glucose reading for a tenant.</summary>
public record TenantOverviewReading(
    double Mgdl,
    double? Delta,
    GlucoseDirection? Direction,
    double? TrendRate,
    DateTime Timestamp);

/// <summary>Resolved classification thresholds (mg/dL) for a tenant.</summary>
public record TenantOverviewThresholds(double UrgentLow, double Low, double High, double UrgentHigh);
