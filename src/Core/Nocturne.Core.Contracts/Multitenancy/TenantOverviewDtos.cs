using System.Text.Json.Serialization;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;

namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>Response envelope for the cross-tenant overview.</summary>
/// <param name="HiddenCount">
/// For one group's overview, how many people of the group, with access of the group's team that has not ended,
/// the caller cannot read. A number only: nothing of those people is in any response. Null for the overview of
/// everyone the caller follows.
/// </param>
public record TenantOverviewResponse(List<TenantOverviewItem> Tenants, int? HiddenCount);

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
    List<TenantOverviewAttention> Attention);

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
    double? HoursUntilEvent = null);

/// <summary>Latest canonical glucose reading for a tenant.</summary>
public record TenantOverviewReading(
    double Mgdl,
    double? Delta,
    GlucoseDirection? Direction,
    double? TrendRate,
    DateTime Timestamp);

/// <summary>Resolved classification thresholds (mg/dL) for a tenant.</summary>
public record TenantOverviewThresholds(double UrgentLow, double Low, double High, double UrgentHigh);
