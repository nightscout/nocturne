using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services.DeviceStatuses;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Decomposes legacy <see cref="DeviceStatus"/> records into typed v4 snapshot tables.
/// Extracts APS (<see cref="V4Models.ApsSnapshot"/> for OpenAPS/AAPS/Trio and Loop), pump
/// (<see cref="V4Models.PumpSnapshot"/>), and uploader (<see cref="V4Models.UploaderSnapshot"/>)
/// snapshots, and persists them with idempotent create-or-update via <c>LegacyId</c> matching.
/// Device override state is folded into one <see cref="StateSpanCategory.Override"/> span per
/// override period.
/// </summary>
/// <seealso cref="IDeviceStatusDecomposer"/>
internal class DeviceStatusDecomposer : DecomposerBase, IDeviceStatusDecomposer
{
    private readonly IApsSnapshotRepository _apsRepo;
    private readonly IPumpSnapshotRepository _pumpRepo;
    private readonly IUploaderSnapshotRepository _uploaderRepo;
    private readonly IDeviceStatusExtrasRepository _extrasRepo;
    private readonly IStateSpanService _stateSpanService;
    private readonly IDeviceService _deviceService;
    private readonly IAuditContext _auditContext;
    private readonly ILoopClosedLoopStamper _closedLoop;
    private readonly TimeProvider _time;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public DeviceStatusDecomposer(
        IApsSnapshotRepository apsRepo,
        IPumpSnapshotRepository pumpRepo,
        IUploaderSnapshotRepository uploaderRepo,
        IDeviceStatusExtrasRepository extrasRepo,
        IStateSpanService stateSpanService,
        IDeviceService deviceService,
        IAuditContext auditContext,
        ILoopClosedLoopStamper closedLoop,
        TimeProvider timeProvider,
        ILogger<DeviceStatusDecomposer> logger)
        : base(logger)
    {
        _apsRepo = apsRepo;
        _pumpRepo = pumpRepo;
        _uploaderRepo = uploaderRepo;
        _extrasRepo = extrasRepo;
        _stateSpanService = stateSpanService;
        _deviceService = deviceService;
        _auditContext = auditContext;
        _closedLoop = closedLoop;
        _time = timeProvider;
    }

    /// <summary>
    /// The source-aware overload with <c>source: null</c>, for a write that carries no connector data
    /// source.
    /// </summary>
    public Task<V4Models.DecompositionResult> DecomposeAsync(DeviceStatus ds, WriteOrigin origin, CancellationToken ct = default)
        => DecomposeAsync(ds, source: null, origin, ct);

