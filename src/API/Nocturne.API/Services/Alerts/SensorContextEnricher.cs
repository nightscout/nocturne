using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Lazy populator for <see cref="SensorContext"/> optional fields. Walks the rules being
/// evaluated this pass once, decides which optional fields need real values, and only fetches
/// data the rules will actually consult.
/// </summary>
/// <remarks>
/// Why lazy: a tenant whose only enabled rule is a glucose threshold should not pay for an
/// IOB calculation, a treatments query, a predictions call, and a device-events lookup on
/// every reading. Each branch in <see cref="EnrichAsOfAsync"/> is gated on the corresponding
/// flag in <see cref="DataNeedsSet"/>.
///
/// Treatments are fetched once and shared between IOB and COB when both are needed — they
/// are the most expensive shared dependency.
///
/// <see cref="IPredictionService"/> is resolved through the service provider so the alert
/// engine continues to function in deployments where prediction is not configured (the type
/// is registered conditionally based on <c>PredictionOptions</c>).
///
/// Live and replay paths share a single internal dispatch; <see cref="EnrichAsync"/>
/// passes "now" as the as-of timestamp, and <c>EnrichAsOfAsync</c> passes the replay tick.
/// Replay skips the prediction service (intrinsically forward-looking) and the active-alerts
/// repo fetch (the replay walker maintains its own running set across the window and threads
/// it in via <see cref="SensorContext.ActiveAlerts"/>).
///
/// Data-source dependencies are bundled into <see cref="SensorContextEnricherDependencies"/>
/// so this constructor stays focused on orchestration concerns (service provider for the
/// optional prediction service, time, logging) rather than plumbing.
/// </remarks>
internal sealed class SensorContextEnricher : ISensorContextEnricher
{
    /// <summary>How far back <see cref="FetchRecentTreatmentsAsync"/> reads. IOB and COB come
    /// from <see cref="IIobCobReader"/>.</summary>
    private const int TreatmentLookbackHours = 24;

    /// <summary>Predictions are produced at fixed intervals from "now"; this is the cadence
    /// used by both AID device-status uploads and the oref WASM curve.</summary>
    private const int PredictionIntervalMinutes = 5;

    private readonly SensorContextEnricherDependencies _deps;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SensorContextEnricher> _logger;

    public SensorContextEnricher(
        SensorContextEnricherDependencies deps,
        IServiceProvider serviceProvider,
        TimeProvider timeProvider,
        ILogger<SensorContextEnricher> logger)
    {
        _deps = deps;
        _serviceProvider = serviceProvider;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<SensorContext> EnrichAsync(
        SensorContext baseContext,
        IEnumerable<AlertRuleSnapshot> rules,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return EnrichInternalAsync(baseContext, rules, now, isReplay: false, ct);
    }

    /// <inheritdoc/>
    public Task<SensorContext> EnrichAsOfAsync(
        SensorContext baseContext,
        IEnumerable<AlertRuleSnapshot> rules,
        DateTime asOf,
        CancellationToken ct)
        => EnrichInternalAsync(baseContext, rules, asOf, isReplay: true, ct);

    /// <summary>
    /// Single dispatch path used by both <see cref="EnrichAsync"/> and <see cref="EnrichAsOfAsync"/>.
    /// <paramref name="now"/> is the time every fetch is pinned to (the live clock for the
    /// orchestrator path, the replay tick for replay). <paramref name="isReplay"/> toggles
    /// branches that don't have a sensible historical answer (predictions, active alerts).
    /// </summary>
    private async Task<SensorContext> EnrichInternalAsync(
        SensorContext baseContext,
        IEnumerable<AlertRuleSnapshot> rules,
        DateTime now,
        bool isReplay,
        CancellationToken ct)
    {
        var needs = RuleDataNeeds.Walk(rules, _logger);
        var enriched = baseContext;

        // Read uncached and calculated here, not through IIobCobReader.ReadTotalAsync: its live
        // reading can be a minute old, older than the treatment that triggered this evaluation.
        if (needs.NeedsIob || needs.NeedsCob)
        {
            var inputs = await ReadIobCobInputsAsync(now, ct);
            if (inputs is not null)
            {
                if (needs.NeedsIob)
                    enriched = enriched with { IobUnits = await ComputeIobAsync(now, inputs, live: !isReplay, ct) };
                if (needs.NeedsCob)
                    enriched = enriched with { CobGrams = await ComputeCobAsync(now, inputs, live: !isReplay, ct) };
            }
        }

        if (needs.NeedsPredicted)
        {
            // Live: the prediction service anchors at its own UtcNow. Replay: we pin the
            // forecast to the tick so a `predicted` rule sees the curve the user would have
            // had at that moment (oref runs against treatments/glucose ≤ tick, profile
            // resolved at tick, oref's `currentTimeMillis` set to tick).
            var asOf = isReplay
                ? new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc))
                : (DateTimeOffset?)null;
            var (predictions, curves) = await FetchPredictionsAsync(asOf, ct);
            enriched = enriched with { Predictions = predictions, PredictionCurves = curves };
        }

