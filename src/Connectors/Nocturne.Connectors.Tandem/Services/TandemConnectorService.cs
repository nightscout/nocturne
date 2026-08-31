using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tandem.Configurations;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Connectors.Tandem.Mappers;
using Nocturne.Connectors.Tandem.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Tandem.Services;

/// <summary>
/// Connector service for Tandem Source (t:connect). Authenticates, selects a pump, then walks its
/// event history in date-chunked windows, decoding each window's pump events and mapping them to
/// Nocturne V4 records. The data covered mirrors the open-source <c>tconnectsync</c> project:
/// CGM readings, boluses (with carbs and calculations), basal delivery, cartridge/cannula/tubing
/// and CGM-session device events, pump suspend/resume, alarms, CGM alerts, sleep/exercise spans,
/// device status, and profiles.
/// </summary>
public class TandemConnectorService : BaseConnectorService<TandemConnectorConfiguration>
{

    private readonly TandemAuthTokenProvider _tokenProvider;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly TandemSourceApiClient _apiClient;

    public TandemConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<TandemConnectorConfiguration> serverResolver,
        ILogger<TandemConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        TandemAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null)
        : base(httpClient, serverResolver, logger, publisher)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _apiClient = new TandemSourceApiClient(httpClient, logger);
    }

    protected override string ConnectorSource => DataSources.TConnectSyncConnector;
    public override string ServiceName => "Tandem Source";


    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        TandemConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };
        var activeTypes = ResolveActiveTypes(request, config);
        var region = TandemConstants.ForRegion(config.Region);

        try
        {
            var token = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
            var session = await _tokenProvider.GetCachedSessionAsync();
            var pumperId = session?.Metadata?.GetValueOrDefault(TandemAuthTokenProvider.PumperIdKey);
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(pumperId))
            {
                _logger.LogError("[{Source}] Tandem Source authentication failed", ConnectorSource);
                result.Success = false;
                result.Errors.Add("Authentication failed");
                result.EndTime = DateTimeOffset.UtcNow;
                return result;
            }

            var pumper = await _apiClient.GetPumperAsync(region, token, pumperId, cancellationToken);
            var pumps = pumper?.Pumps ?? [];
            var device = ChooseDevice(pumps, config.PumpSerialNumber);
            if (device == null)
            {
                if (pumps.Count > 0 && IsRealSerial(config.PumpSerialNumber))
                {
                    // A configured serial that matches no pump is a misconfiguration, not an empty
                    // account — surface it (with the valid serials) so a typo is diagnosable.
                    var serials = string.Join(", ", pumps.Select(m => m.SerialNumber));
                    _logger.LogError(
                        "[{Source}] Configured pump serial {Serial} not found on account; available: {Serials}",
                        ConnectorSource, config.PumpSerialNumber, serials);
                    result.Success = false;
                    result.Errors.Add(
                        $"Pump serial '{config.PumpSerialNumber}' not found on account (available: {serials})");
                }
                else
                {
                    _logger.LogWarning("[{Source}] No Tandem pumps found on the account", ConnectorSource);
                }

                result.EndTime = DateTimeOffset.UtcNow;
                return result;
            }

            var time = new TandemTimeResolver(config.TimezoneOffset);

            await SyncProfilesAsync(device, activeTypes, result, config, cancellationToken);

            var unclosed = await SyncEventsAsync(
                request, region, pumperId, device, activeTypes, time, result, config, cancellationToken);

            // Assigned here, after everything that can fail: on a run that did, Message is the
            // failure summary the tenant's card reads and must not be a coverage notice instead.
            if (result.Success)
                result.Message = string.Join(" ", new[] { ClampNotice(request, device, time), unclosed }
                    .Where(notice => notice is not null));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[{Source}] Tandem sync canceled", ConnectorSource);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Source}] Error during Tandem sync", ConnectorSource);
            result.Success = false;
            result.Errors.Add($"Sync error: {ex.Message}");
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    private async Task SyncProfilesAsync(
        TandemBffPump device, HashSet<SyncDataType> activeTypes, SyncResult result,
        TandemConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var profile = new TandemProfileMapper(_logger).Map(device.Settings?.Details);
        if (profile == null)
            return;

        await PublishRecordTypeAsync<Nocturne.Core.Models.Profile>(
            result, SyncDataType.Profiles, activeTypes, [profile],
            PublishProfileDataAsync, config, cancellationToken);
    }

    /// <returns>What the run has to say about a span it could not close, or <c>null</c>.</returns>
    private async Task<string?> SyncEventsAsync(
        SyncRequest request, TandemConstants.RegionUrls region, string pumperId, TandemBffPump device,
        HashSet<SyncDataType> activeTypes, TandemTimeResolver time, SyncResult result,
        TandemConnectorConfiguration config, CancellationToken cancellationToken)
    {
        // The pump's newest event is a hard ceiling: there is nothing above it to ask for.
        var ceiling = ParseWallClockUtc(device.MaxDateOfEvents, time) ?? DateTime.UtcNow;
        var end = request.To is { } until && until < ceiling ? until : ceiling;

        var start = await ResolveStartAsync(request, config, device, time);
        if (start >= end)
        {
            _logger.LogInformation(
                "[{Source}] Nothing to sync for device {Device} (start {Start} >= end {End})",
                ConnectorSource, device.AssignmentId, start, end);
            return null;
        }

        // A record at either edge of a bounded window is assembled from, or closed by, events on
        // the far side of it: a bolus's request messages, a basal span's successor, an exercise
        // stop. The fetch reaches a day past each edge so those events are in hand — the pump-logs
        // endpoint is day-granular, so this is one more chunk-day, not a different request shape —
        // and only the window itself is published. An open-ended run already spans the pump's
        // whole range and neither pads nor filters.
        var window = request.To is null
            ? (PublishWindow?)null
            : new PublishWindow(start.Date, end.Date.AddDays(1).AddTicks(-1));
        var fetchFrom = window is null ? start : Later(start.Date.AddDays(-1), FirstEvent(device, time));
        var fetchTo = window is null ? end : Earlier(end.AddDays(1), ceiling);

        // LID_DAILY_BASAL (device status) is not in the backend's default event filter, so the full
        // history log must be requested when device status is enabled — matching tconnectsync.
        var fetchAll = config.FetchAllEventTypes || activeTypes.Contains(SyncDataType.DeviceStatus);
        var eventIdsFilter = fetchAll ? null : TandemConstants.DefaultEventIds;

        var cgm = new TandemCgmMapper(_logger, time);
        var bolus = new TandemBolusMapper(_logger, time);
        var basal = new TandemBasalMapper(_logger, time);
        var deviceEvents = new TandemDeviceEventMapper(_logger, time);
        var systemEvents = new TandemSystemEventMapper(_logger, time);
        var userMode = new TandemUserModeMapper(_logger, time);
        var deviceStatus = new TandemDeviceStatusMapper(_logger, time);

        // Fetch and decode every window first, then map over the full event set. Bolus
        // reassembly (request messages + completion) and sleep/exercise start/stop pairing can
        // straddle a window boundary, so — like tconnectsync, which processes the whole requested
        // range in one pass — the connector must not map each window in isolation. Events that
        // appear in more than one window are deduplicated by their (sequenceGroup, sequenceNumber)
        // identity, and the separately-returned clockChanges are not consumed (matching upstream).
        var allEvents = new List<TandemPumpEvent>();
        var seen = new HashSet<(long, uint)>();
        foreach (var (windowStart, windowEnd) in Chunk(fetchFrom, fetchTo))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await FetchWindowAsync(
                region, pumperId, device.AssignmentId, windowStart, windowEnd, eventIdsFilter,
                config, cancellationToken);
            foreach (var logEvent in response?.Events ?? [])
                if (seen.Add((logEvent.SequenceGroup, logEvent.SequenceNumber)))
                    allEvents.Add(TandemEventDecoder.Decode(logEvent, _logger));
        }

        if (allEvents.Count == 0)
            return null;

        var groups = allEvents
            .Select(e => (Event: e, Class: TandemEventClasses.ForEvent(e)))
            .Where(x => x.Class != null)
            .GroupBy(x => x.Class!.Value, x => x.Event)
            .ToDictionary(g => g.Key, g => g.ToList());

        // What the payload is complete through: the ceiling only when the fetch reached it.
        var fetchedThrough = fetchTo < ceiling ? null : (DateTime?)ceiling;

        return await PublishEventsAsync(
            groups, activeTypes, fetchedThrough, window, cgm, bolus, basal, deviceEvents,
            systemEvents, userMode, deviceStatus, result, config, cancellationToken);
    }

    /// <summary>
    /// The span of a bounded run's own window. The fetch reaches a day either side of it, and those
    /// days' records belong to the neighbouring windows: only a record assembled or closed across
    /// the edge is this run's to publish, and it lands inside. It bounds the record types whose
    /// correctness depends on an event across the edge — spans and the bolus family. A record
    /// complete in one event is published as fetched, so a padded day's own can reach the tenant:
    /// it upserts on a stable id to the same values whichever window carries it.
    /// </summary>
    private readonly record struct PublishWindow(DateTime From, DateTime Through)
    {
        internal bool Holds(DateTime at) => at >= From && at <= Through;
    }

    /// <summary>The oldest event the pump still holds, or <c>null</c> when it does not say.</summary>
    private static DateTime? FirstEvent(TandemBffPump device, TandemTimeResolver time) =>
        ParseWallClockUtc(device.AvailableDataRange?.Start, time);

    private static DateTime Later(DateTime value, DateTime? floor) =>
        floor is { } bound && bound > value ? bound : value;

    private static DateTime Earlier(DateTime value, DateTime ceiling) =>
        value < ceiling ? value : ceiling;

    /// <returns>What the run has to say about a span it could not close, or <c>null</c>.</returns>
    private async Task<string?> PublishEventsAsync(
        IReadOnlyDictionary<TandemEventClass, List<TandemPumpEvent>> groups,
        HashSet<SyncDataType> activeTypes, DateTime? fetchedThrough, PublishWindow? window,
        TandemCgmMapper cgm, TandemBolusMapper bolus, TandemBasalMapper basal,
        TandemDeviceEventMapper deviceEvents, TandemSystemEventMapper systemEvents,
        TandemUserModeMapper userMode, TandemDeviceStatusMapper deviceStatus,
        SyncResult result, TandemConnectorConfiguration config, CancellationToken cancellationToken)
    {
        // The records a padded day can only complete, rather than carry whole: a bolus reassembled
        // from messages either side of the edge, a span closed by an event beyond it. The padded
        // day's own are the neighbouring window's to publish.
        List<T> Inside<T>(List<T> records, Func<T, DateTime> at) =>
            window is { } bounds ? records.Where(record => bounds.Holds(at(record))).ToList() : records;

        if (groups.TryGetValue(TandemEventClass.CgmReading, out var cgmEvents))
            await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
                cgm.Map(cgmEvents), PublishSensorGlucoseDataAsync, config, cancellationToken);

        if (groups.TryGetValue(TandemEventClass.Bolus, out var bolusEvents))
        {
            var decomposed = bolus.Map(bolusEvents);
            await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
                Inside(decomposed.Boluses, record => record.Timestamp),
                PublishBolusDataAsync, config, cancellationToken);
            await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, activeTypes,
                Inside(decomposed.CarbIntakes, record => record.Timestamp),
                PublishCarbIntakeDataAsync, config, cancellationToken);
            await PublishRecordTypeAsync(result, SyncDataType.BolusCalculations, activeTypes,
                Inside(decomposed.BolusCalculations, record => record.Timestamp),
                PublishBolusCalculationDataAsync, config, cancellationToken);
        }

        string? unclosed = null;
        if (groups.TryGetValue(TandemEventClass.Basal, out var basalEvents))
        {
            var spans = basal.Map(basalEvents, fetchedThrough, config.IgnoreZeroUnitBasal);

            // The padded day held no further delivery event, so this span ran longer than the day
            // the fetch reached past the window and is the tenant's to chase, not silently absent.
            if (spans.UnclosedFrom is { } unclosedFrom && window is { } bounds && bounds.Holds(unclosedFrom))
                unclosed =
                    $"The basal span starting {unclosedFrom.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} "
                    + "UTC is not published: the delivery event that ends it is more than a day above the window.";

            await PublishRecordTypeAsync(result, SyncDataType.TempBasals, activeTypes,
                Inside(spans.Spans, record => record.StartTimestamp), PublishTempBasalDataAsync,
                config, cancellationToken);
        }

        var devEvents = Concat(groups, TandemEventClass.Cartridge, TandemEventClass.CgmStartJoinStop,
            TandemEventClass.BasalSuspension, TandemEventClass.BasalResume);
        await PublishRecordTypeAsync(result, SyncDataType.DeviceEvents, activeTypes,
            deviceEvents.Map(devEvents), PublishDeviceEventDataAsync, config, cancellationToken);

        var sysEvents = Concat(groups, TandemEventClass.Alarm, TandemEventClass.CgmAlert);
        await PublishRecordTypeAsync(result, SyncDataType.DeviceEvents, activeTypes,
            systemEvents.Map(sysEvents), PublishSystemEventDataAsync, config, cancellationToken);

        if (groups.TryGetValue(TandemEventClass.UserMode, out var userModeEvents))
            await PublishRecordTypeAsync(result, SyncDataType.StateSpans, activeTypes,
                Inside(userMode.Map(userModeEvents), record => record.StartTimestamp),
                PublishStateSpanDataAsync, config, cancellationToken);

        if (groups.TryGetValue(TandemEventClass.DeviceStatus, out var dailyBasal))
            await PublishRecordTypeAsync(result, SyncDataType.DeviceStatus, activeTypes,
                deviceStatus.Map(dailyBasal), PublishDeviceStatusAsync, config, cancellationToken);

        return unclosed;
    }

    private async Task<TandemPumpLogsResponse?> FetchWindowAsync(
        TandemConstants.RegionUrls region, string pumperId, string deviceAssignmentId,
        DateTime windowStart, DateTime windowEnd, int[]? eventIdsFilter,
        TandemConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
        if (string.IsNullOrEmpty(token))
            return null;

        return await ExecuteWithRetryAsync(
            () => _apiClient.GetPumpLogsAsync(
                region, token!, pumperId, deviceAssignmentId, windowStart, windowEnd, eventIdsFilter, cancellationToken),
            _retryDelayStrategy,
            async () =>
            {
                _tokenProvider.InvalidateToken();
                token = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
                return !string.IsNullOrEmpty(token);
            },
            maxRetries: config.MaxRetryAttempts,
            operationName: "FetchPumpEvents",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Resolves the start of the sync window from the caller's bound and the pump's own resume
    /// point — the earliest catch-up point across glucose and treatments, so no active data type is
    /// missed — never earlier than the pump's first event, which is where <see cref="ClampNotice"/>
    /// reports a bound below.
    /// </summary>
    private async Task<DateTime> ResolveStartAsync(
        SyncRequest request, TandemConnectorConfiguration config, TandemBffPump device,
        TandemTimeResolver time)
    {
        var glucoseSince = await CalculateSinceTimestampAsync(config);
        var treatmentSince = await CalculateTreatmentSinceTimestampAsync(config);

        var candidates = new[] { glucoseSince, treatmentSince }.Where(d => d.HasValue).Select(d => d!.Value).ToList();
        var resume = candidates.Count > 0 ? candidates.Min() : DefaultInitialSyncFloor();

        // The pump's own range is how far back this source goes, and the floor a range naming no
        // lower bound resolves to — named, rather than left to the clamp below to arrive at.
        var first = FirstEvent(device, time);
        var start = ResumeFrom(request, resume, first ?? DefaultInitialSyncFloor());

        return Later(start, first);
    }

    /// <summary>
    /// What the run says when it covered less than it was asked for: the pump serves nothing before
    /// its available range begins, and a success over a range that was silently narrowed reads the
    /// same as one that covered it.
    /// </summary>
    private static string? ClampNotice(
        SyncRequest request, TandemBffPump device, TandemTimeResolver time) =>
        FirstEvent(device, time) is { } first && request.From < first
            ? $"The pump holds no data before {first.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC; "
              + "the sync started there."
            : null;

    /// <summary>
    /// Selects the pump to follow: the one matching the configured serial number, or — when none is
    /// configured — the pump with the most recent events, skipping pumps that have never uploaded
    /// (null <c>maxDateOfEvents</c>) and falling back to the first pump when none has events.
    /// Mirrors tconnectsync's ChooseDevice.
    /// </summary>
    internal static TandemBffPump? ChooseDevice(
        IReadOnlyList<TandemBffPump> pumps, string? serialNumber)
    {
        if (pumps.Count == 0)
            return null;

        if (IsRealSerial(serialNumber))
            return pumps.FirstOrDefault(m =>
                string.Equals(m.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));

        // maxDateOfEvents values share the pump's timezone, so ordering the naive wall-clock
        // values directly is equivalent to ordering their UTC conversions.
        return pumps
            .Where(m => ParseWallClock(m.MaxDateOfEvents) != null)
            .OrderByDescending(m => ParseWallClock(m.MaxDateOfEvents)!.Value)
            .FirstOrDefault() ?? pumps[0];
    }

    /// <summary>Parses a naive pump-local BFF timestamp, or null when absent/unparseable.</summary>
    private static DateTime? ParseWallClock(string? value) =>
        DateTime.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>Parses a naive pump-local BFF timestamp and converts it to UTC via the configured offset.</summary>
    private static DateTime? ParseWallClockUtc(string? value, TandemTimeResolver time) =>
        ParseWallClock(value) is { } wallClock ? time.ToUtc(wallClock) : null;

    /// <summary>
    /// Whether a configured serial actually selects a pump. Empty/whitespace means "no preference",
    /// and "11111111" is tconnectsync's sentinel for the same.
    /// </summary>
    private static bool IsRealSerial(string? serial) =>
        !string.IsNullOrWhiteSpace(serial) && serial != "11111111";

    private static List<TandemPumpEvent> Concat(
        IReadOnlyDictionary<TandemEventClass, List<TandemPumpEvent>> groups, params TandemEventClass[] classes) =>
        classes
            .Select(groups.GetValueOrDefault)
            .Where(list => list != null)
            .SelectMany(list => list!)
            .ToList();

    /// <summary>
    /// Splits the range into inclusive day-granular windows no larger than the pump-logs endpoint's
    /// ~4-week cap, mirroring tconnectsync's <c>_pump_log_windows</c>. Bounds are dates: the API
    /// expands them to T00:00:00Z–T23:59:59Z, so adjacent windows do not overlap.
    /// </summary>
    private static IEnumerable<(DateTime Start, DateTime End)> Chunk(DateTime start, DateTime end)
    {
        var cursor = start.Date;
        var last = end.Date;
        while (cursor <= last)
        {
            var windowEnd = cursor.AddDays(TandemConstants.PumpLogsWindowDays - 1);
            if (windowEnd > last)
                windowEnd = last;
            yield return (cursor, windowEnd);
            cursor = windowEnd.AddDays(1);
        }
    }
}
