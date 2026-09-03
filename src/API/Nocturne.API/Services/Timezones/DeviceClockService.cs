using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Timezones;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.Timezones;

/// <summary>
/// Persists device-clock offset evidence for the current tenant and derives deviation segments from
/// it. Follows <see cref="TimezoneTimelineService"/>'s scoping model: the tenant is pinned from the
/// ambient accessor on each call so RLS applies in both HTTP and background connector-sync scopes.
///
/// When corrections are enabled for the connector, two things become user-visible:
/// a newly confirmed deviation segment notifies the tenant owner (an audit trail plus a
/// notification, never a prompt), and a sustained change of the account's declared zone appends a
/// timezone timeline entry. With corrections disabled the service only gathers and derives, so the
/// estimator can be validated against real fleet data without being able to move anything.
/// </summary>
/// <seealso cref="IDeviceClockService"/>
public class DeviceClockService : IDeviceClockService
{
    /// <summary>Evidence older than this no longer influences segmentation and is pruned.</summary>
    public const int RetentionDays = 456;

    private const string SegmentNotificationType = "connector.deviceClockDeviation";
    private const string ZoneChangeNotificationType = "connector.declaredZoneChanged";
    private const string NotificationSource = "device-clock";

    private readonly NocturneDbContext _db;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ITimezoneTimelineService _timeline;
    private readonly ITenantOwnerResolver _ownerResolver;
    private readonly IInAppNotificationService _notifications;
    private readonly ILogger<DeviceClockService> _logger;