        if (needs.NeedsReservoir)
        {
            var estimate = await _deps.Reservoir.GetEstimateAsync(isReplay ? now : null, ct);
            enriched = enriched with
            {
                ReservoirUnits = estimate?.Units,
                ReservoirIsLowerBound = estimate?.IsLowerBound ?? false,
            };
        }

        if (needs.NeedsSiteAge)
        {
            var lastSiteChange = (await _deps.DeviceEvents.GetLatestOfAnyTypeAsync(
                ConsumableTrackerSpecs.EventTypes(LegacyConsumableKind.Site), isReplay ? now : null, ct))?.Timestamp;
            enriched = enriched with { LastSiteChangeAt = lastSiteChange };
        }

        if (needs.NeedsSensorAge)
        {
            var lastSensorStart = await FetchLatestEventAsync(DeviceEventType.SensorStart, isReplay ? now : null, ct);
            enriched = enriched with { LastSensorStartAt = lastSensorStart };
        }

        if (needs.NeedsActiveAlerts)
        {
            // Live: pull the canonical snapshot from the alert repo. Replay: the walker
            // computes its own running tally across the replay window and threads it into
            // baseContext.ActiveAlerts before each tick — preserve that as-is.
            if (!isReplay)
            {
                var activeAlerts = await _deps.Alerts.GetActiveAlertSnapshotsAsync(ct);
                enriched = enriched with { ActiveAlerts = activeAlerts };
            }
        }

        var pumpModeSpans = new Lazy<Task<PumpModeSpans>>(() => ReadPumpModeSpansAsync(now, isReplay, ct));
        enriched = await EnrichLoopingFactsAsync(enriched, needs, now, isReplay, pumpModeSpans, ct);
        enriched = await EnrichPhase2FactsAsync(enriched, needs, now, pumpModeSpans, ct);
        if (!isReplay && pumpModeSpans.IsValueCreated)
            enriched = enriched with { PumpModesRead = (await pumpModeSpans.Value).Read };

        // DND state is fetched unconditionally — engine-level suppression in the orchestrator
        // applies to every rule regardless of whether its tree references the do_not_disturb
        // condition fact. Gating on a NeedsDoNotDisturb walker flag would silently exempt
        // every typical glucose/threshold rule from suppression, which is the opposite of
        // what users expect. The lookups are one indexed row from `tenant_alert_settings` plus
        // the tenant's uncleared `dnd_windows` (partial-index scan) per evaluation pass — cheap
        // enough to make unconditional. Runs after Phase 2 so `enriched.TenantTimeZoneId`
        // (populated from the canonical PatientRecord) is available to interpret the scheduled
        // DND window.
        if (!isReplay)
        {
            // Force the timezone fetch even when no other leaf demands it — scheduled DND
            // needs the patient's tz to interpret window hours, and Phase 2 only fetches
            // when NeedsTenantTimeZone is set.
            if (!needs.NeedsTenantTimeZone && string.IsNullOrEmpty(enriched.TenantTimeZoneId))
            {
                try
                {
                    var tz = await _deps.TherapySettings.GetTimezoneAsync(ct: ct);
                    enriched = enriched with { TenantTimeZoneId = tz };
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Failed to resolve tenant timezone for DND scheduling; scheduled window will fall back to UTC");
                }
            }

            var settings = await _deps.AlertSettings.GetAsync(ct);
            // No row yet means DND has never been configured for this tenant — treat as off.
            // Scheduled DND is tenant-wide, i.e. == scope=all (manual DND is a scope=all window,
            // resolved below); this projects the active scheduled window if any (ADR 0004 D5).
            var scheduled = settings?.Resolve(now, enriched.TenantTimeZoneId);

            // Scoped DND windows + scheduled DND, folded into the active scope set and the
            // tenant-wide projection by the shared resolver — the same call replay makes (there
            // receipt-gated), so the live gate and replay cannot drift on how a window resolves.
            // The read's bound is stated on IDndWindowRepository.GetUnexpiredSnapshotsAsync.
            var windows = await _deps.DndWindowRepository.GetUnexpiredSnapshotsAsync(now, ct);
            var dnd = DndWindowResolver.Resolve(windows, now, receiptGated: false, scheduled);

            enriched = enriched with
            {
                ActiveDoNotDisturb = dnd.ActiveDoNotDisturb,
                ActiveDndScopes = dnd.Scopes,
            };
        }

