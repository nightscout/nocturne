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
/// timezone timeline entry. Each user-visible action is stamped on the observation that anchors it
/// (<see cref="DeviceClockObservationEntity.AppliedAt"/>), so the same assertion acts exactly once —
/// deleting the timeline entry or archiving the notification does not make it come back; only fresh
/// evidence can trigger again. With corrections disabled the service only gathers and derives, so
/// the estimator can be validated against real fleet data without being able to move anything.
/// </summary>
/// <seealso cref="IDeviceClockService"/>
public class DeviceClockService : IDeviceClockService
{
    /// <summary>Evidence older than this no longer influences segmentation and is pruned.</summary>
    public const int RetentionDays = IDeviceClockService.RetentionDays;

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

        var byKey = existing.ToDictionary(e => (e.Source, e.ObservedAt));
        var changed = false;
        foreach (var observation in observations)
        {
            var key = ((int)observation.Source, DateTime.SpecifyKind(observation.ObservedAtUtc, DateTimeKind.Utc));
            if (byKey.TryGetValue(key, out var row))
            {
                // The same batch re-observed later can carry more records (it was caught mid-upload
                // the first time); richer evidence replaces the row — but a bound never replaces a
                // two-sided estimate, however many records it carries.
                if (observation.SampleCount > row.SampleCount && (observation.IsEstimate || !row.IsEstimate))
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

        var live = byKey.Values
            .Where(e => e.ObservedAt >= cutoff)
            .OrderBy(e => e.ObservedAt)
            .ToList();
        var all = live.Select(DeviceClockObservationMapper.ToDomainModel).ToList();

        var expected = await BuildExpectedOffsetAsync(expectedFallbackOffsetHours, cancellationToken);
        var segments = DeviceClockSegmenter.Derive(all, expected);

        if (correctionsEnabled)
        {
            var stampedZone = await MaintainDeclaredZoneAsync(connector, all, live, tenantId, cancellationToken);
            var stampedSegments = await AnnounceSegmentsAsync(connector, segments, live, tenantId, cancellationToken);
            if (stampedZone || stampedSegments)
                await _db.SaveChangesAsync(cancellationToken);
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
        // Without a timeline or a caller-supplied static offset there is no expected clock to
        // deviate from — measuring against zero would show segments the connector (which always
        // passes its configured offset) never derives.
        if (expectedFallbackOffsetHours is null
            && (await _timeline.GetTimelineAsync(cancellationToken)).Count == 0)
            return [];

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
    /// to their vendor profile), so unlike derived offsets it belongs in the timeline proper. The
    /// newest assertion is stamped applied whatever the outcome, so an unchanged profile can never
    /// re-append (or re-fight a user who deleted the entry): only a fresh profile write re-opens the
    /// question.
    /// </summary>
    /// <returns>Whether an observation was stamped (caller saves).</returns>
    private async Task<bool> MaintainDeclaredZoneAsync(
        string connector,
        IReadOnlyList<DeviceClockObservation> observations,
        IReadOnlyList<DeviceClockObservationEntity> entities,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var trailing = observations
            .Where(o => o.Source == DeviceClockObservationSource.Profile && !string.IsNullOrWhiteSpace(o.DeclaredTimezone))
            .TakeLast(DeviceClockSegmenter.MinConsecutiveObservations)
            .ToList();

        if (trailing.Count < DeviceClockSegmenter.MinConsecutiveObservations)
            return false;

        var zone = trailing[^1].DeclaredTimezone!;
        if (trailing.Any(o => !string.Equals(o.DeclaredTimezone, zone, StringComparison.Ordinal)))
            return false;

        if (!TimeZoneHelper.TryGetTimeZoneInfoFromId(zone, out var tz))
            return false;

        var anchor = entities.FirstOrDefault(e =>
            e.Source == (int)DeviceClockObservationSource.Profile
            && e.ObservedAt == trailing[^1].ObservedAtUtc);
        if (anchor is null || anchor.AppliedAt is not null)
            return false;

        var entries = await _timeline.GetTimelineAsync(cancellationToken);
        var current = entries.Count > 0 ? entries[^1].Timezone : null;

        // The change was first asserted at the earliest observation of the sustained run; enter the
        // zone at that moment's wall clock there.
        var effectiveFrom = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(trailing[0].ObservedAtUtc, DateTimeKind.Utc), tz),
            DateTimeKind.Unspecified);

        // Skip (but still stamp) when there is nothing to do or the slot is taken: the zone is
        // already current, an identical entry exists (e.g. this service added it before the marker
        // existed), or any entry occupies the same instant — inserting there would violate the
        // timeline's unique effective_from and a failed insert would poison the change tracker.
        var occupied = entries.Any(e => e.EffectiveFrom == effectiveFrom);
        if (!string.Equals(current, zone, StringComparison.Ordinal) && !occupied)
        {
            await _timeline.UpsertAsync(
                new TimezoneTimelineEntry
                {
                    Id = Guid.Empty,
                    EffectiveFrom = effectiveFrom,
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

        anchor.AppliedAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Announces deviation segments the owner has never been told about. Every observation inside
    /// the segment's window is stamped, and one stamped member marks the whole segment as announced
    /// — so extensions, re-derivations, enabling the flag later, archiving the notification,
    /// notification cleanup, and retention pruning the earliest members (which shifts the segment's
    /// opening observation) can never re-announce it.
    /// </summary>
    /// <returns>Whether any observation was stamped (caller saves).</returns>
    private async Task<bool> AnnounceSegmentsAsync(
        string connector,
        IReadOnlyList<DeviceClockSegment> segments,
        IReadOnlyList<DeviceClockObservationEntity> entities,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var stamped = false;
        foreach (var segment in segments)
        {
            // The window is [first supporting observation, last supporting observation]: a closed
            // segment's ToUtc is its last supporting observation, an open one runs to the end.
            var anchors = entities
                .Where(e => e.ObservedAt >= segment.FirstObservedAtUtc
                            && (segment.ToUtc is not { } end || e.ObservedAt <= end))
                .ToList();
            if (anchors.Count == 0 || anchors.Any(a => a.AppliedAt is not null))
                continue;

            _logger.LogInformation(
                "Confirmed device-clock deviation for tenant {TenantId}, connector {Connector}: "
                + "{From:o} → {To} at {Offset:+0;-0}min ({Count} observations)",
                tenantId, connector, segment.FromUtc,
                segment.ToUtc?.ToString("o") ?? "open", segment.OffsetMinutes, segment.ObservationCount);

            var announced = await TryNotifyOwnerAsync(
                tenantId,
                SegmentNotificationType,
                sourceId: $"{connector}:{segment.FirstObservedAtUtc:yyyyMMddHHmm}:{segment.OffsetMinutes}",
                cancellationToken);

            if (!announced)
                continue; // transient failure: leave the anchor unstamped so the next sync retries

            foreach (var anchor in anchors)
                anchor.AppliedAt = DateTime.UtcNow;
            stamped = true;
        }

        return stamped;
    }

    /// <summary>
    /// Creates an owner notification. Returns true when the announcement is settled (created, or the
    /// tenant has no owner to tell); false only on a transient failure worth retrying.
    /// </summary>
    private async Task<bool> TryNotifyOwnerAsync(
        Guid tenantId, string type, string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            var ownerId = await _ownerResolver.GetOwnerSubjectIdAsync(tenantId, cancellationToken);
            if (ownerId is null)
            {
                _logger.LogWarning(
                    "No owner found for tenant {TenantId}; skipping {Type} notification", tenantId, type);
                return true;
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
            return true;
        }
        catch (Exception ex)
        {
            // The correction must not fail over its announcement.
            _logger.LogError(ex, "Failed to create {Type} notification for tenant {TenantId}", type, tenantId);
            return false;
        }
    }
}