    public DeviceClockService(
        NocturneDbContext db,
        ITenantAccessor tenantAccessor,
        ITimezoneTimelineService timeline,
        ITenantOwnerResolver ownerResolver,
        IInAppNotificationService notifications,
        ILogger<DeviceClockService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenantAccessor = tenantAccessor ?? throw new ArgumentNullException(nameof(tenantAccessor));
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _ownerResolver = ownerResolver ?? throw new ArgumentNullException(nameof(ownerResolver));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private Guid TenantId
    {
        get
        {
            var tenantId = _tenantAccessor.Context?.TenantId
                ?? throw new InvalidOperationException("No tenant context for device clock access.");
            // Pin the context so RLS (USING + WITH CHECK) scopes to this tenant in any scope.
            _db.TenantId = tenantId;
            return tenantId;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceClockSegment>> RecordObservationsAsync(
        string connector,
        IReadOnlyList<DeviceClockObservation> observations,
        double? expectedFallbackOffsetHours,
        bool correctionsEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connector);
        var tenantId = TenantId;

        var existing = await _db.DeviceClockObservations
            .Where(e => e.Connector == connector)
            .OrderBy(e => e.ObservedAt)
            .ToListAsync(cancellationToken);

        var expected = await BuildExpectedOffsetAsync(expectedFallbackOffsetHours, cancellationToken);
        var segmentsBefore = DeviceClockSegmenter.Derive(
            existing.Select(DeviceClockObservationMapper.ToDomainModel).ToList(), expected);

        var byKey = existing.ToDictionary(e => (e.Source, e.ObservedAt));
        var changed = false;
        foreach (var observation in observations)
        {
            var key = ((int)observation.Source, DateTime.SpecifyKind(observation.ObservedAtUtc, DateTimeKind.Utc));
            if (byKey.TryGetValue(key, out var row))
            {
                // The same batch re-observed later can carry more records (it was caught mid-upload
                // the first time); richer evidence replaces the row, identical evidence is a no-op.
                if (observation.SampleCount > row.SampleCount)
                {
                    row.OffsetMinutes = observation.OffsetMinutes;
                    row.IsEstimate = observation.IsEstimate;
                    row.SampleCount = observation.SampleCount;
                    row.CoversFrom = observation.CoversFromUtc is { } covers
                        ? DateTime.SpecifyKind(covers, DateTimeKind.Utc)
                        : null;
                    row.DeclaredTimezone = observation.DeclaredTimezone;
                    changed = true;
                }

                continue;
            }

            var entity = DeviceClockObservationMapper.ToEntity(observation, tenantId);
            entity.Connector = connector;
            _db.DeviceClockObservations.Add(entity);
            byKey[key] = entity;
            changed = true;
        }

        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        var stale = existing.Where(e => e.ObservedAt < cutoff).ToList();
        if (stale.Count > 0)
        {
            _db.DeviceClockObservations.RemoveRange(stale);
            changed = true;
        }

        if (changed)
            await _db.SaveChangesAsync(cancellationToken);

        var all = byKey.Values
            .Where(e => e.ObservedAt >= cutoff)
            .OrderBy(e => e.ObservedAt)
            .Select(DeviceClockObservationMapper.ToDomainModel)
            .ToList();

        var segments = DeviceClockSegmenter.Derive(all, expected);

        if (correctionsEnabled)
        {
            await MaintainDeclaredZoneAsync(connector, all, tenantId, cancellationToken);
            await NotifyNewSegmentsAsync(connector, segmentsBefore, segments, tenantId, cancellationToken);
        }
        else if (segments.Count > 0)
        {
            _logger.LogInformation(
                "Derived {Count} device-clock deviation segment(s) for connector {Connector} "
                + "(first: {From:o} → {To} at {Offset:+0;-0}min); corrections are disabled, so they were not applied",
                segments.Count, connector, segments[0].FromUtc,
                segments[0].ToUtc?.ToString("o") ?? "open", segments[0].OffsetMinutes);
        }

        return segments;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceClockObservation>> GetObservationsAsync(
        string? connector = null,
        CancellationToken cancellationToken = default)
    {
        _ = TenantId;
        var query = _db.DeviceClockObservations.AsQueryable();
        if (!string.IsNullOrWhiteSpace(connector))
            query = query.Where(e => e.Connector == connector);

        var entities = await query.OrderBy(e => e.ObservedAt).ToListAsync(cancellationToken);
        return entities.Select(DeviceClockObservationMapper.ToDomainModel).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceClockSegment>> GetSegmentsAsync(
        string connector,
        double? expectedFallbackOffsetHours = null,
        CancellationToken cancellationToken = default)
    {
        var observations = await GetObservationsAsync(connector, cancellationToken);
        var expected = await BuildExpectedOffsetAsync(expectedFallbackOffsetHours, cancellationToken);
        return DeviceClockSegmenter.Derive(observations, expected);
    }

    private async Task<Func<DateTime, int>> BuildExpectedOffsetAsync(
        double? fallbackOffsetHours, CancellationToken cancellationToken)
    {
        var resolver = await _timeline.GetResolverAsync(fallbackOffsetHours, cancellationToken);
        return resolver.OffsetMinutesAtUtc;
    }

    /// <summary>
    /// Appends a timeline entry when the account's declared zone has sustainably changed — the
    /// trailing profile observations all report the same valid zone and it differs from the
    /// timeline's latest entry. The account holder asserted the zone themselves (their app wrote it
    /// to their vendor profile), so unlike derived offsets it belongs in the timeline proper.
    /// </summary>
    private async Task MaintainDeclaredZoneAsync(
        string connector,
        IReadOnlyList<DeviceClockObservation> observations,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var trailing = observations
            .Where(o => o.Source == DeviceClockObservationSource.Profile && !string.IsNullOrWhiteSpace(o.DeclaredTimezone))
            .TakeLast(DeviceClockSegmenter.MinConsecutiveObservations)
            .ToList();

        if (trailing.Count < DeviceClockSegmenter.MinConsecutiveObservations)
            return;

        var zone = trailing[^1].DeclaredTimezone!;
        if (trailing.Any(o => !string.Equals(o.DeclaredTimezone, zone, StringComparison.Ordinal)))
            return;

        if (!TimeZoneHelper.TryGetTimeZoneInfoFromId(zone, out var tz))
            return;

        var entries = await _timeline.GetTimelineAsync(cancellationToken);
        var current = entries.Count > 0 ? entries[^1].Timezone : null;
        if (string.Equals(current, zone, StringComparison.Ordinal))
            return;

        // The change was first asserted at the earliest observation of the sustained run; enter the
        // zone at that moment's wall clock there.
        var effectiveFrom = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(trailing[0].ObservedAtUtc, DateTimeKind.Utc), tz);

        await _timeline.UpsertAsync(
            new TimezoneTimelineEntry
            {
                Id = Guid.Empty,
                EffectiveFrom = DateTime.SpecifyKind(effectiveFrom, DateTimeKind.Unspecified),
                Timezone = zone,
            },
            cancellationToken);

        _logger.LogInformation(
            "Appended timezone timeline entry {Zone} effective {EffectiveFrom:o} for tenant {TenantId} "
            + "from {Connector}'s sustained declared-zone change (was {Previous})",
            zone, effectiveFrom, tenantId, connector, current ?? "(none)");

        await TryNotifyOwnerAsync(
            tenantId,
            ZoneChangeNotificationType,
            sourceId: $"{connector}:{zone}:{effectiveFrom:yyyyMMddHHmm}",
            cancellationToken);
    }

    private async Task NotifyNewSegmentsAsync(
        string connector,
        IReadOnlyList<DeviceClockSegment> before,
        IReadOnlyList<DeviceClockSegment> after,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        foreach (var segment in after)
        {
            // A segment is "new" when no prior segment covered its start; refinements of an already
            // known deviation (bounds tightening, the open end closing) stay silent.
            if (before.Any(b => b.Contains(segment.FromUtc) || b.FromUtc == segment.FromUtc))
                continue;

            _logger.LogInformation(
                "Confirmed device-clock deviation for tenant {TenantId}, connector {Connector}: "
                + "{From:o} → {To} at {Offset:+0;-0}min ({Count} observations)",
                tenantId, connector, segment.FromUtc,
                segment.ToUtc?.ToString("o") ?? "open", segment.OffsetMinutes, segment.ObservationCount);

            await TryNotifyOwnerAsync(
                tenantId,
                SegmentNotificationType,
                sourceId: $"{connector}:{segment.FromUtc:yyyyMMddHHmm}:{segment.OffsetMinutes}",
                cancellationToken);
        }
    }

    private async Task TryNotifyOwnerAsync(
        Guid tenantId, string type, string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            var ownerId = await _ownerResolver.GetOwnerSubjectIdAsync(tenantId, cancellationToken);
            if (ownerId is null)
            {
                _logger.LogWarning(
                    "No owner found for tenant {TenantId}; skipping {Type} notification", tenantId, type);
                return;
            }

            // Title and subtitle are i18n keys resolved by the frontend copy layer
            // (notification-labels.ts); the backend has no copy layer.
            var titleKey = type == SegmentNotificationType ? "device_clock_deviation" : "declared_zone_changed";
            await _notifications.CreateNotificationAsync(
                userId: ownerId,
                type: type,
                title: titleKey,
                category: NotificationCategory.Informational,
                source: NotificationSource,
                subtitle: titleKey + "_subtitle",
                sourceId: sourceId,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            // The correction must not fail over its announcement.
            _logger.LogError(ex, "Failed to create {Type} notification for tenant {TenantId}", type, tenantId);
        }
    }
}