        return enriched;
    }

    /// <summary>
    /// Populates the Phase-2 leaf inputs (glucose bucket, last-carb/bolus, tenant timezone,
    /// pump-mode and generic state-span snapshots) when their corresponding needs flags are set.
    /// </summary>
    private async Task<SensorContext> EnrichPhase2FactsAsync(
        SensorContext baseCtx, DataNeedsSet needs, DateTime now,
        Lazy<Task<PumpModeSpans>> pumpModeSpans, CancellationToken ct)
    {
        var enriched = baseCtx;

        // Tenant timezone must be resolved before the glucose-bucket branch because the
        // schedule lookup converts `now` (UTC) into the tenant's local wall-clock to pick
        // the right TargetRangeEntry. RuleDataNeeds co-sets NeedsTenantTimeZone whenever
        // NeedsGlucoseBucket is set, so a tenant whose only rule needs the bucket still
        // pays for this lookup — but it's a single cached settings read.
        if (needs.NeedsTenantTimeZone)
        {
            try
            {
                var tz = await _deps.TherapySettings.GetTimezoneAsync(ct: ct);
                enriched = enriched with { TenantTimeZoneId = tz };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Failed to resolve tenant timezone; time-of-day/day-of-week/glucose-bucket evaluators will fall back to UTC");
            }
        }

        // The target-range entry in effect places the glucose bucket and is the target_low and
        // target_high a limit can name, so both read one lookup and cannot disagree. With no entry
        // the bucket falls back to clinical defaults and the targets stay absent.
        var bucketReading = needs.NeedsGlucoseBucket ? enriched.LatestValue : null;
        if (needs.NeedsTargetRange || bucketReading is not null)
        {
            var entry = await ResolveTargetEntryAsync(now, enriched.TenantTimeZoneId, ct);
            if (needs.NeedsTargetRange && entry is not null)
                enriched = enriched with { TargetLow = entry.Low.ToDecimal(), TargetHigh = entry.High.ToDecimal() };
            if (bucketReading is { } latestValue)
                enriched = enriched with { GlucoseBucket = BucketOf(latestValue, entry) };
        }

        // Treatments — fetch once and project last-carb/last-bolus timestamps.
        if (needs.NeedsTreatments)
        {
            var treatments = await FetchRecentTreatmentsAsync(now, ct);
            DateTime? lastCarbAt = null;
            DateTime? lastBolusAt = null;
            foreach (var t in treatments)
            {
                if (t.Mills <= 0) continue;
                var at = t.Mills.ToUtcDateTime();
                if (t.Carbs is > 0 && (lastCarbAt is null || at > lastCarbAt))
                    lastCarbAt = at;
                if (t.Insulin is > 0 && (lastBolusAt is null || at > lastBolusAt))
                    lastBolusAt = at;
            }
            enriched = enriched with { LastCarbAt = lastCarbAt, LastBolusAt = lastBolusAt };
        }

        if (needs.NeedsPumpModes)
        {
            enriched = enriched with { ActivePumpModes = ActivePumpModes((await pumpModeSpans.Value).Sent) };
        }

        if (needs.ReferencedStateSpans.Count > 0)
        {
            var dict = new Dictionary<(StateSpanCategory, string?), StateSpanSnapshot>(needs.ReferencedStateSpans.Count);
            foreach (var key in needs.ReferencedStateSpans)
            {
                var startedAt = key.Category == StateSpanCategory.Profile
                    ? await ActiveProfileStartAsync(key.State, now, ct)
                    : (await _deps.StateSpans.GetActiveAtAsync(key.Category, key.State, now, ct))?.StartTimestamp;
                if (startedAt is { } start)
                {
                    dict[key] = new StateSpanSnapshot(key.Category, key.State, start);
                }
            }
            enriched = enriched with { ActiveStateSpans = dict };
        }

        if (needs.NeedsSleepSession)
        {
            enriched = enriched with { SleepSessionActive = await IsSleepSessionActiveAsync(now, ct) };
        }

        if (needs.ReferencedTrackerDefinitions.Count > 0)
        {
            // One query for the whole referenced set. `now` is the live clock for
            // orchestrator/sweep passes and the replay tick for replay, so the same
            // fetch answers "active at this instant" in both paths.
            var references = await _deps.Trackers.GetActiveTrackerReferencesAsync(
                needs.ReferencedTrackerDefinitions, now, ct);
            enriched = enriched with { ActiveTrackers = references };
        }

        return enriched;
    }

    /// <summary>
    /// Returns true when a sleep session has <c>StartTime &lt;= now &lt;= EndTime</c>. The sleep
    /// service's range filter maps <c>from</c> to <c>EndTime &gt;= from</c> and <c>to</c> to
    /// <c>StartTime &lt;= to</c>, so passing <paramref name="now"/> as both bounds selects exactly
    /// the sessions overlapping the instant. A query failure is treated as "not active" (logged
    /// and swallowed), matching the silent fail-mode of the other enrichment branches.
    /// </summary>
    private async Task<bool> IsSleepSessionActiveAsync(DateTime now, CancellationToken ct)
    {
        try
        {
            var sessions = await _deps.Sleep.GetSessionsAsync(from: now, to: now, limit: 1, cancellationToken: ct);
            return sessions.Any();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to resolve active sleep session for alert evaluation; treating as inactive");
            return false;
        }
    }

    /// <summary>
    /// The target-range entry in effect at <paramref name="at"/>: the active profile's
    /// <see cref="Nocturne.Core.Models.V4.TargetRangeSchedule"/> version then, at the local time of day
    /// in <paramref name="tenantTimeZoneId"/> (IANA or Windows-mapped, from
    /// <see cref="SensorContext.TenantTimeZoneId"/>; null or unparseable reads as UTC). Null when the
    /// profile has no schedule or its schedule has no entries.
    /// </summary>
    private async Task<TargetRangeEntry?> ResolveTargetEntryAsync(
        DateTime at, string? tenantTimeZoneId, CancellationToken ct)
    {
        var profileName = await _deps.ActiveProfileResolver.GetProfileNameAsync(at.ToMills(), ct);
        // Before the store's first version, its newest, as ProfileVersionCache.ActiveAt reads it.
        var schedule = await _deps.TargetRangeSchedules.GetActiveAtAsync(profileName, at, ct)
            ?? (await _deps.TargetRangeSchedules.GetByProfileNameAsync(profileName, ct)).MaxBy(s => s.Timestamp);
        if (schedule is null)
            return null;

        var zone = string.IsNullOrEmpty(tenantTimeZoneId) ? null : TimeZoneHelper.GetTimeZoneInfoFromId(tenantTimeZoneId);
        var secondsFromMidnight = ScheduleEntryTime.SecondsOfDay(at.ToMills(), zone);
        return ScheduleEntryTime.TryEntryAt(
            schedule.Entries, secondsFromMidnight, e => ScheduleEntryTime.SecondsOf(e.TimeAsSeconds, e.Time), out var active)
            ? active
            : null;
    }

    /// <summary>
    /// The bucket of <paramref name="glucoseMgdl"/> by <paramref name="entry"/>'s boundaries, or with
    /// no entry by the consensus in-range band and clinical bucket defaults.
    /// </summary>
    private static GlucoseBucket BucketOf(decimal glucoseMgdl, TargetRangeEntry? entry) =>
        entry is null
            ? GlucoseBucketResolver.Compute(
                glucoseMgdl,
                GlucoseConstants.TargetBottomMgdl.ToDecimal(),
                GlucoseConstants.TargetTopMgdl.ToDecimal(),
                null,
                null,
                null)
            : GlucoseBucketResolver.Compute(
                glucoseMgdl,
                entry.Low.ToDecimal(),
                entry.High.ToDecimal(),
                entry.VeryLow,
                entry.TightHigh,
                entry.VeryHigh);

    /// <summary>
    /// Populates the looping-related facts (APS cycle/enaction/sensitivity, pump status and
    /// suspension, temp basal, uploader, override) when their corresponding needs flags are
    /// set. Extracted from <see cref="EnrichInternalAsync"/> to keep that method focused on
    /// dispatch — this section grew to ~90 LOC of fetch-and-project once cold-start flags
    /// and freshness gating landed.
    /// </summary>
    private async Task<SensorContext> EnrichLoopingFactsAsync(
        SensorContext baseCtx, DataNeedsSet needs, DateTime now, bool isReplay,
        Lazy<Task<PumpModeSpans>> pumpModeSpans, CancellationToken ct)
    {
        var enriched = baseCtx;

        // For live calls (isReplay=false) the asOf parameter is null — repos return the
        // absolute latest. For replay we pin to `now` (the replay tick). The HasEver* flag
        // is computed from the same as-of-bounded result, so a fact only "exists" if it was
        // observed by `now`; future facts must not leak backwards into the replay window.
        DateTime? asOf = isReplay ? now : null;

        if (needs.NeedsLastApsCycle || needs.NeedsLastApsEnacted || needs.NeedsSensitivityRatio)
        {
            if (needs.NeedsLastApsCycle)
            {
                var t = await _deps.ApsSnapshots.GetLatestTimestampAsOfAsync(asOf, ct);
                enriched = enriched with { LastApsCycleAt = t, HasEverApsCycled = t.HasValue };
            }
            if (needs.NeedsLastApsEnacted)
            {
                var t = await _deps.ApsSnapshots.GetLatestEnactedTimestampAsync(asOf, ct);
                enriched = enriched with { LastApsEnactedAt = t };
            }
            if (needs.NeedsSensitivityRatio)
            {
                var s = await _deps.ApsSnapshots.GetLatestSensitivityRatioAsync(asOf, ct);
                enriched = enriched with { SensitivityRatio = s, HasEverApsSensitivity = s.HasValue };
            }
        }

        if (needs.NeedsApsDetermination)
        {
            // At any age: the engine judges the determination's freshness (engine-semantics.md §3).
            var latest = (await _deps.ApsSnapshots.GetAsync(
                from: null, to: asOf, device: null, source: null, limit: 1, offset: 0, descending: true, ct: ct))
                .FirstOrDefault();
            enriched = enriched with { ApsDetermination = latest is null ? null : ApsDeterminations.Of(latest) };
        }

        if (needs.NeedsPumpStatus)
        {
            var pump = await _deps.PumpSnapshots.GetLatestAsync(asOf, ct);
            if (pump is not null)
            {
                enriched = enriched with
                {
                    PumpBatteryPercent = pump.BatteryPercent.HasValue ? (decimal?)pump.BatteryPercent.Value : null,
                    HasEverPumpSnapshot = true,
                };

                // Freshness gate: only project active pump-suspension when the underlying pump
                // snapshot is itself fresh — prevents suspension state latching after the
                // uploader goes offline.
                // Strictly less-than: a snapshot exactly at the threshold is treated as stale,
                // biasing toward "unknown" rather than "still suspended" at the edge of upload timing.
                var pumpFresh = (now - pump.Timestamp) < _deps.Options.Value.PumpFreshnessThreshold;
                if (pumpFresh)
                {
                    var span = OpenSuspension((await pumpModeSpans.Value).Sent);
                    if (span is not null)
                    {
                        enriched = enriched with
                        {
                            ActivePumpSuspension = new PumpSuspensionSnapshot(span.StartTimestamp)
                        };
                    }
                }
            }
        }

        if (needs.NeedsTempBasal)
        {
            var temp = await _deps.TempBasals.GetActiveAtAsync(now, ct);
            if (temp is { Rate: { } rate })
            {
                enriched = enriched with { ActiveTempBasal = ProjectTempBasal(temp, rate) };
            }
        }

        if (needs.NeedsUploaderStatus)
        {
            var uploader = await _deps.UploaderSnapshots.GetLatestAsync(asOf, ct);
            if (uploader is not null)
            {
                enriched = enriched with
                {
                    UploaderBatteryPercent = uploader.Battery.HasValue ? (decimal?)uploader.Battery.Value : null,
                    HasEverUploaderSnapshot = true,
                };
            }
        }

        if (needs.NeedsTransmitterStatus)
        {
            var latest = await _deps.UploaderSnapshots.GetLatestOfTypeAsync(TransmitterStatus.UploaderType, asOf, ct);
            if (TransmitterStatus.From(latest) is { } transmitter)
            {
                enriched = enriched with
                {
                    TransmitterVoltageA = transmitter.VoltageA,
                    TransmitterVoltageB = transmitter.VoltageB,
                    TransmitterResistance = transmitter.Resistance,
                    TransmitterDays = transmitter.Days,
                };
            }
        }

        if (needs.NeedsOverride)
        {
            var span = await _deps.StateSpans.GetActiveAtAsync(
                StateSpanCategory.Override, state: null, at: now, ct);
            if (!isReplay)
            {
                var deviceStatus = await DeviceStatusOverridesAsync(now, ct);
                span ??= StartingAhead(deviceStatus, now).MinBy(s => s.StartTimestamp);
                enriched = enriched with
                {
                    OverridesRead = new SpansRead(now, deviceStatus.Select(s => (DateTime?)s.StartTimestamp).Max()),
                };
            }
            if (span is not null)
                enriched = enriched with { ActiveOverride = new OverrideSnapshot(span.StartTimestamp) };
        }

        if (needs.NeedsSensorState)
            enriched = await WithReportedSensorStateAsync(enriched, now, ct);

        return enriched;
    }

    /// <summary>
    /// The sensor state the newest recent <see cref="Nocturne.Core.Models.V4.SensorStateReport"/> of the newest reading's source states
    /// (<see cref="SensorStateReports"/>), with the instant the engine stops counting it. A state other
    /// than ok replaces the <see cref="SensorState.NoValue"/> the base context may carry for a newest
    /// reading with no value, since it says why; an ok one does not. A context whose reading source
    /// lacks a device or a data source reads no report.
    /// </summary>
    private async Task<SensorContext> WithReportedSensorStateAsync(
        SensorContext context, DateTime now, CancellationToken ct)
    {
        if (context.LatestSource is not { Device: { Length: > 0 } device, DataSource: { Length: > 0 } dataSource })
            return context;
        var newest = await _deps.SensorStates.GetNewestAsync(
            device, dataSource, now - SensorStateReports.MaxAge, now, AlertReadings.Bound(now), ct);
        return SensorStateReports.StateOf(newest) is var (reported, until)
               && (reported != SensorState.Ok || context.SensorState is null)
            ? context with { SensorState = new SensorStateSnapshot(reported, Since: null, until) }
            : context;
    }

    private static TempBasalSnapshot ProjectTempBasal(TempBasal t, double rate) =>
        new(
            Rate: rate.ToDecimal(),
            ScheduledRate: t.ScheduledRate?.ToDecimal(),
            StartedAt: t.StartTimestamp);

    /// <summary>
    /// Fetches treatments within the IOB/COB lookback window ending at <paramref name="now"/>.
    /// Uses <c>ITreatmentService.GetTreatmentsByRangeAsync</c> so the read is bounded by
    /// <c>[now - 24h, now]</c> directly at the data layer — replay paths land on the correct
    /// historical window regardless of how many newer treatments the tenant has logged after
    /// <paramref name="now"/>.
    /// </summary>
    private async Task<List<Treatment>> FetchRecentTreatmentsAsync(DateTime now, CancellationToken ct)
    {
        var nowMills = now.ToMills();
        var cutoffMills = nowMills - (TreatmentLookbackHours * 60L * 60L * 1000L);

        var treatments = await _deps.Treatments.GetTreatmentsByRangeAsync(cutoffMills, nowMills, ct);
        return treatments.ToList();
    }

    private async Task<IobCobInputs?> ReadIobCobInputsAsync(DateTime now, CancellationToken ct)
    {
        try
        {
            return await _deps.IobCob.ReadInputsAsync(now.ToMills(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to read treatments for IOB and COB for alert evaluation; leaving both null");
            return null;
        }
    }

    // Both anchored at `now`, the replay tick on replay: a decay anchored at the wall clock would
    // put every bolus of a past tick beyond DIA.
    private async Task<decimal?> ComputeIobAsync(DateTime now, IobCobInputs inputs, bool live, CancellationToken ct)
    {
        try
        {
            var result = await _deps.Iob.CalculateTotalAsync(
                inputs.Boluses, inputs.TempBasals, now.ToMills(), live: live, ct: ct);
            _logger.LogDebug(
                "IOB compute @ {Now}: boluses={BolusCount}, tempBasals={TempBasalCount}, result.Iob={Iob}, source={Source}",
                now, inputs.Boluses.Count, inputs.TempBasals.Count, result.Iob, result.Source ?? "(none)");
            return result.Iob.ToDecimal();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to compute IOB for alert evaluation; leaving null");
            return null;
        }
    }

    private async Task<decimal?> ComputeCobAsync(DateTime now, IobCobInputs inputs, bool live, CancellationToken ct)
    {
        try
        {
            var result = await _deps.Cob.CalculateTotalAsync(
                inputs.CarbIntakes, inputs.Boluses, inputs.TempBasals, now.ToMills(), live: live, ct: ct);
            _logger.LogDebug(
                "COB compute @ {Now}: carbIntakes={CarbCount}, result.Cob={Cob}, source={Source}",
                now, inputs.CarbIntakes.Count, result.Cob, result.Source ?? "(none)");
            return result.Cob.ToDecimal();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to compute COB for alert evaluation; leaving null");
            return null;
        }
    }

    private static readonly (IReadOnlyList<PredictedGlucosePoint>, IReadOnlyDictionary<ForecastCurve, IReadOnlyList<PredictedGlucosePoint>>)
        NoPredictions = (Array.Empty<PredictedGlucosePoint>(), new Dictionary<ForecastCurve, IReadOnlyList<PredictedGlucosePoint>>());

    /// <summary>
    /// The default series the prediction service returns and every named curve it returns, each
    /// under its name with the offsets the default has always had (engine-semantics.md §4).
    /// </summary>
    private async Task<(IReadOnlyList<PredictedGlucosePoint> Default, IReadOnlyDictionary<ForecastCurve, IReadOnlyList<PredictedGlucosePoint>> Curves)>
        FetchPredictionsAsync(DateTimeOffset? asOf, CancellationToken ct)
    {
        // Optional dependency — registered conditionally based on PredictionOptions.
        // GetService<> returns null when unavailable (PredictionSource.None or DI not wired).
        var predictionService = _serviceProvider.GetService<IPredictionService>();
        if (predictionService is null)
            return NoPredictions;

        GlucosePredictionResponse response;
        try
        {
            response = await predictionService.GetPredictionsAsync(asOf: asOf, cancellationToken: ct);
        }
        catch (InvalidOperationException ex)
        {
            // Documented "no readings available" path — silent empty mirrors the leaf evaluators.
            _logger.LogWarning(ex, "Prediction service had insufficient data; returning empty predictions");
            return NoPredictions;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Prediction service failed; returning empty predictions");
            return NoPredictions;
        }

        var interval = response.IntervalMinutes > 0 ? response.IntervalMinutes : PredictionIntervalMinutes;
        var curves = new (ForecastCurve Name, IReadOnlyList<double>? Values)[]
        {
            (ForecastCurve.Iob, response.Predictions.IobOnly),
            (ForecastCurve.Cob, response.Predictions.Cob),
            (ForecastCurve.Uam, response.Predictions.Uam),
            (ForecastCurve.Zt, response.Predictions.ZeroTemp),
        };
        if (response.Algorithm is { } algorithm && LoopHealthCalculator.IsOref(algorithm))
            curves = UntrimmedOrefCurves(curves);
        var named = new Dictionary<ForecastCurve, IReadOnlyList<PredictedGlucosePoint>>();
        foreach (var (name, curve) in curves)
        {
            if (curve is { Count: > 0 })
                named[name] = Points(curve, interval);
        }
        var main = response.Predictions.Default;
        return (main is { Count: > 0 } ? Points(main, interval) : Array.Empty<PredictedGlucosePoint>(), named);
    }

    /// <summary>
    /// The named oref curves, each one oref shortened carried out to the longest by
    /// <see cref="OrefCurveTails"/>, so a <c>min_of</c> or <c>max_of</c> reads them as far as any
    /// goes (engine-semantics.md §4). <c>default</c> is never touched.
    /// </summary>
    private static (ForecastCurve Name, IReadOnlyList<double>? Values)[] UntrimmedOrefCurves(
        (ForecastCurve Name, IReadOnlyList<double>? Values)[] curves)
    {
        var longest = curves.Max(c => c.Values?.Count ?? 0);
        return curves
            .Select(c => c.Values is { Count: > 0 } values
                && (c.Name == ForecastCurve.Zt ? values.Count < longest : OrefCurveTails.IsLevelTrimmed(values.Count, longest))
                    ? (c.Name, (IReadOnlyList<double>?)OrefCurveTails.HoldLast(values, longest))
                    : c)
            .ToArray();
    }

    private static IReadOnlyList<PredictedGlucosePoint> Points(IReadOnlyList<double> curve, int interval)
    {
        var points = new List<PredictedGlucosePoint>(curve.Count);
        for (var i = 0; i < curve.Count; i++)
        {
            // Curve index 0 is "now"; first forward step is index 1. Offset minutes are measured
            // from the current reading; the engine filters by WithinMinutes.
            var offsetMinutes = (i + 1) * interval;
            points.Add(new PredictedGlucosePoint(offsetMinutes, curve[i].ToDecimal()));
        }
        return points;
    }

    private async Task<DateTime?> FetchLatestEventAsync(DeviceEventType eventType, DateTime? asOf, CancellationToken ct)
    {
        var evt = await _deps.DeviceEvents.GetLatestByEventTypeAsync(eventType, asOf, ct);
        return evt?.Timestamp;
    }

    /// <summary>
    /// The pump-mode spans the facts read at <paramref name="now"/>: those open at it, and live, per mode with
    /// none open, the soonest open span starting no later than <see cref="AlertReadings.Bound"/> after it, as
    /// the engine-semantics.md section 5.4 Host paragraph states. The ahead spans are a separate, bounded read,
    /// so no span beyond the bound can use up the cap of the current one or push the one sent out of its page.
    /// Read once for every pump-mode fact. A live read also learns the newest start among the spans not ended
    /// before <paramref name="now"/>, open or closed, sent or not, from a third read of that span alone.
    /// </summary>
    private async Task<PumpModeSpans> ReadPumpModeSpansAsync(DateTime now, bool isReplay, CancellationToken ct)
    {
        var current = (await _deps.StateSpans.GetStateSpansAsync(
                StateSpanCategory.PumpMode, from: now, to: now, cancellationToken: ct))
            .Where(s => s.EndTimestamp is null || s.EndTimestamp > now)
            .ToList();
        if (isReplay)
            return new PumpModeSpans(current, null);

        var ahead = (await _deps.StateSpans.GetStateSpansAsync(
                StateSpanCategory.PumpMode, from: now, to: AlertReadings.Bound(now), active: true, cancellationToken: ct))
            .ToList();
        var newestStored = (await _deps.StateSpans.GetStateSpansAsync(
                StateSpanCategory.PumpMode, from: now, count: 1, cancellationToken: ct))
            .ToList();
        var covered = current.Select(s => s.State).ToHashSet(StringComparer.Ordinal);
        var sent = (IReadOnlyList<StateSpan>)
        [
            .. current,
            .. StartingAhead(ahead, now)
                .Where(s => !covered.Contains(s.State))
                .GroupBy(s => s.State, StringComparer.Ordinal)
                .Select(g => g.MinBy(s => s.StartTimestamp)!),
        ];
        var newest = newestStored.FirstOrDefault()?.StartTimestamp;
        return new PumpModeSpans(sent, new SpansRead(now, newest));
    }

    /// <summary>
    /// The overrides a device status wrote that have not ended before <paramref name="now"/>, open or with an
    /// end after their start. A fast uploader clock dates one ahead: the soonest no later than
    /// <see cref="AlertReadings.Bound"/> after <paramref name="now"/> is sent, and one further ahead is reported.
    /// An override written any other way is a plan, and stays held back until its time. Read with the copies
    /// deduplication hides, since Loop reports one override as a treatment and as a device status, and the
    /// treatment copy can be the group's primary. A device status exists only when the uploader reports the
    /// override running, so its copy being hidden does not make the override a plan.
    /// </summary>
    private async Task<IReadOnlyList<StateSpan>> DeviceStatusOverridesAsync(DateTime now, CancellationToken ct) =>
        [
            .. (await _deps.StateSpans.GetAllCopiesAsync(StateSpanCategory.Override, from: now, to: null, cancellationToken: ct))
                .Where(s => s.Metadata.IsFromDeviceStatus() && (s.EndTimestamp is null || s.EndTimestamp > s.StartTimestamp)),
        ];

    private static IEnumerable<StateSpan> StartingAhead(IEnumerable<StateSpan> spans, DateTime now) =>
        spans.Where(s => s.StartTimestamp > now && s.StartTimestamp <= AlertReadings.Bound(now));

    /// <param name="Sent">The spans the facts read.</param>
    /// <param name="Read">What a live read saw of the stored spans; null for a replay read.</param>
    private sealed record PumpModeSpans(IReadOnlyList<StateSpan> Sent, SpansRead? Read);

    /// <summary>
    /// Every span <see cref="ReadPumpModeSpansAsync"/> sent, its state as written. Spans of different modes
    /// overlap (a Suspended span runs on top of an Automatic or Manual one), so none is chosen over another; the
    /// engine reads the entry for the mode each leaf names.
    /// </summary>
    private static IReadOnlyList<PumpModeSnapshot> ActivePumpModes(IReadOnlyList<StateSpan> spans) =>
        spans
            .Where(s => s.State is { Length: > 0 })
            .OrderBy(s => s.StartTimestamp)
            .ThenBy(s => s.State, StringComparer.Ordinal)
            .Select(s => new PumpModeSnapshot(s.State!, s.StartTimestamp))
            .ToList();

    private static StateSpan? OpenSuspension(IReadOnlyList<StateSpan> spans) =>
        spans
            .Where(s => string.Equals(s.State, nameof(PumpModeState.Suspended), StringComparison.Ordinal))
            .MaxBy(s => s.StartTimestamp);

    /// <summary>
    /// When the profile activation in force at <paramref name="now"/> started, for a
    /// <see cref="StateSpanCategory.Profile"/> leaf. Its state matches any activation when null or
    /// <see cref="ProfileState.Active"/>, the one state a profile span carried; otherwise it names the
    /// profile.
    /// </summary>
    private async Task<DateTime?> ActiveProfileStartAsync(string? state, DateTime now, CancellationToken ct)
    {
        var nowMills = now.ToMills();
        var resolved = await _deps.ProfileActivations.GetAtAsync(nowMills, ct);
        if (resolved is null)
            return null;

        var matches = state is null
            || string.Equals(state, nameof(ProfileState.Active), StringComparison.Ordinal)
            || string.Equals(state, resolved.ProfileName, StringComparison.Ordinal);
        return matches ? resolved.Activation.StartTimestamp : null;
    }
}