    /// <inheritdoc />
    public async Task<V4Models.DecompositionResult> DecomposeAsync(DeviceStatus ds, string? source, WriteOrigin origin, CancellationToken ct = default)
    {
        NormalizeMills(ds);

        var correlationId = CorrelationIdFor(ds.Id, await GetStoredCorrelationIdsAsync([ds.Id], SnapshotTables, ct));
        return await DecomposeCoreAsync(ds, source, correlationId, group: null, origin, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The group is found by its stored id and rewritten row by row, so a status stored without a
    /// legacy id (connector statuses keyed on their sync key, V4-native snapshots) is updated in place
    /// instead of being inserted a second time. Such a group takes the ObjectId its anchor already has
    /// on the wire as its legacy id, which keeps the status's identifier stable even when the update
    /// adds or drops the anchoring snapshot. The removals are system-attributed so they do not hold
    /// the legacy id against a later update that brings the section back.
    /// </remarks>
    public async Task<V4Models.DecompositionResult?> ReplaceAsync(
        string storedId, DeviceStatus ds, WriteOrigin origin, CancellationToken ct = default)
    {
        if (await LoadStoredGroupAsync(storedId, ct) is not { } group)
            return null;

        NormalizeMills(ds);
        ds.Id = group.LegacyId;

        using (SystemAttributedBatchWrites(_auditContext))
        {
            if (group.Aps is { } aps && ds.OpenAps == null && ds.Loop == null)
                await _apsRepo.DeleteAsync(aps.Id, origin, ct);
            if (group.Pump is { } pump && ds.Pump == null)
                await _pumpRepo.DeleteAsync(pump.Id, origin, ct);
            if (group.Uploader is { } uploader && ds.Uploader == null && !ds.UploaderBattery.HasValue)
                await _uploaderRepo.DeleteAsync(uploader.Id, origin, ct);
            await _extrasRepo.DeleteByCorrelationIdAsync(group.CorrelationId, ct);
            await RetractOverrideAsync(group, ct);
        }

        return await DecomposeAsync(
            [new BatchStatus(ds, group.LegacyId, group.CorrelationId, ResolveTimestamp(ds))], source: null, group, origin, ct);
    }

    private async Task<V4Models.DecompositionResult> DecomposeCoreAsync(
        DeviceStatus ds, string? source, Guid correlationId, StoredGroup? group,
        WriteOrigin origin, CancellationToken ct)
    {
        var legacyId = ds.Id;
        var result = new V4Models.DecompositionResult { CorrelationId = correlationId };
        var statusMills = ResolveStatusMills(ds);

        Guid? pumpDeviceId = null;

        if (ds.Pump != null)
        {
            pumpDeviceId = await DecomposePumpAsync(ds, legacyId, source, statusMills, group, result, origin, ct);
        }

        if (ds.Cgm != null)
        {
            await RegisterCgmDeviceAsync(ds, statusMills, ct);
        }

        var apsAttempted = false;
        if (MapToApsSnapshot(ds, legacyId, source, result.CorrelationId) is { } apsModel)
        {
            apsAttempted = true;
            await UpsertApsSnapshotAsync(legacyId, apsModel, pumpDeviceId, statusMills, group, result, origin, ct);
            await StampLoopAsync(result, ct);
        }

        if (ds.Uploader != null || ds.UploaderBattery.HasValue)
        {
            await DecomposeUploaderAsync(ds, legacyId, source, statusMills, group, result, origin, ct);
        }

        if (ds.Override is { Active: not null })
        {
            var stored = result.StoredRecords.OfType<V4Models.IV4Record>();
            await DecomposeOverrideAsync(ds, legacyId ?? GroupKey(stored), result, ct);
        }

        var snapshotAttempted = ds.Pump != null || apsAttempted || ds.Uploader != null || ds.UploaderBattery.HasValue;
        var snapshotStored = result.StoredRecords
            .Any(r => r is V4Models.ApsSnapshot or V4Models.PumpSnapshot or V4Models.UploaderSnapshot);

        // See the held-snapshot filter in DecomposeBatchAsync.
        if (!snapshotAttempted || snapshotStored)
            await DecomposeExtrasAsync(ds, result, origin, ct);

        return result;
    }

    /// <inheritdoc />
    /// <remarks>Mirrors the pump, <see cref="MapToApsSnapshot"/> and uploader conditions in
    /// <see cref="DecomposeAsync(DeviceStatus, string?, WriteOrigin, CancellationToken)"/>.</remarks>
    public bool HasLegacyKeyedSnapshot(DeviceStatus ds)
        => ds.Pump != null || ds.OpenAps != null || ds.Loop != null
            || ds.Uploader != null || ds.UploaderBattery.HasValue;

    /// <summary>AAPS sends <c>date</c> instead of <c>mills</c>; normalize before decomposition.</summary>
    private static void NormalizeMills(DeviceStatus ds)
    {
        if (ds.Mills == 0 && ds.Date is > 0)
            ds.Mills = ds.Date.Value;
    }

    #region APS Decomposition

    private async Task UpsertApsSnapshotAsync(
        string? legacyId, V4Models.ApsSnapshot model, Guid? pumpDeviceId, long statusMills,
        StoredGroup? group, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
    {
        model.DeviceId = pumpDeviceId;
        if (group is not null)
            (model.DataSource, model.SyncIdentifier) = (group.Aps?.DataSource, group.Aps?.SyncIdentifier);

        await UpsertByLegacyIdAsync(
            _apsRepo, legacyId, model, result, origin, ct,
            beforeWrite: async existing =>
            {
                model.PatientDeviceId =
                    await _deviceService.ResolvePatientDeviceAsync(pumpDeviceId, statusMills, ct)
                    ?? existing?.PatientDeviceId;
            },
            findStored: group is null ? null : () => Task.FromResult(group.Aps));
    }

    private Task StampLoopAsync(V4Models.DecompositionResult result, CancellationToken ct) =>
        _closedLoop.AfterSnapshotsWrittenAsync(
            result.StoredRecords.OfType<V4Models.ApsSnapshot>(), ct);

    #endregion

    #region Pump Decomposition

    /// <summary>
    /// The pump-device identity key. Only the CareLink connector supplies a real pump serial today;
    /// preferring serial for other sources would re-key existing devices that newly start reporting
    /// <c>pump.serial</c>, orphaning their history. So the serial preference is gated to CareLink
    /// (identified by its device-name prefix); every other source keeps the model-as-key behavior.
    /// </summary>
    private static string? PumpDeviceKey(DeviceStatus ds) =>
        ds.Device?.StartsWith("CareLink", StringComparison.OrdinalIgnoreCase) == true
            ? ds.Pump?.Serial ?? ds.Pump?.Model
            : ds.Pump?.Model;

    /// <summary>
    /// The patient-device link is left to the caller: only the single path has a stored
    /// attribution to fall back to when the re-resolution comes back null.
    /// </summary>
    private async Task<V4Models.PumpSnapshot> BuildPumpSnapshotAsync(
        DeviceStatus ds, string? legacyId, string? source, long statusMills, Guid? correlationId, CancellationToken ct)
    {
        var model = MapToPumpSnapshot(ds, legacyId, source, correlationId);

        model.DeviceId = await _deviceService.ResolveAsync(
            V4Models.DeviceCategory.InsulinPump,
            ds.Pump?.Manufacturer,
            PumpDeviceKey(ds),
            statusMills, ct);

        return model;
    }

    private async Task<Guid?> DecomposePumpAsync(
        DeviceStatus ds, string? legacyId, string? source, long statusMills, StoredGroup? group,
        V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
    {
        var model = await BuildPumpSnapshotAsync(ds, legacyId, source, statusMills, result.CorrelationId, ct);
        if (group is not null)
            (model.DataSource, model.SyncIdentifier) = (group.Pump?.DataSource, group.Pump?.SyncIdentifier);

        var upserted = await UpsertByLegacyIdAsync(
            _pumpRepo, legacyId, model, result, origin, ct,
            beforeWrite: async existing =>
            {
                model.PatientDeviceId =
                    await _deviceService.ResolvePatientDeviceAsync(model.DeviceId, statusMills, ct)
                    ?? existing?.PatientDeviceId;
            },
            findStored: group is null ? null : () => Task.FromResult(group.Pump));

        // No stored snapshot to transition from when the write was refused.
        if (upserted is ({ } persisted, _))
        {
            await DecomposePumpSuspensionAsync(ds, persisted, result, origin, ct);
            await DecomposePumpModeAsync(ds, persisted, result, origin, ct);
        }

        return model.DeviceId;
    }

    /// <summary>
    /// Detects pump-suspension transitions and emits/closes a
    /// <see cref="StateSpanCategory.PumpMode"/> / <see cref="PumpModeState.Suspended"/> state span.
    /// </summary>
    /// <remarks>
    /// <para>Compares the just-upserted <see cref="V4Models.PumpSnapshot"/> against the most-recent
    /// prior snapshot strictly before its timestamp. On a <c>false → true</c> transition (or first
    /// observation with <c>Suspended == true</c>), opens a new span. On <c>true → false</c>, closes
    /// the open span. Equal-state comparisons are no-ops.</para>
    /// <para>First observation: when there is no prior snapshot, opening on
    /// <c>Suspended == true</c> anchors the span at the first observed timestamp — there is no
    /// transition signal to anchor on otherwise.</para>
    /// <para>Idempotency: the open span carries a deterministic
    /// <c>OriginalId = "pump-suspended:{snapshotId}"</c> so re-decomposing the same legacy
    /// <see cref="DeviceStatus"/> will upsert (not duplicate) the row.</para>
    /// <para>Assumes a single insulin pump per tenant — the open-span lookup does not filter by
    /// <c>Source</c>, so a second pump's resume could close a first pump's open span. Out of scope
    /// per the alerting model (one tenant = one diabetic person).</para>
    /// </remarks>
    private async Task DecomposePumpSuspensionAsync(
        DeviceStatus ds,
        V4Models.PumpSnapshot newSnapshot,
        V4Models.DecompositionResult result,
        WriteOrigin origin, CancellationToken ct)
    {
        var prior = await _pumpRepo.GetLatestBeforeAsync(newSnapshot.Timestamp, ct);
        var priorSuspended = prior?.Suspended ?? false;
        var nowSuspended = newSnapshot.Suspended ?? false;

        if (priorSuspended == nowSuspended)
            return;

        // Prefer pump's own clock for the transition timestamp; fall back to ingestion timestamp.
        var transitionAt = ParseTimestampToDateTime(newSnapshot.Clock) ?? newSnapshot.Timestamp;

        if (!priorSuspended && nowSuspended)
        {
            // Guard against duplicate open spans. An uploader can emit two device statuses for the
            // same suspend event sharing the SAME ingest Timestamp but different pump clocks (e.g.
            // Trio uploads paired snapshots). Because GetLatestBeforeAsync uses a strict `<` on
            // Timestamp, the second such snapshot cannot see its sibling as prior and reads
            // prior=not-suspended — a spurious false→true transition. Opening a second span here
            // leaves an orphan the single resume can't fully close, latching the pump "suspended"
            // indefinitely. If a Suspended span is already open, this observation is already
            // covered; do nothing.
            var existingOpen = await _stateSpanService.GetStateSpansAsync(
                category: StateSpanCategory.PumpMode,
                state: PumpModeState.Suspended.ToString(),
                active: true,
                count: 1,
                cancellationToken: ct);

            if (existingOpen.Any())
            {
                Logger.LogDebug(
                    "Skipped opening duplicate PumpMode/Suspended StateSpan for snapshot {SnapshotId}; a suspension is already active",
                    newSnapshot.Id);
                return;
            }

            var span = new StateSpan
            {
                Category = StateSpanCategory.PumpMode,
                State = PumpModeState.Suspended.ToString(),
                StartTimestamp = transitionAt,
                EndTimestamp = null,
                Source = ds.Device,
                OriginalId = $"pump-suspended:{newSnapshot.Id}",
            };

            var upserted = await _stateSpanService.UpsertStateSpanAsync(span, ct);
            result.CreatedRecords.Add(upserted);
            Logger.LogDebug(
                "Opened PumpMode/Suspended StateSpan for snapshot {SnapshotId} (legacy {LegacyId})",
                newSnapshot.Id, newSnapshot.LegacyId);
        }
        else // priorSuspended && !nowSuspended
        {
            // Close ALL active Suspended spans, not just one: leftover duplicates (created before
            // the open-guard above, or by concurrent decomposition) must all be closed on resume,
            // otherwise an orphan keeps the pump latched "suspended".
            var openSpans = (await _stateSpanService.GetStateSpansAsync(
                category: StateSpanCategory.PumpMode,
                state: PumpModeState.Suspended.ToString(),
                active: true,
                count: int.MaxValue,
                cancellationToken: ct)).ToList();

            if (openSpans.Count == 0)
            {
                // No open span exists — the suspended=true state predates the StateSpan feature
                // or the opening snapshot was never decomposed. Create a retroactive closed span
                // anchored at the prior snapshot's timestamp so the suspension timeline is complete.
                if (prior is null)
                {
                    Logger.LogWarning(
                        "PumpMode/Suspended transition true→false detected but no prior snapshot or open StateSpan (snapshot {SnapshotId})",
                        newSnapshot.Id);
                    return;
                }

                var retroactiveStart = ParseTimestampToDateTime(prior.Clock) ?? prior.Timestamp;
                var backfilled = new StateSpan
                {
                    Category = StateSpanCategory.PumpMode,
                    State = PumpModeState.Suspended.ToString(),
                    StartTimestamp = retroactiveStart,
                    EndTimestamp = transitionAt,
                    Source = ds.Device,
                    OriginalId = $"pump-suspended:{prior.Id}",
                };

                var upserted = await _stateSpanService.UpsertStateSpanAsync(backfilled, ct);
                result.CreatedRecords.Add(upserted);
                Logger.LogInformation(
                    "Backfilled closed PumpMode/Suspended StateSpan from prior snapshot {PriorSnapshotId} to {EndTimestamp} (resume snapshot {SnapshotId})",
                    prior.Id, transitionAt, newSnapshot.Id);
                return;
            }

            foreach (var openSpan in openSpans)
            {
                openSpan.EndTimestamp = transitionAt;
                var closed = await _stateSpanService.UpsertStateSpanAsync(openSpan, ct);
                result.UpdatedRecords.Add(closed);
                Logger.LogDebug(
                    "Closed PumpMode/Suspended StateSpan {SpanId} at {EndTimestamp}",
                    openSpan.Id, transitionAt);
            }
        }
    }

    /// <summary>
    /// Detects closed-loop mode transitions and maintains <see cref="StateSpanCategory.PumpMode"/> /
    /// <see cref="PumpModeState.Automatic"/> vs <see cref="PumpModeState.Manual"/> state spans.
    /// </summary>
    /// <remarks>
    /// <para>No-op unless the snapshot carries a <see cref="V4Models.PumpSnapshot.PumpMode"/> signal —
    /// only connectors that observe the pump's algorithm state (currently CareLink) populate it, so
    /// every other source leaves it null and emits no spans.</para>
    /// <para>Compares the just-upserted snapshot's mode against the most-recent prior snapshot
    /// (strictly earlier). On a change (or first observation), closes any open span of the opposite
    /// mode and opens one for the new mode. Steady-state (equal modes) is a no-op, so the open span
    /// spans the whole period rather than fragmenting per snapshot.</para>
    /// <para>Independent of the Suspended span machinery: both share the <c>PumpMode</c> category but
    /// are filtered by <see cref="StateSpan.State"/>, so an Automatic/Manual span and a Suspended span
    /// may legitimately overlap.</para>
    /// <para>Idempotency: the open span carries a deterministic
    /// <c>OriginalId = "pump-mode:{snapshotId}"</c>, and the open-span guard prevents duplicates when
    /// the same device status is re-decomposed.</para>
    /// </remarks>
    private async Task DecomposePumpModeAsync(
        DeviceStatus ds,
        V4Models.PumpSnapshot newSnapshot,
        V4Models.DecompositionResult result,
        WriteOrigin origin, CancellationToken ct)
    {
        var newMode = ParsePumpMode(newSnapshot.PumpMode);
        if (newMode is null)
            return;

        var prior = await _pumpRepo.GetLatestBeforeAsync(newSnapshot.Timestamp, ct);
        var priorMode = ParsePumpMode(prior?.PumpMode);

        if (priorMode == newMode)
            return;

        // Prefer pump's own clock for the transition timestamp; fall back to ingestion timestamp.
        var transitionAt = ParseTimestampToDateTime(newSnapshot.Clock) ?? newSnapshot.Timestamp;

        // Close any open span for the opposite Automatic/Manual mode. Skip spans that start after this
        // transition so re-decomposing an older snapshot can't invert a newer span's range.
        var oppositeMode = newMode == PumpModeState.Automatic ? PumpModeState.Manual : PumpModeState.Automatic;
        var openOpposite = (await _stateSpanService.GetStateSpansAsync(
            category: StateSpanCategory.PumpMode,
            state: oppositeMode.ToString(),
            active: true,
            count: int.MaxValue,
            cancellationToken: ct)).ToList();

        foreach (var openSpan in openOpposite)
        {
            if (openSpan.StartTimestamp > transitionAt)
                continue;

            openSpan.EndTimestamp = transitionAt;
            var closed = await _stateSpanService.UpsertStateSpanAsync(openSpan, ct);
            result.UpdatedRecords.Add(closed);
            Logger.LogDebug(
                "Closed PumpMode/{Mode} StateSpan {SpanId} at {EndTimestamp}",
                oppositeMode, openSpan.Id, transitionAt);
        }

        // Open a span for the new mode unless one is already open (idempotent re-decomposition).
        var existingOpen = await _stateSpanService.GetStateSpansAsync(
            category: StateSpanCategory.PumpMode,
            state: newMode.Value.ToString(),
            active: true,
            count: 1,
            cancellationToken: ct);

        if (existingOpen.Any())
            return;

        var span = new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = newMode.Value.ToString(),
            StartTimestamp = transitionAt,
            EndTimestamp = null,
            Source = ds.Device,
            OriginalId = $"pump-mode:{newSnapshot.Id}",
        };

        var upserted = await _stateSpanService.UpsertStateSpanAsync(span, ct);
        result.CreatedRecords.Add(upserted);
        Logger.LogDebug(
            "Opened PumpMode/{Mode} StateSpan for snapshot {SnapshotId} (legacy {LegacyId})",
            newMode.Value, newSnapshot.Id, newSnapshot.LegacyId);
    }

    /// <summary>
    /// Parses a stored pump-mode string into the Automatic/Manual subset of <see cref="PumpModeState"/>
    /// that this decomposer tracks. Returns null for absent or out-of-scope states (e.g. Suspended,
    /// which is owned by <see cref="DecomposePumpSuspensionAsync"/>).
    /// </summary>
    internal static PumpModeState? ParsePumpMode(string? value)
    {
        if (TryParseDefinedName<PumpModeState>(value, out var mode)
            && mode is PumpModeState.Automatic or PumpModeState.Manual)
            return mode;
        return null;
    }

    #endregion

    #region CGM Registration

    /// <summary>
    /// Registers the CGM sensor in the device registry. The CGM has no dedicated snapshot table —
    /// only the canonical <see cref="V4Models.Device"/> is upserted (stamping first/last seen) so the
    /// sensor shows up as an in-use device. No-op unless the connector populated manufacturer +
    /// model/serial (<see cref="IDeviceService.ResolveAsync"/> returns null when either is missing).
    /// </summary>
    private async Task RegisterCgmDeviceAsync(DeviceStatus ds, long statusMills, CancellationToken ct)
    {
        await _deviceService.ResolveAsync(
            V4Models.DeviceCategory.CGM,
            ds.Cgm?.Manufacturer,
            ds.Cgm?.Serial ?? ds.Cgm?.Model,
            statusMills, ct);
    }

    #endregion

    #region Uploader Decomposition

    private async Task<V4Models.UploaderSnapshot> BuildUploaderSnapshotAsync(
        DeviceStatus ds, string? legacyId, string? source, long statusMills, Guid? correlationId, CancellationToken ct)
    {
        var model = MapToUploaderSnapshot(ds, legacyId, source, correlationId);

        model.DeviceId = await _deviceService.ResolveAsync(
            V4Models.DeviceCategory.Uploader,
            ds.Uploader?.Name,
            ds.Uploader?.Type ?? "unknown",
            statusMills, ct);

        return model;
    }

    private async Task DecomposeUploaderAsync(
        DeviceStatus ds, string? legacyId, string? source, long statusMills, StoredGroup? group,
        V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
    {
        var model = await BuildUploaderSnapshotAsync(ds, legacyId, source, statusMills, result.CorrelationId, ct);
        if (group is not null)
            (model.DataSource, model.SyncIdentifier) = (group.Uploader?.DataSource, group.Uploader?.SyncIdentifier);

        await UpsertByLegacyIdAsync(
            _uploaderRepo, legacyId, model, result, origin, ct,
            findStored: group is null ? null : () => Task.FromResult(group.Uploader));
    }

    #endregion

    #region Override Decomposition

    private const int OverrideQueryLimit = 10;

    private static StateSpan BuildOverrideSpan(DeviceStatus ds, DateTime timestamp, string? legacyId)
    {
        DateTime? end = ds.Override!.Duration is > 0
            ? (ParseTimestampToDateTime(ds.Override.Timestamp) ?? timestamp)
                .AddSeconds(ds.Override.Duration.Value)
            : null;
        return new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = OverrideState.Custom.ToString(),
            StartTimestamp = timestamp,
            // The span starts at the status time, so an end already past it cannot be stored as-is.
            EndTimestamp = end > timestamp ? end : null,
            Source = ds.Device,
            OriginalId = legacyId,
            Metadata = BuildOverrideMetadata(ds.Override),
        };
    }

    /// <summary>
    /// A span stays open until a contrary observation or its declared end.
    /// </summary>
    private async Task DecomposeOverrideAsync(
        DeviceStatus ds, string? legacyId, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var at = ResolveTimestamp(ds);
        var observed = ds.Override!.Active == true
            ? BuildOverrideMetadata(ds.Override)
            : null;

        bool Unchanged(StateSpan span) =>
            observed is not null
            && (!IsUploaderSpan(span, ds.Device)
                ? span.Metadata.IsSameOverrideAs(observed)
                : span.Metadata is { } metadata
                  && metadata.TryReadString("name") == observed.TryReadString("name")
                  && metadata.OverrideScaleFactor() == observed.OverrideScaleFactor());

        var covered = false;
        for (var skip = 0; ; skip += OverrideQueryLimit)
        {
            // A span closed at `at` still contains `at`, so closing one does not shift later pages.
            var containing = (await _stateSpanService.GetStateSpansAsync(
                category: StateSpanCategory.Override,
                from: at,
                to: at,
                count: OverrideQueryLimit,
                skip: skip,
                cancellationToken: ct)).ToList();

            foreach (var span in containing)
            {
                // Of two conflicting observations at one instant, the first decomposed wins.
                if (Unchanged(span) || span.StartTimestamp >= at)
                {
                    covered |= IsUploaderSpan(span, ds.Device);
                    continue;
                }

                if (span.EndTimestamp == at)
                    continue;

                span.EndTimestamp = at;
                if (await _stateSpanService.UpdateStateSpanAsync(span.Id!, span, ct) is { } closed)
                    result.UpdatedRecords.Add(closed);
                Logger.LogDebug("Closed Override StateSpan {SpanId} at {EndTimestamp}", span.Id, at);
            }

            if (containing.Count < OverrideQueryLimit)
                break;
        }

        if (observed is null || covered)
            return;

        // Strict bound in memory, as in FirstApsBetweenAsync.
        var next = (await _stateSpanService.GetStateSpansStartingFromAsync(
            StateSpanCategory.Override, ds.Device, at, OverrideQueryLimit, ct))
            .FirstOrDefault(s => s.StartTimestamp > at && IsUploaderSpan(s, ds.Device));

        var contraryAt = (await FirstApsBetweenAsync(ds.Device, at, next?.StartTimestamp, legacyId, ct))?.Timestamp;

        var opened = BuildOverrideSpan(ds, at, legacyId);

        if (next is not null
            && contraryAt is null
            && Unchanged(next)
            && !(opened.EndTimestamp < next.StartTimestamp))
        {
            StartAt(next, at, legacyId);
            if (await _stateSpanService.UpdateStateSpanAsync(next.Id!, next, ct) is { } extended)
                result.UpdatedRecords.Add(extended);
            return;
        }

        var firstLater = contraryAt ?? next?.StartTimestamp;
        if (firstLater < (opened.EndTimestamp ?? DateTime.MaxValue))
            opened.EndTimestamp = firstLater;

        var upserted = await _stateSpanService.UpsertStateSpanAsync(opened, ct);
        result.CreatedRecords.Add(upserted);
        Logger.LogDebug("Opened Override StateSpan from device status {LegacyId}", legacyId);
    }

    private async Task<V4Models.ApsSnapshot?> FirstApsBetweenAsync(
        string? device, DateTime after, DateTime? before, string? excludedKey, CancellationToken ct)
    {
        for (var offset = 0; ; offset += OverrideQueryLimit)
        {
            // Strict bounds in memory: Npgsql truncates to microseconds, so `after` + 1 tick matches `after`.
            var page = (await _apsRepo.GetAsync(
                from: after,
                to: before,
                device: device,
                source: null,
                limit: OverrideQueryLimit,
                offset: offset,
                descending: false,
                ct: ct)).ToList();
            var found = page.FirstOrDefault(a => a.Timestamp > after
                                                 && a.Timestamp < (before ?? DateTime.MaxValue)
                                                 && StoredKey(a) != excludedKey);
            if (found is not null || page.Count < OverrideQueryLimit)
                return found;
        }
    }

    /// <summary>
    /// Whether the span is the uploader's own devicestatus span rather than a treatment override
    /// whose source happens to equal the device.
    /// </summary>
    private static bool IsUploaderSpan(StateSpan span, string? device) =>
        span.Source == device
        && span.Metadata.TryReadString(StateSpanMetadataExtensions.CollectionKey)
            != StateSpanMetadataExtensions.TreatmentsCollection;

    /// <summary>
    /// Moves a span's start to a status and keys the span to that status. Upserts match on
    /// OriginalId, so a span keyed to a status inside it would be overwritten when that status's
    /// replacement opens a span of its own.
    /// </summary>
    private static void StartAt(StateSpan span, DateTime start, string? key)
    {
        span.StartTimestamp = start;
        span.OriginalId = key ?? span.OriginalId;
    }

    #endregion

    #region Extras Decomposition

    /// <summary>
    /// The sub-objects and unknown top-level keys with no typed snapshot of their own, or
    /// <see langword="null"/> when the device status carries none.
    /// </summary>
    private V4Models.DeviceStatusExtras? BuildExtras(DeviceStatus ds, Guid correlationId)
    {
        var extras = new Dictionary<string, object?>();

        if (ds.XDripJs != null)
            extras["xdripjs"] = ds.XDripJs;
        if (ds.RadioAdapter != null)
            extras["radioAdapter"] = ds.RadioAdapter;
        if (ds.Connect != null)
            extras["connect"] = ds.Connect;
        if (ds.Cgm != null)
            extras["cgm"] = ds.Cgm;
        if (ds.Meter != null)
            extras["meter"] = ds.Meter;
        if (ds.InsulinPen != null)
            extras["insulinPen"] = ds.InsulinPen;
        if (ds.MmTune != null)
            extras["mmtune"] = ds.MmTune;
        // RileyLinks live on the Loop object, which is already fully serialized into
        // ApsSnapshot.LoopJson when ds.Loop is present — no need to duplicate here.

        // Capture unknown top-level keys from JSON deserialization
        if (ds.ExtensionData != null)
        {
            foreach (var kvp in ds.ExtensionData)
                extras[kvp.Key] = kvp.Value;
        }

        if (extras.Count == 0)
            return null;

        return new V4Models.DeviceStatusExtras
        {
            CorrelationId = correlationId,
            Timestamp = ResolveTimestamp(ds),
            Extras = extras,
        };
    }

    private async Task DecomposeExtrasAsync(
        DeviceStatus ds, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
    {
        if (result.CorrelationId is not { } correlationId || BuildExtras(ds, correlationId) is not { } model)
            return;

        V4Models.DeviceStatusExtras created;
        try
        {
            created = await _extrasRepo.CreateAsync(model, origin, ct);
        }
        catch (RecreationBlockedException)
        {
            Logger.LogDebug(
                "Skipped DeviceStatusExtras for correlation {CorrelationId}: its identity is already held",
                correlationId);
            return;
        }

        result.CreatedRecords.Add(created);
        Logger.LogDebug("Created DeviceStatusExtras with {Count} keys for correlation {CorrelationId}",
            model.Extras!.Count, correlationId);
    }

    #endregion

    #region Replacement

    /// <summary>
    /// The live snapshots of one stored device status, and the legacy id and correlation id a
    /// replacement writes them under. A replacement keeps each snapshot's stored sync identity, so a
    /// connector's later re-sync still matches the row rather than inserting beside it.
    /// </summary>
    private sealed record StoredGroup(
        string LegacyId,
        Guid CorrelationId,
        DateTime Timestamp,
        V4Models.ApsSnapshot? Aps,
        V4Models.PumpSnapshot? Pump,
        V4Models.UploaderSnapshot? Uploader);

    /// <summary>
    /// Resolves <paramref name="storedId"/>, the id a projected status carries (its legacy id, else its
    /// anchor's uuid), to the snapshots stored for it. A sibling is found under the anchor's legacy id
    /// first, so a group whose correlation ids have forked still converges on one row per table.
    /// </summary>
    private async Task<StoredGroup?> LoadStoredGroupAsync(string storedId, CancellationToken ct)
    {
        V4Models.IV4Record? anchor = await _apsRepo.GetByLegacyIdAsync(storedId, ct)
            ?? (V4Models.IV4Record?)await _pumpRepo.GetByLegacyIdAsync(storedId, ct)
            ?? await _uploaderRepo.GetByLegacyIdAsync(storedId, ct);
        if (anchor is null && Guid.TryParse(storedId, out var uuid))
        {
            anchor = await _apsRepo.GetByIdAsync(uuid, ct)
                ?? (V4Models.IV4Record?)await _pumpRepo.GetByIdAsync(uuid, ct)
                ?? await _uploaderRepo.GetByIdAsync(uuid, ct);
        }

        if (anchor is null)
            return null;

        var correlationId = anchor.CorrelationId is { } stored && stored != Guid.Empty
            ? stored
            : Guid.CreateVersion7();

        return new StoredGroup(
            StoredKey(anchor),
            correlationId,
            anchor.Timestamp,
            anchor as V4Models.ApsSnapshot
                ?? await FindSiblingAsync(_apsRepo, _apsRepo.GetByCorrelationIdsAsync, anchor, ct),
            anchor as V4Models.PumpSnapshot
                ?? await FindSiblingAsync(_pumpRepo, _pumpRepo.GetByCorrelationIdsAsync, anchor, ct),
            anchor as V4Models.UploaderSnapshot
                ?? await FindSiblingAsync(_uploaderRepo, _uploaderRepo.GetByCorrelationIdsAsync, anchor, ct));
    }

    private static async Task<TRecord?> FindSiblingAsync<TRecord>(
        ILegacyKeyedRepository<TRecord> repository,
        Func<IEnumerable<Guid>, CancellationToken, Task<IEnumerable<TRecord>>> byCorrelationIds,
        V4Models.IV4Record anchor,
        CancellationToken ct)
        where TRecord : class, V4Models.IV4Record
    {
        if (anchor.LegacyId is { } legacyId && await repository.GetByLegacyIdAsync(legacyId, ct) is { } byLegacyId)
            return byLegacyId;

        return anchor.CorrelationId is { } correlationId && correlationId != Guid.Empty
            ? (await byCorrelationIds([correlationId], ct)).FirstOrDefault()
            : null;
    }

    /// <summary>
    /// The id a stored status is addressed by: its anchoring snapshot's legacy id, else the ObjectId
    /// that snapshot's uuid gives on the wire. The anchor is the one the projection picks.
    /// </summary>
    private static string StoredKey(V4Models.IV4Record anchor) => anchor.LegacyId ?? MongoObjectId.FromGuid(anchor.Id);

    /// <summary>
    /// The <see cref="StoredKey"/> of the status whose snapshots were just written, so an override
    /// uploaded with a status that has no id is still stored under the key a replace or delete finds.
    /// <see langword="null"/> when no snapshot was written.
    /// </summary>
    private static string? GroupKey(IEnumerable<V4Models.IV4Record> written)
    {
        var records = written.ToList();
        var anchor = records.OfType<V4Models.ApsSnapshot>().FirstOrDefault()
            ?? (V4Models.IV4Record?)records.OfType<V4Models.PumpSnapshot>().FirstOrDefault()
            ?? records.OfType<V4Models.UploaderSnapshot>().FirstOrDefault();
        return anchor is null ? null : StoredKey(anchor);
    }

    /// <summary>
    /// Withdraws the group's override observation. A span the status opened starts at its device's
    /// next status instead, or is deleted when there is none. A span it ended stays ended.
    /// </summary>
    private async Task<int> RetractOverrideAsync(StoredGroup group, CancellationToken ct)
    {
        var device = group.Aps?.Device ?? group.Pump?.Device ?? group.Uploader?.Device;
        var opened = (await _stateSpanService.GetStateSpansAsync(
                category: StateSpanCategory.Override,
                from: group.Timestamp,
                to: group.Timestamp,
                count: int.MaxValue,
                cancellationToken: ct))
            .Where(s => IsUploaderSpan(s, device) && s.StartTimestamp == group.Timestamp && s.Id is not null)
            .ToList();

        var deleted = 0;
        foreach (var span in opened)
        {
            if (await FirstApsBetweenAsync(device, group.Timestamp, span.EndTimestamp, group.LegacyId, ct) is { } next)
            {
                StartAt(span, next.Timestamp, StoredKey(next));
                await _stateSpanService.UpdateStateSpanAsync(span.Id!, span, ct);
            }
            else if (await _stateSpanService.DeleteStateSpanAsync(span.Id!, ct))
            {
                deleted++;
            }
        }

        return deleted;
    }

    #endregion

    #region Batch Decomposition

    /// <summary>The batch path before the four phases, kept until the equivalence test has run against it.</summary>
    internal async Task<V4Models.DecompositionResult> DecomposeBatchBeforeFourPhasesAsync(
        IReadOnlyList<DeviceStatus> statuses, string? source, WriteOrigin origin, CancellationToken ct = default)
    {
        if (statuses.Count == 0)
            return new V4Models.DecompositionResult();

        var result = new V4Models.DecompositionResult();

        var apsList = new List<V4Models.ApsSnapshot>();
        var pumpList = new List<V4Models.PumpSnapshot>();
        var uploaderList = new List<V4Models.UploaderSnapshot>();
        var extrasList = new List<V4Models.DeviceStatusExtras>();
        var overrideStatuses = new List<(DeviceStatus Status, Guid CorrelationId)>();
        var correlationIds = await GetStoredCorrelationIdsAsync(statuses.Select(s => s.Id), SnapshotTables, ct);

        await DeferLastSeenAsync(_deviceService, async () =>
        {
            foreach (var ds in statuses)
            {
                NormalizeMills(ds);

                var legacyId = ds.Id;
                var correlationId = CorrelationIdFor(legacyId, correlationIds);
                result.CorrelationId ??= correlationId;
                var statusMills = ResolveStatusMills(ds);

                Guid? pumpDeviceId = null;

                if (ds.Pump != null)
                {
                    var pumpModel = await BuildPumpSnapshotAsync(ds, legacyId, source, statusMills, correlationId, ct);
                    pumpModel.PatientDeviceId = await _deviceService.ResolvePatientDeviceAsync(pumpModel.DeviceId, statusMills, ct);

                    pumpDeviceId = pumpModel.DeviceId;
                    pumpList.Add(pumpModel);
                }

                if (ds.Cgm != null)
                {
                    await RegisterCgmDeviceAsync(ds, statusMills, ct);
                }

                if (MapToApsSnapshot(ds, legacyId, source, correlationId) is { } apsModel)
                {
                    apsModel.DeviceId = pumpDeviceId;
                    apsModel.PatientDeviceId = await _deviceService.ResolvePatientDeviceAsync(pumpDeviceId, statusMills, ct);
                    apsList.Add(apsModel);
                }

                if (ds.Uploader != null || ds.UploaderBattery.HasValue)
                {
                    uploaderList.Add(await BuildUploaderSnapshotAsync(ds, legacyId, source, statusMills, correlationId, ct));
                }

                if (ds.Override is { Active: not null })
                {
                    overrideStatuses.Add((ds, correlationId));
                }

                if (BuildExtras(ds, correlationId) is { } extrasModel)
                {
                    extrasList.Add(extrasModel);
                }
            }
        }, ct);

        using (SystemAttributedBatchWrites(_auditContext))
        {
            await BulkUpsertAsync(_apsRepo, apsList, result, origin, ct);
            await BulkUpsertAsync(_pumpRepo, pumpList, result, origin, ct);
            await BulkUpsertAsync(_uploaderRepo, uploaderList, result, origin, ct);

            // Extras carry no legacy id, so nothing holds them when a re-run's snapshots are all
            // withheld; written anyway they would join nothing. A stored group's extras row, already
            // live under the correlation id the group keeps, holds its own re-send off in BulkCreateAsync.
            var stored = result.StoredRecords.OfType<V4Models.IV4Record>()
                .Select(r => r.CorrelationId)
                .ToHashSet();
            var held = apsList.Select(a => a.CorrelationId)
                .Concat(pumpList.Select(p => p.CorrelationId))
                .Concat(uploaderList.Select(u => u.CorrelationId))
                .Where(id => !stored.Contains(id))
                .ToHashSet();
            extrasList.RemoveAll(e => held.Contains(e.CorrelationId));

            await BulkCreateAsync(_extrasRepo, extrasList, result, origin, ct);
        }

        await StampLoopAsync(result, ct);

        var storedSnapshots = result.StoredRecords
            .OfType<V4Models.IV4Record>()
            .ToLookup(r => r.CorrelationId);
        // Newest first: the batch's APS rows are already stored and would contradict older observations.
        foreach (var (ds, correlationId) in overrideStatuses.OrderByDescending(o => ResolveTimestamp(o.Status)))
        {
            await DecomposeOverrideAsync(ds, ds.Id ?? GroupKey(storedSnapshots[correlationId]), result, ct);
        }

        // Post-insert pump suspension pass: sequential, order-dependent
        if (pumpList.Count > 0)
        {
            var persistedPumps = result.StoredRecords
                .OfType<V4Models.PumpSnapshot>()
                .OrderBy(p => p.Timestamp)
                .ToList();

            for (var i = 0; i < persistedPumps.Count; i++)
            {
                var pumpSnapshot = persistedPumps[i];
                // The last duplicate is the one BulkUpsertAsync kept.
                var ds = statuses.LastOrDefault(s => s.Id == pumpSnapshot.LegacyId);
                if (ds != null)
                {
                    await DecomposePumpSuspensionAsync(ds, pumpSnapshot, result, origin, ct);
                    await DecomposePumpModeAsync(ds, pumpSnapshot, result, origin, ct);
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Four phases, each a fixed number of queries whatever the batch's size: the preload, the models
    /// built in memory, one bulk upsert per table, and the override and pump-mode folds over the
    /// spans the preload read, written back in one update and one upsert.
    /// </remarks>
    public async Task<DeviceStatusDecomposition> DecomposeBatchAsync(
        IReadOnlyList<DeviceStatus> statuses, string? source, WriteOrigin origin, CancellationToken ct = default)
    {
        if (statuses.Count == 0)
            return new DeviceStatusDecomposition();

        foreach (var ds in statuses)
            NormalizeMills(ds);

        var correlationIds = await GetStoredCorrelationIdsAsync(statuses.Select(s => s.Id), SnapshotTables, ct);
        var batch = statuses
            .Select(ds => new BatchStatus(ds, ds.Id, CorrelationIdFor(ds.Id, correlationIds), ResolveTimestamp(ds)))
            .ToList();
        return await DecomposeAsync(batch, source, group: null, origin, ct);
    }

    /// <summary>A status of a batch, with the legacy id and correlation id its group is written under.</summary>
    private sealed record BatchStatus(DeviceStatus Status, string? LegacyId, Guid CorrelationId, DateTime Timestamp)
    {
        public long Mills => Timestamp.ToMills();
    }

    /// <summary>The models one status builds, before any is written.</summary>
    private sealed record BatchModels(
        V4Models.ApsSnapshot? Aps,
        V4Models.PumpSnapshot? Pump,
        V4Models.UploaderSnapshot? Uploader,
        V4Models.DeviceStatusExtras? Extras);

    /// <param name="group">
    /// The stored group a replacement rewrites row by row, or <see langword="null"/> for a batch, whose
    /// rows are matched by legacy id.
    /// </param>
    private async Task<DeviceStatusDecomposition> DecomposeAsync(
        IReadOnlyList<BatchStatus> batch, string? source, StoredGroup? group, WriteOrigin origin, CancellationToken ct)
    {
        var result = new DeviceStatusDecomposition { CorrelationId = batch[0].CorrelationId };

        var devices = await ResolveDevicesAsync(batch, ct);
        var links = await _deviceService.GetPatientDeviceLinksAsync(devices.Select(d => d.Pump), ct);
        var spans = await PreloadSpansAsync(batch, ct);

        var models = batch
            .Select((status, i) => BuildModels(status, source, devices[i].Pump, devices[i].Uploader, links, group))
            .ToList();

        await WriteSnapshotsAsync(models, group, result, origin, ct);
        await StampLoopAsync(result, ct);

        var owners = new Dictionary<StateSpan, Guid>(ReferenceEqualityComparer.Instance);
        await FoldOverridesAsync(batch, spans, result, owners, ct);
        await FoldPumpModesAsync(spans, result, owners, ct);
        await WriteSpansAsync(spans, result, owners, ct);

        var wrote = result.CreatedRecords.Concat(result.UpdatedRecords)
            .Select(r => r switch
            {
                StateSpan span => owners.TryGetValue(span, out var owner) ? owner : (Guid?)null,
                V4Models.IV4Record record => record.CorrelationId,
                V4Models.DeviceStatusExtras extras => extras.CorrelationId,
                _ => null,
            })
            .OfType<Guid>()
            .ToHashSet();
        result.Wrote = [.. batch.Select(s => wrote.Contains(s.CorrelationId))];
        return result;
    }

    /// <summary>
    /// The pump and uploader device of each status, resolved with its CGM in one call, so each device's
    /// seen window is written once for the batch. The CGM has no snapshot table: resolving it registers
    /// the sensor as an in-use device.
    /// </summary>
    private async Task<IReadOnlyList<(Guid? Pump, Guid? Uploader)>> ResolveDevicesAsync(
        IReadOnlyList<BatchStatus> batch, CancellationToken ct)
    {
        var sightings = new List<DeviceSighting>();
        var slots = new List<(int? Pump, int? Uploader)>(batch.Count);
        foreach (var status in batch)
        {
            var ds = status.Status;
            int? pump = null, uploader = null;
            if (ds.Pump != null)
            {
                pump = sightings.Count;
                sightings.Add(new(V4Models.DeviceCategory.InsulinPump, ds.Pump.Manufacturer, PumpDeviceKey(ds), status.Mills));
            }

            if (ds.Cgm != null)
                sightings.Add(new(V4Models.DeviceCategory.CGM, ds.Cgm.Manufacturer, ds.Cgm.Serial ?? ds.Cgm.Model, status.Mills));

            if (ds.Uploader != null || ds.UploaderBattery.HasValue)
            {
                uploader = sightings.Count;
                sightings.Add(new(V4Models.DeviceCategory.Uploader, ds.Uploader?.Name, ds.Uploader?.Type ?? "unknown", status.Mills));
            }

            slots.Add((pump, uploader));
        }

        var ids = await _deviceService.ResolveManyAsync(sightings, ct);
        return [.. slots.Select(s => (s.Pump is { } p ? ids[p] : null, s.Uploader is { } u ? ids[u] : null))];
    }

    private BatchModels BuildModels(
        BatchStatus status, string? source, Guid? pumpDeviceId, Guid? uploaderDeviceId,
        IPatientDeviceLinks links, StoredGroup? group)
    {
        var (ds, legacyId, correlationId, _) = status;

        V4Models.PumpSnapshot? pump = null;
        if (ds.Pump != null)
        {
            pump = MapToPumpSnapshot(ds, legacyId, source, correlationId);
            pump.DeviceId = pumpDeviceId;
            pump.PatientDeviceId = links.Resolve(pumpDeviceId, status.Mills);
            if (group is not null)
                (pump.DataSource, pump.SyncIdentifier) = (group.Pump?.DataSource, group.Pump?.SyncIdentifier);
        }

        var aps = MapToApsSnapshot(ds, legacyId, source, correlationId);
        if (aps is not null)
        {
            aps.DeviceId = pumpDeviceId;
            aps.PatientDeviceId = links.Resolve(pumpDeviceId, status.Mills);
            if (group is not null)
                (aps.DataSource, aps.SyncIdentifier) = (group.Aps?.DataSource, group.Aps?.SyncIdentifier);
        }

        V4Models.UploaderSnapshot? uploader = null;
        if (ds.Uploader != null || ds.UploaderBattery.HasValue)
        {
            uploader = MapToUploaderSnapshot(ds, legacyId, source, correlationId);
            uploader.DeviceId = uploaderDeviceId;
            if (group is not null)
                (uploader.DataSource, uploader.SyncIdentifier) = (group.Uploader?.DataSource, group.Uploader?.SyncIdentifier);
        }

        return new BatchModels(aps, pump, uploader, BuildExtras(ds, correlationId));
    }

    /// <summary>
    /// One bulk upsert per snapshot table, then the extras of the groups still holding a snapshot. A
    /// replacement writes its group's rows by their stored ids instead: a row may carry no legacy id,
    /// or another one, for the bulk upsert to match it by.
    /// </summary>
    private async Task WriteSnapshotsAsync(
        IReadOnlyList<BatchModels> models, StoredGroup? group, V4Models.DecompositionResult result,
        WriteOrigin origin, CancellationToken ct)
    {
        var apsList = models.Select(m => m.Aps).OfType<V4Models.ApsSnapshot>().ToList();
        var pumpList = models.Select(m => m.Pump).OfType<V4Models.PumpSnapshot>().ToList();
        var uploaderList = models.Select(m => m.Uploader).OfType<V4Models.UploaderSnapshot>().ToList();
        var extrasList = models.Select(m => m.Extras).OfType<V4Models.DeviceStatusExtras>().ToList();

        if (group is not null)
        {
            foreach (var pump in pumpList)
                await UpsertByLegacyIdAsync(_pumpRepo, group.LegacyId, pump, result, origin, ct,
                    beforeWrite: existing => KeepAttribution(() => pump.PatientDeviceId ??= existing?.PatientDeviceId),
                    findStored: () => Task.FromResult(group.Pump));
            foreach (var aps in apsList)
                await UpsertByLegacyIdAsync(_apsRepo, group.LegacyId, aps, result, origin, ct,
                    beforeWrite: existing => KeepAttribution(() => aps.PatientDeviceId ??= existing?.PatientDeviceId),
                    findStored: () => Task.FromResult(group.Aps));
            foreach (var uploader in uploaderList)
                await UpsertByLegacyIdAsync(_uploaderRepo, group.LegacyId, uploader, result, origin, ct,
                    findStored: () => Task.FromResult(group.Uploader));
            await BulkCreateAsync(_extrasRepo, Unheld(extrasList), result, origin, ct);
            return;
        }

        using (SystemAttributedBatchWrites(_auditContext))
        {
            await BulkUpsertAsync(_apsRepo, apsList, result, origin, ct);
            await BulkUpsertAsync(_pumpRepo, pumpList, result, origin, ct);
            await BulkUpsertAsync(_uploaderRepo, uploaderList, result, origin, ct);
            await BulkCreateAsync(_extrasRepo, Unheld(extrasList), result, origin, ct);
        }

        // Extras carry no legacy id, so nothing holds them when a group's snapshots are all withheld;
        // written anyway they would join nothing. A stored group's extras row, already live under the
        // correlation id the group keeps, holds its own re-send off in BulkCreateAsync.
        List<V4Models.DeviceStatusExtras> Unheld(List<V4Models.DeviceStatusExtras> extras)
        {
            var stored = result.StoredRecords.OfType<V4Models.IV4Record>().Select(r => r.CorrelationId).ToHashSet();
            var held = apsList.Select(a => a.CorrelationId)
                .Concat(pumpList.Select(p => p.CorrelationId))
                .Concat(uploaderList.Select(u => u.CorrelationId))
                .Where(id => !stored.Contains(id))
                .ToHashSet();
            return [.. extras.Where(e => !held.Contains(e.CorrelationId))];
        }
    }

    /// <summary>A re-resolution that has since become ambiguous cannot displace the stored link.</summary>
    private static Task KeepAttribution(Action keep)
    {
        keep();
        return Task.CompletedTask;
    }

    #endregion

    #region Span Folds

    /// <summary>
    /// The override and pump-mode spans the folds read and write: those read before the batch and
    /// those the folds open. A read span the folds change is written back by id; an opened one is
    /// upserted by its original id, as a span written on its own would be.
    /// </summary>
    private sealed class SpanState(List<StateSpan> overrides, List<StateSpan> pumpModes)
    {
        public List<StateSpan> Overrides { get; } = overrides;
        public List<StateSpan> PumpModes { get; } = pumpModes;
        public List<StateSpan> Opened { get; } = [];
        public List<StateSpan> Changed { get; } = [];

        public void Change(StateSpan span)
        {
            if (!Opened.Contains(span) && !Changed.Contains(span))
                Changed.Add(span);
        }

        /// <summary>
        /// An opened span takes the place in memory of the span already keyed to its original id,
        /// which the upsert overwrites in the store.
        /// </summary>
        public void Open(List<StateSpan> category, StateSpan span)
        {
            if (!string.IsNullOrEmpty(span.OriginalId))
                category.RemoveAll(s => s.OriginalId == span.OriginalId);
            category.Add(span);
            Opened.Add(span);
        }
    }

    /// <summary>
    /// Every override span that can contain or follow one of the batch's observations, and every open
    /// pump-mode span: one query per category the batch observes.
    /// </summary>
    private async Task<SpanState> PreloadSpansAsync(IReadOnlyList<BatchStatus> batch, CancellationToken ct)
    {
        var observing = batch.Where(s => s.Status.Override is { Active: not null }).ToList();
        IReadOnlyList<StateSpan> overrides = observing.Count == 0
            ? []
            : await _stateSpanService.GetOpenOrEndingFromAsync(
                StateSpanCategory.Override, observing.Min(s => s.Timestamp), ct);
        IReadOnlyList<StateSpan> pumpModes = batch.Any(s => s.Status.Pump != null)
            ? await _stateSpanService.GetOpenOrEndingFromAsync(StateSpanCategory.PumpMode, endingFrom: null, ct)
            : [];
        return new SpanState([.. overrides], [.. pumpModes]);
    }

    /// <summary>
    /// Folds each status's override observation into the spans, newest first: the batch's APS rows are
    /// already stored and would contradict older observations.
    /// </summary>
    private async Task FoldOverridesAsync(
        IReadOnlyList<BatchStatus> batch, SpanState spans, V4Models.DecompositionResult result,
        Dictionary<StateSpan, Guid> owners, CancellationToken ct)
    {
        var observing = batch.Where(s => s.Status.Override is { Active: not null })
            .OrderByDescending(s => s.Timestamp)
            .ToList();
        if (observing.Count == 0)
            return;

        var marks = await _apsRepo.GetMarksAsync(
            [.. observing.Select(s => s.Status.Device).Distinct()],
            observing[^1].Timestamp, observing[0].Timestamp, OverrideQueryLimit, ct);
        var stored = result.StoredRecords.OfType<V4Models.IV4Record>().ToLookup(r => r.CorrelationId);

        foreach (var status in observing)
        {
            var key = status.LegacyId ?? GroupKey(stored[status.CorrelationId]);
            foreach (var span in FoldOverride(status, key, spans, marks))
                owners[span] = status.CorrelationId;
        }
    }

    /// <summary>
    /// A span stays open until a contrary observation or its declared end.
    /// </summary>
    /// <returns>The spans the observation opened or changed.</returns>
    private List<StateSpan> FoldOverride(
        BatchStatus status, string? key, SpanState spans, IReadOnlyList<V4Models.ApsSnapshotMark> marks)
    {
        var (ds, _, _, at) = status;
        var device = ds.Device;
        var observed = ds.Override!.Active == true ? BuildOverrideMetadata(ds.Override) : null;
        var touched = new List<StateSpan>();

        bool Unchanged(StateSpan span) =>
            observed is not null
            && (!IsUploaderSpan(span, device)
                ? span.Metadata.IsSameOverrideAs(observed)
                : span.Metadata is { } metadata
                  && metadata.TryReadString("name") == observed.TryReadString("name")
                  && metadata.OverrideScaleFactor() == observed.OverrideScaleFactor());

        var covered = false;
        var containing = spans.Overrides
            .Where(s => s.StartTimestamp <= at && (s.EndTimestamp == null || s.EndTimestamp >= at))
            .ToList();
        foreach (var span in containing)
        {
            // Of two conflicting observations at one instant, the first decomposed wins.
            if (Unchanged(span) || span.StartTimestamp >= at)
            {
                covered |= IsUploaderSpan(span, device);
                continue;
            }

            if (span.EndTimestamp == at)
                continue;

            span.EndTimestamp = at;
            spans.Change(span);
            touched.Add(span);
        }

        if (observed is null || covered)
            return touched;

        var next = spans.Overrides
            .Where(s => (string.IsNullOrEmpty(device) || s.Source == device) && s.StartTimestamp >= at)
            .OrderBy(s => s.StartTimestamp)
            .Take(OverrideQueryLimit)
            .FirstOrDefault(s => s.StartTimestamp > at && IsUploaderSpan(s, device));

        var before = next?.StartTimestamp ?? DateTime.MaxValue;
        var contraryAt = marks
            .Where(m => (device is null || m.Device == device) && m.Timestamp > at && m.Timestamp < before
                        && (m.LegacyId ?? MongoObjectId.FromGuid(m.Id)) != key)
            .OrderBy(m => m.Timestamp).ThenBy(m => m.Id)
            .FirstOrDefault()?.Timestamp;

        var opened = BuildOverrideSpan(ds, at, key);

        if (next is not null
            && contraryAt is null
            && Unchanged(next)
            && !(opened.EndTimestamp < next.StartTimestamp))
        {
            StartAt(next, at, key);
            spans.Change(next);
            touched.Add(next);
            return touched;
        }

        var firstLater = contraryAt ?? next?.StartTimestamp;
        if (firstLater < (opened.EndTimestamp ?? DateTime.MaxValue))
            opened.EndTimestamp = firstLater;

        spans.Open(spans.Overrides, opened);
        touched.Add(opened);
        return touched;
    }

    /// <summary>
    /// Folds each stored pump snapshot, oldest first, into the <see cref="StateSpanCategory.PumpMode"/>
    /// spans, each against the newest snapshot stored strictly before it.
    /// </summary>
    private async Task FoldPumpModesAsync(
        SpanState spans, V4Models.DecompositionResult result, Dictionary<StateSpan, Guid> owners, CancellationToken ct)
    {
        var pumps = result.StoredRecords.OfType<V4Models.PumpSnapshot>().OrderBy(p => p.Timestamp).ToList();
        if (pumps.Count == 0)
            return;

        var timeline = await _pumpRepo.GetTimelineAsync(pumps[0].Timestamp, pumps[^1].Timestamp, ct);
        foreach (var pump in pumps)
        {
            var prior = timeline.Where(p => p.Timestamp < pump.Timestamp).MaxBy(p => p.Timestamp);
            foreach (var span in FoldSuspension(pump, prior, spans).Concat(FoldPumpMode(pump, prior, spans)))
                owners[span] = pump.CorrelationId ?? Guid.Empty;
        }
    }

    /// <summary>
    /// Opens or closes a <see cref="StateSpanCategory.PumpMode"/> / <see cref="PumpModeState.Suspended"/>
    /// span on a suspension transition.
    /// </summary>
    /// <remarks>
    /// <para>On a <c>false → true</c> transition (or first observation with <c>Suspended == true</c>),
    /// opens a new span. On <c>true → false</c>, closes the open span. Equal-state comparisons are no-ops.</para>
    /// <para>First observation: when there is no prior snapshot, opening on
    /// <c>Suspended == true</c> anchors the span at the first observed timestamp — there is no
    /// transition signal to anchor on otherwise.</para>
    /// <para>Idempotency: the open span carries a deterministic
    /// <c>OriginalId = "pump-suspended:{snapshotId}"</c> so re-decomposing the same legacy
    /// <see cref="DeviceStatus"/> will upsert (not duplicate) the row.</para>
    /// <para>Assumes a single insulin pump per tenant — the open-span lookup does not filter by
    /// <c>Source</c>, so a second pump's resume could close a first pump's open span. Out of scope
    /// per the alerting model (one tenant = one diabetic person).</para>
    /// </remarks>
    /// <returns>The spans the transition opened or changed.</returns>
    private List<StateSpan> FoldSuspension(V4Models.PumpSnapshot snapshot, V4Models.PumpSnapshot? prior, SpanState spans)
    {
        var priorSuspended = prior?.Suspended ?? false;
        var nowSuspended = snapshot.Suspended ?? false;
        if (priorSuspended == nowSuspended)
            return [];

        // Prefer the pump's own clock for the transition; fall back to the ingestion timestamp.
        var transitionAt = ParseTimestampToDateTime(snapshot.Clock) ?? snapshot.Timestamp;
        var suspended = PumpModeState.Suspended.ToString();
        var open = spans.PumpModes.Where(s => s.State == suspended && s.EndTimestamp == null).ToList();

        if (nowSuspended)
        {
            // Guard against duplicate open spans. An uploader can emit two device statuses for the
            // same suspend event sharing the SAME ingest Timestamp but different pump clocks (e.g.
            // Trio uploads paired snapshots). Because the prior snapshot is read strictly before the
            // Timestamp, the second such snapshot cannot see its sibling as prior and reads
            // prior=not-suspended — a spurious false→true transition. Opening a second span here
            // leaves an orphan the single resume can't fully close, latching the pump "suspended"
            // indefinitely. If a Suspended span is already open, this observation is already covered.
            if (open.Count > 0)
                return [];

            var span = PumpModeSpan(suspended, transitionAt, snapshot.Device, $"pump-suspended:{snapshot.Id}");
            spans.Open(spans.PumpModes, span);
            return [span];
        }

        if (open.Count == 0)
        {
            // No open span exists — the suspended=true state predates the StateSpan feature or the
            // opening snapshot was never decomposed. A retroactive closed span anchored at the prior
            // snapshot's timestamp keeps the suspension timeline complete.
            if (prior is null)
            {
                Logger.LogWarning(
                    "PumpMode/Suspended transition true→false detected but no prior snapshot or open StateSpan (snapshot {SnapshotId})",
                    snapshot.Id);
                return [];
            }

            var backfilled = PumpModeSpan(
                suspended, ParseTimestampToDateTime(prior.Clock) ?? prior.Timestamp, snapshot.Device,
                $"pump-suspended:{prior.Id}");
            backfilled.EndTimestamp = transitionAt;
            spans.Open(spans.PumpModes, backfilled);
            return [backfilled];
        }

        // Close ALL active Suspended spans, not just one: leftover duplicates (created before the
        // open-guard above, or by concurrent decomposition) must all be closed on resume, otherwise an
        // orphan keeps the pump latched "suspended".
        foreach (var span in open)
        {
            span.EndTimestamp = transitionAt;
            spans.Change(span);
        }

        return open;
    }

    /// <summary>
    /// Maintains <see cref="StateSpanCategory.PumpMode"/> / <see cref="PumpModeState.Automatic"/> vs
    /// <see cref="PumpModeState.Manual"/> spans on a closed-loop mode transition.
    /// </summary>
    /// <remarks>
    /// <para>No-op unless the snapshot carries a <see cref="V4Models.PumpSnapshot.PumpMode"/> signal —
    /// only connectors that observe the pump's algorithm state (currently CareLink) populate it, so
    /// every other source leaves it null and emits no spans.</para>
    /// <para>On a change (or first observation), closes any open span of the opposite mode and opens
    /// one for the new mode. Steady-state (equal modes) is a no-op, so the open span spans the whole
    /// period rather than fragmenting per snapshot.</para>
    /// <para>Independent of <see cref="FoldSuspension"/>: both share the <c>PumpMode</c> category but
    /// are filtered by <see cref="StateSpan.State"/>, so an Automatic/Manual span and a Suspended span
    /// may legitimately overlap.</para>
    /// <para>Idempotency: the open span carries a deterministic
    /// <c>OriginalId = "pump-mode:{snapshotId}"</c>, and the open-span guard prevents duplicates when
    /// the same device status is re-decomposed.</para>
    /// </remarks>
    /// <returns>The spans the transition opened or changed.</returns>
    private static List<StateSpan> FoldPumpMode(V4Models.PumpSnapshot snapshot, V4Models.PumpSnapshot? prior, SpanState spans)
    {
        if (ParsePumpMode(snapshot.PumpMode) is not { } mode || ParsePumpMode(prior?.PumpMode) == mode)
            return [];

        var transitionAt = ParseTimestampToDateTime(snapshot.Clock) ?? snapshot.Timestamp;
        var opposite = (mode == PumpModeState.Automatic ? PumpModeState.Manual : PumpModeState.Automatic).ToString();

        // A span starting after the transition stays open, so re-decomposing an older snapshot can't
        // invert a newer span's range.
        var touched = spans.PumpModes
            .Where(s => s.State == opposite && s.EndTimestamp == null && s.StartTimestamp <= transitionAt)
            .ToList();
        foreach (var span in touched)
        {
            span.EndTimestamp = transitionAt;
            spans.Change(span);
        }

        if (spans.PumpModes.Any(s => s.State == mode.ToString() && s.EndTimestamp == null))
            return touched;

        var opened = PumpModeSpan(mode.ToString(), transitionAt, snapshot.Device, $"pump-mode:{snapshot.Id}");
        spans.Open(spans.PumpModes, opened);
        touched.Add(opened);
        return touched;
    }

    private static StateSpan PumpModeSpan(string state, DateTime start, string? source, string originalId) => new()
    {
        Category = StateSpanCategory.PumpMode,
        State = state,
        StartTimestamp = start,
        EndTimestamp = null,
        Source = source,
        OriginalId = originalId,
    };

    /// <summary>
    /// The read spans the folds changed, in one update by id, then the spans they opened, in one upsert
    /// in the order they were opened.
    /// </summary>
    private async Task WriteSpansAsync(
        SpanState spans, V4Models.DecompositionResult result, Dictionary<StateSpan, Guid> owners, CancellationToken ct)
    {
        if (spans.Changed.Count > 0)
        {
            var updated = await _stateSpanService.UpdateStateSpansAsync(spans.Changed, ct);
            foreach (var (span, written) in spans.Changed.Zip(updated))
            {
                if (written is null)
                    continue;
                owners[written] = owners[span];
                result.UpdatedRecords.Add(written);
            }
        }

        if (spans.Opened.Count > 0)
        {
            var upserted = await _stateSpanService.UpsertStateSpansAsync(spans.Opened, ct);
            foreach (var (span, written) in spans.Opened.Zip(upserted))
            {
                owners[written] = owners[span];
                result.CreatedRecords.Add(written);
            }
        }
    }

    /// <summary>
    /// The snapshot tables a status's stored correlation id is read from, in the APS, pump, uploader
    /// order <see cref="DeleteStoredAsync"/> prefers. The group's extras row carries no legacy id
    /// of its own and is found through that id, so a re-send that moved the snapshots onto a new one
    /// would orphan it beyond <see cref="DeleteStoredAsync"/>'s reach.
    /// </summary>
    private StoredCorrelationLookup[] SnapshotTables =>
    [
        _apsRepo.GetCorrelationIdsByLegacyIdAsync,
        _pumpRepo.GetCorrelationIdsByLegacyIdAsync,
        _uploaderRepo.GetCorrelationIdsByLegacyIdAsync,
    ];

    #endregion

    #region Mapping Helpers

    /// <summary>
    /// An OpenAPS/AAPS/Trio payload wins over a Loop one; <see langword="null"/> when the device
    /// status carries neither.
    /// </summary>
    internal V4Models.ApsSnapshot? MapToApsSnapshot(
        DeviceStatus ds, string? legacyId, string? source, Guid? correlationId)
    {
        if (ds.OpenAps != null)
            return MapToApsSnapshotFromOpenAps(ds, legacyId, source, correlationId);

        return ds.Loop != null ? MapToApsSnapshotFromLoop(ds, legacyId, source, correlationId) : null;
    }

    private V4Models.ApsSnapshot MapToApsSnapshotFromOpenAps(
        DeviceStatus ds, string? legacyId, string? source, Guid? correlationId)
    {
        var command = ApsSnapshotCurves.FreshestOpenApsCommand(ds.OpenAps!);
        var apsSystem = DetectOpenApsVariant(ds);

        var snapshot = new V4Models.ApsSnapshot
        {
            Timestamp = ResolveTimestamp(ds),
            UtcOffset = ds.UtcOffset,
            Device = ds.Device,
            LegacyId = legacyId,
            DataSource = source,
            CorrelationId = correlationId,
            AidAlgorithm = apsSystem,
            Iob = ds.OpenAps.Iob?.Iob,
            BasalIob = ds.OpenAps.Iob?.BasalIob,
            BolusIob = ds.OpenAps.Iob?.BolusIob,
            Cob = ds.OpenAps.Cob ?? command?.COB,
            CurrentBg = command?.Bg,
            EventualBg = command?.EventualBG,
            TargetBg = command?.TargetBG,
            RecommendedBolus = command?.InsulinReq,
            SensitivityRatio = command?.SensitivityRatio,
            Enacted = ds.OpenAps.Enacted != null
                && (ds.OpenAps.Enacted.Received == true || ds.OpenAps.Enacted.Recieved == true),
            EnactedRate = ds.OpenAps.Enacted?.Rate,
            EnactedDuration = ds.OpenAps.Enacted?.Duration,
            EnactedBolusVolume = ds.OpenAps.Enacted?.Smb is > 0
                ? ds.OpenAps.Enacted.Smb
                : ds.OpenAps.Enacted?.Units,
            AidVersion = ds.OpenAps?.Version,
        };
        ApsSnapshotCurves.FillColumns(snapshot, command);
        snapshot.SuggestedJson = ApsSnapshotCurves.SerializeCommand(ds.OpenAps.Suggested, snapshot, JsonOptions);
        snapshot.EnactedJson = ApsSnapshotCurves.SerializeCommand(ds.OpenAps.Enacted, snapshot, JsonOptions);
        return snapshot;
    }

    private V4Models.ApsSnapshot MapToApsSnapshotFromLoop(
        DeviceStatus ds, string? legacyId, string? source, Guid? correlationId)
    {
        var snapshot = new V4Models.ApsSnapshot
        {
            Timestamp = ResolveTimestamp(ds),
            UtcOffset = ds.UtcOffset,
            Device = ds.Device,
            LegacyId = legacyId,
            DataSource = source,
            CorrelationId = correlationId,
            AidAlgorithm = V4Models.AidAlgorithm.Loop,
            Iob = ds.Loop!.Iob?.Iob,
            BasalIob = ds.Loop.Iob?.BasalIob,
            BolusIob = null,
            Cob = ds.Loop.Cob?.Cob,
            CurrentBg = ds.Loop.Predicted?.Values?.FirstOrDefault(),
            EventualBg = ds.Loop.Predicted?.Values?.LastOrDefault(),
            RecommendedBolus = ds.Loop.RecommendedBolus,
            Enacted = ds.Loop.Enacted?.Received == true,
            EnactedRate = ds.Loop.Enacted?.Rate,
            EnactedDuration = ds.Loop.Enacted?.Duration,
            EnactedBolusVolume = ds.Loop.Enacted?.BolusVolume,
            AidVersion = null,
        };
        ApsSnapshotCurves.FillColumns(snapshot, ds.Loop);
        snapshot.LoopJson = ApsSnapshotCurves.SerializeLoop(ds.Loop, snapshot, JsonOptions);
        return snapshot;
    }

    private V4Models.PumpSnapshot MapToPumpSnapshot(
        DeviceStatus ds, string? legacyId, string? source, Guid? correlationId)
    {
        return new V4Models.PumpSnapshot
        {
            Timestamp = ResolveTimestamp(ds),
            UtcOffset = ds.UtcOffset,
            Device = ds.Device,
            LegacyId = legacyId,
            DataSource = source,
            CorrelationId = correlationId,
            Manufacturer = ds.Pump!.Manufacturer,
            Model = ds.Pump.Model,
            Reservoir = ds.Pump.Reservoir,
            ReservoirDisplay = ds.Pump.ReservoirDisplayOverride,
            BatteryPercent = ds.Pump.Battery?.Percent,
            BatteryVoltage = ds.Pump.Battery?.Voltage,
            Bolusing = ds.Pump.Status?.Bolusing,
            Suspended = ds.Pump.Status?.Suspended,
            PumpStatus = ds.Pump.Status?.Status,
            PumpMode = ds.Pump.PumpMode,
            Clock = ds.Pump.Clock,
            Iob = ds.Pump.Iob?.Iob,
            BolusIob = ds.Pump.Iob?.BolusIob,
            AdditionalProperties = ds.Pump.Extended is { Count: > 0 }
                ? ds.Pump.Extended.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value)
                : null,
        };
    }

    private V4Models.UploaderSnapshot MapToUploaderSnapshot(
        DeviceStatus ds, string? legacyId, string? source, Guid? correlationId)
    {
        return new V4Models.UploaderSnapshot
        {
            Timestamp = ResolveTimestamp(ds),
            UtcOffset = ds.UtcOffset,
            Device = ds.Device,
            LegacyId = legacyId,
            DataSource = source,
            CorrelationId = correlationId,
            Name = ds.Uploader?.Name,
            Battery = ds.Uploader?.Battery ?? ds.UploaderBattery,
            BatteryVoltage = ds.Uploader?.BatteryVoltage,
            IsCharging = ds.IsCharging ?? ds.Uploader?.IsCharging,
            Temperature = ds.Uploader?.Temperature,
            Type = ds.Uploader?.Type,
        };
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Distinguishes between vanilla OpenAPS, AAPS, and Trio based on payload heuristics.
    /// All three post under the "openaps" devicestatus key.
    /// - AAPS: uploader name contains "AndroidAPS"
    /// - Trio: openaps block includes a version field
    /// - Vanilla OpenAPS: neither of the above
    /// </summary>
    internal static V4Models.AidAlgorithm DetectOpenApsVariant(DeviceStatus ds)
    {
        if (ds.Uploader?.Name?.Contains("AndroidAPS", StringComparison.OrdinalIgnoreCase) == true)
            return V4Models.AidAlgorithm.AndroidAps;

        if (!string.IsNullOrEmpty(ds.OpenAps?.Version))
            return V4Models.AidAlgorithm.Trio;

        return V4Models.AidAlgorithm.OpenAps;
    }

    /// <summary>
    /// Resolves the best available timestamp for a device status record.
    /// Priority: Mills (already normalized from date) > OpenAPS IOB time >
    /// <see cref="ApsSnapshotCurves.FreshestOpenApsCommand"/> timestamp > Loop timestamp > Pump clock > CreatedAt >
    /// Loop predicted start date > now.
    /// </summary>
    internal DateTime ResolveTimestamp(DeviceStatus ds)
    {
        if (ds.Mills > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(ds.Mills).UtcDateTime;

        if (ParseTimestampToDateTime(ds.OpenAps?.Iob?.Time) is { } iobTime)
            return iobTime;

        var command = ds.OpenAps is null ? null : ApsSnapshotCurves.FreshestOpenApsCommand(ds.OpenAps);
        if (ParseTimestampToDateTime(command?.Timestamp) is { } commandTime)
            return commandTime;

        if (ParseTimestampToDateTime(ds.Loop?.Timestamp) is { } loopTime)
            return loopTime;

        if (ParseTimestampToDateTime(ds.Pump?.Clock) is { } pumpTime)
            return pumpTime;

        if (ParseTimestampToDateTime(ds.CreatedAt) is { } createdTime)
            return createdTime;

        // predicted.startDate is the latest reading's time, not the cycle's
        if (ParseTimestampToDateTime(ds.Loop?.Predicted?.StartDate) is { } predictedTime)
            return predictedTime;

        return _time.GetUtcNow().UtcDateTime;
    }

    private long ResolveStatusMills(DeviceStatus ds) =>
        ResolveTimestamp(ds).ToMills();

    private static DateTime? ParseTimestampToDateTime(string? timestamp) =>
        UploaderTimestamp.ParseUtcDateTime(timestamp);

    private static Dictionary<string, object> BuildOverrideMetadata(OverrideStatus overrideStatus)
    {
        var metadata = new Dictionary<string, object>
        {
            [StateSpanMetadataExtensions.CollectionKey] = StateSpanMetadataExtensions.DeviceStatusCollection,
        };

        if (!string.IsNullOrEmpty(overrideStatus.Name))
            metadata["name"] = overrideStatus.Name;

        if (overrideStatus.Multiplier.HasValue)
            metadata["multiplier"] = overrideStatus.Multiplier.Value;

        if (overrideStatus.CurrentCorrectionRange?.MinValue.HasValue == true)
            metadata["currentCorrectionRange.minValue"] = overrideStatus.CurrentCorrectionRange.MinValue.Value;

        if (overrideStatus.CurrentCorrectionRange?.MaxValue.HasValue == true)
            metadata["currentCorrectionRange.maxValue"] = overrideStatus.CurrentCorrectionRange.MaxValue.Value;

        return metadata;
    }

    #endregion

    /// <inheritdoc />
    /// <remarks>
    /// Rows carrying the group's legacy id are deleted under it, which reaches every row of a forked
    /// group; the rest (a status stored without one) by id. Deletes take the caller's attribution, so a
    /// user's delete leaves tombstones that refuse a re-upload.
    /// </remarks>
    public async Task<int> DeleteStoredAsync(string storedId, WriteOrigin origin, CancellationToken ct = default)
    {
        if (await LoadStoredGroupAsync(storedId, ct) is not { } group)
            return 0;

        var deleted = 0;
        if (group.Aps?.LegacyId == group.LegacyId || group.Pump?.LegacyId == group.LegacyId
            || group.Uploader?.LegacyId == group.LegacyId)
        {
            deleted += await _apsRepo.DeleteByLegacyIdAsync(group.LegacyId, origin, ct);
            deleted += await _pumpRepo.DeleteByLegacyIdAsync(group.LegacyId, origin, ct);
            deleted += await _uploaderRepo.DeleteByLegacyIdAsync(group.LegacyId, origin, ct);
        }

        deleted += await DeleteUnkeyedAsync(_apsRepo, group.Aps, group.LegacyId, origin, ct);
        deleted += await DeleteUnkeyedAsync(_pumpRepo, group.Pump, group.LegacyId, origin, ct);
        deleted += await DeleteUnkeyedAsync(_uploaderRepo, group.Uploader, group.LegacyId, origin, ct);
        deleted += await _extrasRepo.DeleteByCorrelationIdAsync(group.CorrelationId, ct);
        deleted += await RetractOverrideAsync(group, ct);

        if (deleted > 0)
            Logger.LogDebug("Deleted {Count} v4 records for device status {StoredId}", deleted, storedId);

        return deleted;
    }

    private static async Task<int> DeleteUnkeyedAsync<TRecord>(
        ILegacyKeyedRepository<TRecord> repository, TRecord? member, string legacyId,
        WriteOrigin origin, CancellationToken ct)
        where TRecord : class, V4Models.IV4Record
    {
        if (member is null || member.LegacyId == legacyId)
            return 0;

        await repository.DeleteAsync(member.Id, origin, ct);
        return 1;
    }
}
