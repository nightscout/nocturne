using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>Response envelope for the cross-tenant overview.</summary>
public record TenantOverviewResponse(List<TenantOverviewItem> Tenants);

/// <summary>
/// Per-tenant overview entry. <paramref name="ActiveAlertCount"/> and
/// <paramref name="HighestActiveSeverity"/> are null when the caller's effective
/// permissions on the tenant do not satisfy alerts read.
/// </summary>
public record TenantOverviewItem(
    Guid TenantId,
    string Slug,
    string DisplayName,
    DateTime? LastReadingAt,
    TenantOverviewReading? Latest,
    GlucoseStatus Status,
    TenantOverviewThresholds Thresholds,
    int? ActiveAlertCount,
    AlertRuleSeverity? HighestActiveSeverity);

/// <summary>Latest canonical glucose reading for a tenant.</summary>
public record TenantOverviewReading(
    double Mgdl,
    double? Delta,
    GlucoseDirection? Direction,
    double? TrendRate,
    DateTime Timestamp);

/// <summary>Resolved classification thresholds (mg/dL) for a tenant.</summary>
public record TenantOverviewThresholds(double UrgentLow, double Low, double High, double UrgentHigh);
