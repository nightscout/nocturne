using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Outbound;

namespace Nocturne.Connectors.Nightscout.Services;

public partial class NightscoutConnectorServiceBase<TConfig> : BaseConnectorService<TConfig>
    where TConfig : NightscoutConnectorConfiguration
{
    // Starts as the startup defaults (from IConnectorRegistration); replaced with the
    // per-tenant config when EnsureAuthenticatedAsync runs at the start of a sync.
    // Per-instance, no concurrency: connectors are resolved into a fresh DI scope per
    // tenant sync, and SyncDataAsync is not invoked concurrently on the same instance.
    private TConfig _currentConfig;
    private string? _apiSecretHash;
    private NightscoutAddress? _address;
    private readonly NightscoutLookups _lookups;
    private readonly ITenantAccessor _tenant;

    public NightscoutConnectorServiceBase(
        HttpClient httpClient,
        IConnectorServerResolver<TConfig> serverResolver,
        ILogger logger,
        IRetryDelayStrategy retryDelayStrategy,
        IConnectorRegistration<TConfig> registration,
        NightscoutLookups lookups,
        ITenantAccessor tenant,
        IConnectorPublisher? publisher = null,
        TimeProvider? timeProvider = null
    )
        : base(httpClient, serverResolver, logger, retryDelayStrategy, publisher, timeProvider)
    {
        _currentConfig = registration?.Defaults ?? throw new ArgumentNullException(nameof(registration));
        _lookups = lookups;
        _tenant = tenant;
    }

    public override string ServiceName => "Nightscout";

    // A Nightscout instance is a full data export, so the initial sync (no prior data) imports the
    // source's entire history rather than the default bounded window — capping the first backfill
    // would silently drop older records. Catch-up syncs still resume from each type's own cursor.
    protected override DateTime? InitialSyncFloor => null;

    /// <summary>
    ///     Where a crawl with no lower bound stops: <see cref="NightscoutConnectorConfiguration.HistoryDays"/>
    ///     before now, or null for the source's first record. Only an open lower bound is given
    ///     one, so the limit shortens the backfill of a family with nothing stored and what such a
    ///     crawl left owed. A catch-up keeps its resume point however old, so an outage longer than
    ///     the limit loses nothing, and a sync of an explicit range is answered as asked.
    /// </summary>
    private DateTime? HistoryFloor(TConfig config) =>
        NightscoutConnectorConfiguration.HistoryStart(config.HistoryDays, Clock.GetUtcNow().UtcDateTime);


    private async Task<bool> AuthenticateWithConfigAsync(TConfig config, CancellationToken cancellationToken)
    {
        _currentConfig = config;
        _address = NightscoutAddress.Resolve(config.Url);
        _apiSecretHash = ApiSecretHeaderValue(config.ApiSecret);

        _logger.LogDebug(
            "[{ConnectorSource}] Authenticating with Nightscout at {Url} ({Credential})",
            ConnectorSource,
            _address.Origin,
            _apiSecretHash is null ? "no API secret" : "API secret");

        try
        {
            var headers = GetAuthHeaders();
            var response = await GetWithHeadersAsync(
                _address.For("/api/v1/entries.json?count=1"), headers, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (IsWafChallengePage(response, body))
                {
                    _logger.LogError(
                        "[{ConnectorSource}] Nightscout instance at {Url} is behind a WAF (e.g. Cloudflare) that is blocking API requests",
                        ConnectorSource,
                        _address.Origin);
                    TrackFailedAuthentication(
                        "Your Nightscout instance is behind a firewall (e.g. Cloudflare) that is blocking Nocturne from syncing. " +
                        "Please add a WAF bypass rule for API paths (e.g. /api/*) or allowlist the Nocturne server IP.");
                    return false;
                }

                _logger.LogError(
                    "[{ConnectorSource}] Nightscout auth check returned HTTP {StatusCode}: {Body}",
                    ConnectorSource,
                    (int)response.StatusCode,
                    body);
                TrackFailedAuthentication(NightscoutMessages.ForStatus(
                    response.StatusCode, "the connection check", NightscoutRead.ConnectorProbe,
                    secretSent: _apiSecretHash is not null));
                return false;
            }

            TrackSuccessfulRequest();
            _logger.LogInformation(
                "[{ConnectorSource}] Successfully authenticated with Nightscout instance",
                ConnectorSource);
            return true;
        }
        // A request that never got an answer says nothing about the credential, so it must not be
        // reported as one. DNS, a refused connection, dropped packets and a client timeout all land
        // here. OutboundRefusedException carries its own wording and names more than "unreachable"
        // can. A cancellation the caller asked for is the run being withdrawn, not a failure.
        catch (Exception ex) when (ex is not OperationCanceledException || ConnectorFaults.IsTimeout(ex, cancellationToken))
        {
            TrackFailedAuthentication(ex switch
            {
                OutboundRefusedException => ex.Message,
                HttpRequestException or OperationCanceledException => NightscoutMessages.Unreachable,
                _ => NightscoutMessages.CheckFailed,
            });
            _logger.LogError(ex,
                "[{ConnectorSource}] Failed to connect to Nightscout instance at {Url}",
                ConnectorSource,
                _address.Origin);
            return false;
        }
    }

    protected override Task<bool> EnsureAuthenticatedAsync(
        TConfig config,
        CancellationToken cancellationToken) => AuthenticateWithConfigAsync(config, cancellationToken);

    /// <inheritdoc />
    protected override bool SignsInBeforeSync => true;

    private static readonly SyncDataType[] TreatmentTypes =
    [
        SyncDataType.Boluses, SyncDataType.CarbIntake, SyncDataType.ManualBG,
        SyncDataType.BolusCalculations, SyncDataType.Notes, SyncDataType.DeviceEvents
    ];

    /// <summary>
    ///     Each active family's point on an open-ended run; an explicit range reads none. Glucose
    ///     resumes from its point only on a scheduled run (<see cref="BaseConnectorService{TConfig}.GlucoseFromAsync"/>).
    /// </summary>
    protected override IEnumerable<ResumeKey> ResumesFrom(SyncRequest request, TConfig config)
    {
        if (request.To is not null)
            yield break;

        var activeTypes = ResolveActiveTypes(request, config);
        if (request.Resume.Scheduled && activeTypes.Contains(SyncDataType.Glucose))
            yield return ResumeFamily.Glucose;
        if (activeTypes.Overlaps(TreatmentTypes))
            yield return ResumeFamily.Treatments;
        if (activeTypes.Contains(SyncDataType.DeviceStatus))
            yield return ResumeFamily.DeviceStatus;
        if (activeTypes.Contains(SyncDataType.Activity))
            yield return ResumeFamily.Activity;
    }

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        TConfig config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { Success = true };

        var activeTypes = ResolveActiveTypes(request, config);

        // On an open-ended catch-up (no explicit upper bound) glucose resolves its bound through
        // GlucoseFromAsync, and each other data type through ResumeFrom, from the request and its
        // own resume point. Explicit ranged syncs (request.To set, e.g. a manual re-import) honour
        // request.From/To as-is.
        var openEnded = request.To is null;
        var historyFloor = openEnded ? HistoryFloor(config) : null;

        DateTime? Floored(DateTime? from) => from ?? historyFloor;

        // Each data type below streams fetch-page → publish-page rather than accumulating
        // the whole range first: a multi-year backfill of a high-volume collection held in
        // one list has taken the process out with OutOfMemory, failing unrelated tenants'
        // publishes with it. Pages arrive newest first, so anything a broken crawl never
        // stored sits BELOW the newest stored record where an ordinary catch-up never
        // returns; see CrawlAndPublishAsync for how that history is resumed.
        if (activeTypes.Contains(SyncDataType.Glucose))
        {
            try
            {
                var glucoseFrom = await GlucoseFromAsync(request, "Glucose", cancellationToken);
                var recent = openEnded && glucoseFrom is { } crawlFrom
                    ? new RecentRecords<Entry>(crawlFrom - RecentReconcileWindow, Full: false, EntryTime)
                    : null;

                var outcome = await CrawlAndPublishAsync(
                    "Glucose", glucoseFrom, request.To,
                    (from, to) => FetchGlucosePagesAsync(Floored(from), to, recent, cancellationToken),
                    timeOf: EntryTime,
                    publishAsync: p => PublishInBatchesAsync(p, config, cancellationToken),
                    config.MaxCount,
                    cancellationToken);

                if (recent is not null && outcome.Success)
                    outcome = outcome.Plus(await PublishUncrawledAsync(recent, glucoseFrom!.Value, config, cancellationToken));

                RecordPublishOutcome(result, SyncDataType.Glucose, outcome.Count, outcome.Success);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || ConnectorFaults.IsTimeout(ex, cancellationToken))
            {
                RecordTypeSyncError(result, "Glucose", ex);
            }
        }

        // Nightscout fetches every treatment type as one batch, so each active sub-type
        // carries the whole batch's outcome.
        if (activeTypes.Overlaps(TreatmentTypes))
        {
            try
            {
                // The treatment cursor resolves to a bound rather than to an absent resume point:
                // with none stored it is this connector's own open InitialSyncFloor.
                var treatmentFrom = openEnded
                    ? ResumeFrom(request.From, await ResumePointAsync("Treatments", request.Resume.Point(ResumeFamily.Treatments), cancellationToken))
                    : request.From;

                var now = Clock.GetUtcNow().UtcDateTime;
                var recent = openEnded && treatmentFrom is { } crawlFrom
                    ? await RecentTreatmentsForAsync(crawlFrom, now, cancellationToken)
                    : null;

                var outcome = await CrawlAndPublishAsync(
                    "Treatments", treatmentFrom, request.To,
                    (from, to) => FetchTreatmentPagesAsync(Floored(from), to, recent, cancellationToken),
                    timeOf: t => ParseCreatedAt(t.CreatedAt)?.UtcDateTime,
                    publishAsync: p => PublishInBatchesAsync(p, config, cancellationToken),
                    config.MaxCount,
                    cancellationToken);

                if (recent is not null && outcome.Success)
                {
                    var reconciled = await ReconcileRecentTreatmentsAsync(recent, treatmentFrom!.Value, config, cancellationToken);
                    outcome = outcome.Plus(reconciled);
                    if (reconciled.Success && recent.Full)
                        await RecordFullReconcileAsync(now, cancellationToken);
                }

                if (openEnded && outcome.Success)
                    outcome = outcome.Plus(await RecheckOpenSpansAsync(recent, config, cancellationToken));

                foreach (var treatmentType in TreatmentTypes.Where(activeTypes.Contains))
                    RecordPublishOutcome(result, treatmentType, outcome.Count, outcome.Success);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || ConnectorFaults.IsTimeout(ex, cancellationToken))
            {
                RecordTypeSyncError(result, "Treatments", ex);
            }
        }

        if (activeTypes.Contains(SyncDataType.Profiles))
        {
            try
            {
                var profiles = await FetchProfilesAsync(cancellationToken);
                await PublishRecordTypeAsync(result, activeTypes,
                    profiles.ToList(), config, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || ConnectorFaults.IsTimeout(ex, cancellationToken))
            {
                RecordTypeSyncError(result, "Profiles", ex);
            }
        }

        if (activeTypes.Contains(SyncDataType.DeviceStatus))
        {
            try
            {
                var deviceStatusFrom = openEnded
                    ? ResumeFrom(request.From, await ResumePointAsync("DeviceStatus", request.Resume.Point(ResumeFamily.DeviceStatus), cancellationToken))
                    : request.From;

                var recent = openEnded && deviceStatusFrom is { } crawlFrom
                    ? new RecentRecords<DeviceStatus>(crawlFrom - RecentReconcileWindow, Full: false,
                        d => ParseCreatedAt(d.CreatedAt)?.UtcDateTime)
                    : null;

                var outcome = await CrawlAndPublishAsync(
                    "DeviceStatus", deviceStatusFrom, request.To,
                    (from, to) => FetchCreatedAtPagesAsync<DeviceStatus>(
                        Floored(from), to, "devicestatus", d => d.CreatedAt, "FetchDeviceStatus", cancellationToken,
                        observe: page => recent?.Collect(page)),
                    timeOf: d => ParseCreatedAt(d.CreatedAt)?.UtcDateTime,
                    publishAsync: p => PublishByRouteAsync(p, config, cancellationToken),
                    config.MaxCount,
                    cancellationToken);

                if (recent is not null && outcome.Success)
                    outcome = outcome.Plus(await PublishUncrawledAsync(recent, deviceStatusFrom!.Value, config, cancellationToken));

                RecordPublishOutcome(result, SyncDataType.DeviceStatus, outcome.Count, outcome.Success);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || ConnectorFaults.IsTimeout(ex, cancellationToken))
            {
                RecordTypeSyncError(result, "DeviceStatus", ex);
            }
        }

        if (activeTypes.Contains(SyncDataType.Food))
        {
            try
            {
                var foods = await FetchFoodAsync(cancellationToken);
                await PublishRecordTypeAsync(result, activeTypes,
                    foods.ToList(), config, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || ConnectorFaults.IsTimeout(ex, cancellationToken))
            {
                RecordTypeSyncError(result, "Food", ex);
            }
        }

        if (activeTypes.Contains(SyncDataType.Activity))
        {
            try
            {
                var activityFrom = openEnded
                    ? ResumeFrom(request.From, await ResumePointAsync("Activity", request.Resume.Point(ResumeFamily.Activity), cancellationToken))
                    : request.From;

                var recent = openEnded && activityFrom is { } crawlFrom
                    ? new RecentRecords<Activity>(crawlFrom - RecentReconcileWindow, Full: false,
                        a => ParseCreatedAt(a.CreatedAt)?.UtcDateTime)
                    : null;

                var outcome = await CrawlAndPublishAsync(
                    "Activity", activityFrom, request.To,
                    (from, to) => FetchCreatedAtPagesAsync<Activity>(
                        Floored(from), to, "activity", a => a.CreatedAt, "FetchActivity", cancellationToken,
                        observe: page => recent?.Collect(page)),
                    timeOf: a => ParseCreatedAt(a.CreatedAt)?.UtcDateTime,
                    publishAsync: p => PublishByRouteAsync(p, config, cancellationToken),
                    config.MaxCount,
                    cancellationToken);

                if (recent is not null && outcome.Success)
                    outcome = outcome.Plus(await PublishUncrawledAsync(recent, activityFrom!.Value, config, cancellationToken));

                RecordPublishOutcome(result, SyncDataType.Activity, outcome.Count, outcome.Success);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || ConnectorFaults.IsTimeout(ex, cancellationToken))
            {
                RecordTypeSyncError(result, "Activity", ex);
            }
        }

        return result;
    }

    /// <summary>
    ///     Upper bound for the first page of a paginated fetch. Nightscout applies an
    ///     implicit recency window (roughly the last four days) to any query carrying no
    ///     date filter at all, so a fully unbounded first page silently truncates a
    ///     full-history backfill — the short page then reads as end-of-history to the
    ///     pagination loop. Anchoring the bound to "now" keeps every request explicitly
    ///     dated; requests that already carry a bound pass through unchanged.
    /// </summary>
    internal static DateTime? AnchorUnboundedFetch(DateTime? from, DateTime? to, DateTime now) =>
        from is null && to is null ? now : to;

    /// <summary>
    ///     Streams a paginated Nightscout collection newest-first through
    ///     <see cref="BackwardTimePager.PageAsync{T}"/>, widening a crowded page up to
    ///     <see cref="BackwardTimePager.WidestPageSize"/>.
    /// </summary>
    /// <param name="from">Optional inclusive lower bound.</param>
    /// <param name="to">Optional inclusive upper bound; anchored to now when both bounds are open.</param>
    /// <param name="buildUrl">Builds the request URL for the given bounds and count.</param>
    /// <param name="oldestOf">Extracts the oldest record time from a page, or null when the page has no usable times.</param>
    /// <param name="admittedThrough">The latest record time the URL built for a bound admits.</param>
    /// <param name="collection">The Nightscout collection paged, for logging.</param>
    /// <param name="operationName">Operation label for fetch logging.</param>
    /// <param name="keep">Optional page filter; pagination still steps on the unfiltered page.</param>
    private async IAsyncEnumerable<T[]> FetchPagesAsync<T>(
        DateTime? from,
        DateTime? to,
        Func<DateTime?, DateTime?, int, string> buildUrl,
        Func<T[], DateTime?> oldestOf,
        Func<DateTime, DateTime> admittedThrough,
        string collection,
        string operationName,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Func<T[], T[]>? keep = null)
        where T : ProcessableDocumentBase
    {
        var pages = BackwardTimePager.PageAsync<T>(
            from,
            AnchorUnboundedFetch(from, to, Clock.GetUtcNow().UtcDateTime),
            _currentConfig.MaxCount,
            BackwardTimePager.WidestPageSize(_currentConfig.MaxCount),
            async (bound, count) =>
            {
                // FetchDataAsync reports failure (retries exhausted, non-retryable HTTP, bad JSON) as
                // null rather than throwing; <see cref="BaseConnectorService{TConfig}.FetchFailed"/> is
                // why that is not the end of the range.
                var page = await FetchDataAsync<T[]>(buildUrl(from, bound, count), operationName, cancellationToken)
                           ?? throw FetchFailed(operationName);
                return new TimePage<T>(page, page.Length);
            },
            oldestOf,
            admittedThrough,
            _logger,
            ConnectorSource,
            collection);

        await foreach (var page in pages)
        {
            var kept = keep is null ? page : keep(page);
            if (kept.Length > 0)
                yield return kept;
        }
    }

    private static DateTime? EntryTime(Entry entry) =>
        entry.Mills > 0 ? entry.Mills.ToUtcDateTime() : null;

    private static DateTime? OldestEntryTime(Entry[] page)
    {
        var oldestMs = page.Min(e => e.Mills);
        return oldestMs > 0
            ? oldestMs.ToUtcDateTime()
            : null;
    }

    private static DateTimeOffset? ParseCreatedAt(string? createdAt) =>
        UploaderTimestamp.TryParse(createdAt, out var parsed) ? parsed : null;

    /// <summary>
    ///     Pages a created_at collection inside the <see cref="BackwardTimePager.CreatedAtOffsetEnvelope"/>.
    ///     The filter's ceiling is the anchor the fetch bound was widened from, so an unbounded
    ///     backfill still stops at "now" rather than importing a future-dated device clock.
    /// </summary>
    private IAsyncEnumerable<T[]> FetchCreatedAtPagesAsync<T>(
        DateTime? from,
        DateTime? to,
        string collection,
        Func<T, string?> createdAtOf,
        string operationName,
        CancellationToken cancellationToken,
        Action<T[]>? observe = null)
        where T : ProcessableDocumentBase
    {
        var anchoredTo = AnchorUnboundedFetch(from, to, Clock.GetUtcNow().UtcDateTime);

        return FetchPagesAsync<T>(
            from - BackwardTimePager.CreatedAtOffsetEnvelope,
            anchoredTo + BackwardTimePager.CreatedAtOffsetEnvelope,
            (pageFrom, pageTo, count) => BuildCreatedAtUrl(collection, pageFrom, pageTo, count),
            page => BackwardTimePager.OldestWrittenCreatedAt(page, createdAtOf),
            BackwardTimePager.CreatedAtAdmittedThrough,
            collection,
            operationName,
            cancellationToken,
            page =>
            {
                observe?.Invoke(page);
                return page.Where(item => BackwardTimePager.CreatedAtWithin(createdAtOf(item), from, anchoredTo)).ToArray();
            });
    }

    /// <param name="recent">
    ///     Collects every page the read returns. Entries are read on their own time with no margin, so
    ///     the read reaches down to <see cref="RecentRecords{T}.ReadFrom"/> when that lies below
    ///     <paramref name="from"/>; only entries from <paramref name="from"/> on are yielded to publish.
    /// </param>
    private IAsyncEnumerable<Entry[]> FetchGlucosePagesAsync(
        DateTime? from, DateTime? to, RecentRecords<Entry>? recent, CancellationToken cancellationToken)
    {
        var readFrom = recent is not null && from > recent.ReadFrom ? recent.ReadFrom : from;

        return FetchPagesAsync<Entry>(
            readFrom, to, BuildEntriesUrl, OldestEntryTime, bound => bound, "entries", "FetchGlucosePages",
            cancellationToken,
            keep: page =>
            {
                foreach (var entry in page)
                    entry.DataSource = ConnectorSource;
                recent?.Collect(page);
                return readFrom == from ? page : page.Where(e => EntryTime(e) is not { } at || at >= from).ToArray();
            });
    }

    /// <param name="recent">
    ///     Collects every page the read returns as it arrives, before the crawl's window is applied.
    ///     A full reconcile reaches back to <see cref="RecentRecords{T}.ReadFrom"/> when that lies
    ///     below <paramref name="from"/>; only records from <paramref name="from"/> on are yielded to
    ///     publish.
    /// </param>
    private async IAsyncEnumerable<Treatment[]> FetchTreatmentPagesAsync(
        DateTime? from, DateTime? to, RecentRecords<Treatment>? recent,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var readFrom = recent is { Full: true } && from > recent.ReadFrom ? recent.ReadFrom : from;

        await foreach (var page in FetchCreatedAtPagesAsync<Treatment>(
            readFrom, to, "treatments", t => t.CreatedAt, "FetchTreatments", cancellationToken,
            observe: raw =>
            {
                foreach (var treatment in raw)
                    treatment.DataSource = ConnectorSource;
                recent?.Collect(raw);
            }))
        {
            var kept = readFrom == from ? page : page.Where(t => BackwardTimePager.CreatedAtWithin(t.CreatedAt, from, to)).ToArray();
            if (kept.Length > 0)
                yield return kept;
        }
    }

    /// <summary>
    ///     How far below the crawl's resume point every catch-up reconciles. A created_at read already
    ///     reaches <see cref="BackwardTimePager.CreatedAtOffsetEnvelope"/> below it for offset-written
    ///     values, so this window plus <see cref="ReconcileReadMargin"/> stays inside what it downloads
    ///     anyway. The entries read has no such margin and is extended by it
    ///     (<see cref="FetchGlucosePagesAsync"/>).
    /// </summary>
    private static readonly TimeSpan RecentReconcileWindow = TimeSpan.FromHours(12);

    /// <summary>
    ///     The window a full reconcile covers, reading this far back past the crawl when it must.
    /// </summary>
    private static readonly TimeSpan FullReconcileWindow = TimeSpan.FromHours(24);

    /// <summary>How long after a full reconcile the next catch-up runs another.</summary>
    private static readonly TimeSpan FullReconcileEvery = TimeSpan.FromHours(1);

    /// <summary>
    ///     The resume-state key the last full reconcile's time is kept under. No crawled collection is
    ///     named this.
    /// </summary>
    private const string FullReconcileKey = "TreatmentsFullReconcile";

    /// <remarks>The reconcile stamp schedules the treatment batch, which every treatment type shares.</remarks>
    protected override IReadOnlyDictionary<string, IReadOnlyCollection<SyncDataType>> ResumeStamps { get; } =
        new Dictionary<string, IReadOnlyCollection<SyncDataType>> { [FullReconcileKey] = TreatmentTypes };

    private Task<DateTime?> LastFullReconcileAsync(CancellationToken cancellationToken) =>
        ResumeStampAsync(FullReconcileKey, cancellationToken);

    private Task RecordFullReconcileAsync(DateTime at, CancellationToken cancellationToken) =>
        SetResumeStampAsync(FullReconcileKey, at, cancellationToken);

    /// <summary>
    ///     How much further back than the window the read reaches, and how far either side of a
    ///     stored time a lookup searches beyond <see cref="BackwardTimePager.CreatedAtOffsetEnvelope"/>. A row near the window's
    ///     edge can have a stored time slightly off its created_at.
    /// </summary>
    private static readonly TimeSpan ReconcileReadMargin = TimeSpan.FromHours(1);

    /// <summary>The most treatments one sync looks up by id; past this it deletes none.</summary>
    private const int MaxLookupsPerSync = 10;

    /// <summary>
    ///     How many missing treatments a sync may delete whatever their share of the window, so a
    ///     sparse window can lose one. Past this, missing more than a fifth of the window deletes
    ///     none: that reads as a source answering short, not as edits.
    /// </summary>
    private const int FewMissing = 3;

    private static readonly TimeSpan LookUpAgainAfter = TimeSpan.FromHours(6);

    /// <summary>
    ///     What a catch-up's read of one collection returned from <see cref="ReadFrom"/> on, for
    ///     <see cref="PublishUncrawledAsync{T}"/>, and for the treatment delete pass and the open-span
    ///     recheck, which match stored rows against <see cref="Served"/>.
    /// </summary>
    /// <param name="Full">Whether this is the hourly full treatment reconcile, which may extend the read.</param>
    /// <param name="TimeOf">The time the collection is read and crawled on.</param>
    /// <param name="KeysOf">The keys a stored row can be matched to a served record by; none are kept when null.</param>
    private sealed record RecentRecords<T>(
        DateTime WindowStart, bool Full, Func<T, DateTime?> TimeOf, Func<T, IEnumerable<string?>>? KeysOf = null)
        where T : ProcessableDocumentBase
    {
        public DateTime ReadFrom => WindowStart - ReconcileReadMargin;

        public List<T> Records { get; } = [];

        /// <summary>
        ///     Each key of a record as the source served it, with the record's time. The first record
        ///     served keeps its time, and insertion order is kept because the canary is the first key of
        ///     its kind. A publish rewrites a record's id to the one it is stored under, which the source
        ///     does not know, so a match against the source reads this and never <see cref="Records"/>.
        /// </summary>
        public Dictionary<string, DateTime> Served { get; } = [];

        public void Collect(IEnumerable<T> page)
        {
            foreach (var record in page.Where(r => TimeOf(r) is not { } at || at >= ReadFrom))
            {
                Records.Add(record);
                if (TimeOf(record) is not { } at)
                    continue;

                foreach (var key in KeysOf?.Invoke(record) ?? [])
                {
                    if (key is { Length: > 0 })
                        Served.TryAdd(key, at);
                }
            }
        }

        /// <summary>The identified records the crawl from <paramref name="crawledFrom"/> did not publish.</summary>
        public List<T> Uncrawled(DateTime crawledFrom) =>
            Records.Where(r => r.Id is { Length: > 0 } && TimeOf(r) is { } at && at < crawledFrom).ToList();
    }

    /// <summary>
    ///     Publishes what the read returned below the crawl's resume point. The crawl resumes from the
    ///     newest stored time, so a record that reaches the source after a newer one is never crawled:
    ///     CGM readings backfilled after a signal loss, back-dated treatments, uploads from an offline
    ///     phone, activity a health app syncs hours late.
    /// </summary>
    /// <param name="crawledFrom">The crawl's own lower bound; it already published what lies above.</param>
    /// <returns>How many records were written, not how many were compared.</returns>
    private async Task<PagedCrawlOutcome> PublishUncrawledAsync<T>(
        RecentRecords<T> recent,
        DateTime crawledFrom,
        TConfig config,
        CancellationToken cancellationToken)
        where T : ProcessableDocumentBase
    {
        // An unavailable publisher is not a failed sync here, but the recent publish reports it as one.
        if (Publisher is not { IsAvailable: true })
            return new PagedCrawlOutcome(0, true);

        var uncrawled = recent.Uncrawled(crawledFrom);
        if (uncrawled.Count == 0)
            return new PagedCrawlOutcome(0, true);

        return await PublishRecentByRouteAsync(uncrawled, config, cancellationToken) is { } written
            ? new PagedCrawlOutcome(written, true)
            : new PagedCrawlOutcome(0, false);
    }

    /// <summary>
    ///     This catch-up's reconcile window: <see cref="FullReconcileWindow"/> once
    ///     <see cref="FullReconcileEvery"/> has passed since the last full one, otherwise
    ///     <see cref="RecentReconcileWindow"/> below the crawl's resume point.
    /// </summary>
    private async Task<RecentRecords<Treatment>> RecentTreatmentsForAsync(
        DateTime crawlFrom, DateTime now, CancellationToken cancellationToken)
    {
        var lastFull = await LastFullReconcileAsync(cancellationToken);
        return lastFull is { } last && last <= now && now - last < FullReconcileEvery
            ? new RecentRecords<Treatment>(crawlFrom - RecentReconcileWindow, Full: false, TreatmentTime, TreatmentKeys)
            : new RecentRecords<Treatment>(now - FullReconcileWindow, Full: true, TreatmentTime, TreatmentKeys);
    }

    private static DateTime? TreatmentTime(Treatment treatment) => ParseCreatedAt(treatment.CreatedAt)?.UtcDateTime;

    /// <summary>
    ///     The keys a stored treatment can be matched to its served document by: its <c>_id</c>, its
    ///     uploader's <c>id</c>, and the <c>identifier</c> write-back gave it, which is where a treatment
    ///     Nocturne wrote back and stores under that key is recognised although the source gave it an
    ///     <c>_id</c> of its own.
    /// </summary>
    private static IEnumerable<string?> TreatmentKeys(Treatment treatment) =>
        [treatment.Id, TreatmentClientId.Of(treatment), treatment.UpstreamIdentifier];

    /// <summary>
    ///     Catches up on what the event-time crawl cannot see, from the treatments the crawl's own read
    ///     returned in the reconcile window (<see cref="PublishUncrawledAsync{T}"/>). Trio edits by
    ///     deleting and re-uploading under the original event time, which the crawl misses. It also
    ///     misses an edit made in place under the same id (Loop, AAPS, Careportal). Those are published
    ///     again, under <see cref="ITreatmentPublisher.PublishRecentTreatmentsAsync"/>'s rule.
    ///     Nightscout's v1 API hard-deletes and leaves no tombstone to page for. Stored rows the read did
    ///     not return are therefore deleted once the source confirms them gone
    ///     (<see cref="DeleteTreatmentsGoneUpstreamAsync"/>).
    /// </summary>
    /// <param name="crawledFrom">The crawl's own lower bound; it already published what lies above.</param>
    /// <returns>How many treatments were written, not how many were compared.</returns>
    private async Task<PagedCrawlOutcome> ReconcileRecentTreatmentsAsync(
        RecentRecords<Treatment> recent, DateTime crawledFrom, TConfig config,
        CancellationToken cancellationToken)
    {
        if (Publisher is not { IsAvailable: true } publisher || recent.Records.Count == 0)
            return new PagedCrawlOutcome(0, true);

        var republished = await PublishUncrawledAsync(recent, crawledFrom, config, cancellationToken);
        if (!republished.Success)
            return republished;

        // Not a publish: the delete pass reads and removes stored rows, which no route covers.
        await DeleteTreatmentsGoneUpstreamAsync(publisher.Treatments, recent.WindowStart, recent.Served, cancellationToken);

        return republished;
    }

    /// <summary>
    ///     Deletes this connector's stored treatments from <paramref name="windowStart"/> on that
    ///     <paramref name="read"/> lacks, once a lookup by id finds each one gone. The read alone
    ///     proves nothing: the crawl can step past a crowded millisecond, and a stored time
    ///     can differ from its created_at. The lookup is trusted only once it finds a treatment the
    ///     read did return, so a source that cannot answer it deletes nothing. A failed lookup also
    ///     deletes nothing and leaves the sync's result alone, as does more missing than
    ///     <see cref="MaxLookupsPerSync"/> or <see cref="FewMissing"/> allow.
    /// </summary>
    /// <param name="read">
    ///     The keys the read returned (<see cref="TreatmentKeys"/>), each with its created_at, as
    ///     <see cref="RecentRecords{T}.Served"/> holds them.
    /// </param>
    private async Task DeleteTreatmentsGoneUpstreamAsync(
        ITreatmentPublisher treatments,
        DateTime windowStart,
        IReadOnlyDictionary<string, DateTime> read,
        CancellationToken cancellationToken)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var stored = await treatments.GetStoredTreatmentIdsAsync(ConnectorSource, windowStart, now, cancellationToken);
        var candidates = stored
            .Where(s => !read.ContainsKey(s.Key) && !LookedUpRecently(s.Key, now))
            .ToList();

        if (candidates.Count == 0)
            return;

        if (candidates.Count > MaxLookupsPerSync || (candidates.Count > FewMissing && candidates.Count * 5 > stored.Count))
        {
            _logger.LogWarning(
                "[{ConnectorSource}] {Missing} of {Stored} stored treatments since {Since:u} are missing from the source; deleting none",
                ConnectorSource, candidates.Count, stored.Count, windowStart);
            return;
        }

        var gone = new HashSet<string>();
        try
        {
            foreach (var kind in candidates.GroupBy(c => IsObjectId(c.Key)))
            {
                if (read.FirstOrDefault(r => IsObjectId(r.Key) == kind.Key) is not { Key: not null } canary)
                    continue;

                if (!await TreatmentExistsUpstreamAsync(canary.Key, canary.Value, cancellationToken))
                {
                    _logger.LogWarning(
                        "[{ConnectorSource}] The source could not find a treatment it had just returned by id; deleting none",
                        ConnectorSource);
                    return;
                }

                foreach (var (id, at) in kind)
                {
                    if (await TreatmentExistsUpstreamAsync(id, at, cancellationToken))
                        _lookups.Remember(LookupSource, id, now + LookUpAgainAfter);
                    else
                        gone.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ConnectorFaults.IsTimeout(ex, cancellationToken))
        {
            _logger.LogWarning(ex,
                "[{ConnectorSource}] Looking treatments up by id failed; deleting none", ConnectorSource);
            return;
        }

        if (gone.Count == 0)
            return;

        var deleted = await treatments.DeleteTreatmentsAsync(ConnectorSource, gone, cancellationToken);
        _logger.LogInformation(
            "[{ConnectorSource}] Deleted {Records} records of {Treatments} treatments the source no longer has",
            ConnectorSource, deleted, gone.Count);
    }

    private NightscoutLookupSource LookupSource => new(_tenant.TenantId, ServiceName, _address?.Base ?? string.Empty);

    private bool LookedUpRecently(string id, DateTime now) => _lookups.LookedUpRecently(LookupSource, id, now);

    /// <summary>
    ///     Looks a treatment up by the field its id came from. A treatment is read under its <c>_id</c>,
    ///     but a row can still be stored under an uploader's own <c>id</c> (Trio's UUID) if no read has
    ///     moved it since (<see cref="ITreatmentPublisher.PublishTreatmentsAsync"/>). An id that is not
    ///     an ObjectId is therefore looked up by <c>id</c>. Asking for it by <c>_id</c> is an error on
    ///     Nightscout releases that cast the value to an ObjectId. An ObjectId-shaped id is looked up by
    ///     <c>_id</c> and then by <c>id</c>. Either is then looked up by <c>identifier</c>, where
    ///     write-back put the key of a treatment Nocturne stores under it (see
    ///     <see cref="TreatmentKeys"/>), in one query: as it is, and as
    ///     <see cref="MongoObjectId.Coerce"/> of it, the uuid prefix or hash write-back has created under
    ///     since. A row relabelled to the connector still holds the key it was stored under before. None
    ///     of these fields is indexed, so the lookup is bounded to the
    ///     created_at range the treatment can sit in: <paramref name="at"/>, give or take
    ///     <see cref="BackwardTimePager.CreatedAtOffsetEnvelope"/> and <see cref="ReconcileReadMargin"/>.
    /// </summary>
    private async Task<bool> TreatmentExistsUpstreamAsync(string id, DateTime at, CancellationToken cancellationToken) =>
        (await FindTreatmentUpstreamAsync(id, at, cancellationToken)).Length > 0;

    /// <summary>
    ///     The documents <see cref="TreatmentExistsUpstreamAsync"/> finds: those of the first field that
    ///     names <paramref name="id"/>, or none.
    /// </summary>
    private async Task<Treatment[]> FindTreatmentUpstreamAsync(string id, DateTime at, CancellationToken cancellationToken) =>
        IsObjectId(id) && await FindTreatmentByObjectIdAsync(id, at, cancellationToken) is { Length: > 0 } byId
            ? byId
            : await FindTreatmentByClientKeysAsync(id, at, cancellationToken);

    /// <summary>The documents <c>find[_id]</c> finds for an ObjectId-shaped <paramref name="id"/>.</summary>
    private Task<Treatment[]> FindTreatmentByObjectIdAsync(string id, DateTime at, CancellationToken cancellationToken) =>
        LookUpTreatmentAsync(at, $"&find[_id]={Uri.EscapeDataString(id)}", cancellationToken);

    /// <summary>The documents found by <c>id</c>, else by <c>identifier</c>, as <see cref="TreatmentExistsUpstreamAsync"/> looks.</summary>
    private async Task<Treatment[]> FindTreatmentByClientKeysAsync(string id, DateTime at, CancellationToken cancellationToken)
    {
        if (await LookUpTreatmentAsync(at, $"&find[id]={Uri.EscapeDataString(id)}", cancellationToken) is { Length: > 0 } byClientId)
            return byClientId;

        string[] identifiers = [.. new[] { id, MongoObjectId.Coerce(id)! }.Distinct(StringComparer.Ordinal)];
        return await LookUpTreatmentAsync(at, string.Concat(identifiers.Select((value, i) =>
            $"&find[identifier][$in][{i}]={Uri.EscapeDataString(value)}")), cancellationToken);
    }

    private async Task<Treatment[]> LookUpTreatmentAsync(DateTime at, string find, CancellationToken cancellationToken)
    {
        const string operation = "LookUpTreatment";
        var reach = BackwardTimePager.CreatedAtOffsetEnvelope + ReconcileReadMargin;
        var url = BuildCreatedAtUrl("treatments", at - reach, at + reach) + find;

        return await FetchDataAsync<Treatment[]>(url, operation, cancellationToken) ?? throw FetchFailed(operation);
    }

    private static bool IsObjectId(string id) => id.Length == 24 && id.All(char.IsAsciiHexDigit);
    private async Task<IEnumerable<Profile>> FetchProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = await FetchDataAsync<Profile[]>(
            "/api/v1/profile.json",
            "FetchProfiles",
            cancellationToken);

        if (profiles == null || profiles.Length == 0)
        {
            _logger.LogInformation(
                "[{ConnectorSource}] No profiles found on Nightscout instance",
                ConnectorSource);
            return [];
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} profiles from Nightscout",
            ConnectorSource,
            profiles.Length);

        return profiles;
    }

    private async Task<IEnumerable<Food>> FetchFoodAsync(CancellationToken cancellationToken)
    {
        var foods = await FetchDataAsync<Food[]>(
            $"/api/v1/food.json?count={_currentConfig.MaxCount}",
            "FetchFood",
            cancellationToken);

        if (foods == null || foods.Length == 0)
        {
            _logger.LogInformation(
                "[{ConnectorSource}] No food records found on Nightscout instance",
                ConnectorSource);
            return [];
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} food records from Nightscout",
            ConnectorSource,
            foods.Length);

        return foods;
    }

    private async Task<T?> FetchDataAsync<T>(
        string url, string operationName, CancellationToken cancellationToken) where T : class
    {
        return await ExecuteWithRetryAsync(
            async () => await FetchDataCoreAsync<T>(url, cancellationToken),
            maxRetries: _currentConfig.MaxRetryAttempts,
            operationName: operationName,
            cancellationToken: cancellationToken);
    }

    private async Task<T?> FetchDataCoreAsync<T>(string url, CancellationToken cancellationToken) where T : class
    {
        var headers = GetAuthHeaders();
        var absoluteUrl = _address?.For(url) ?? url;
        var response = await GetWithHeadersAsync(absoluteUrl, headers, cancellationToken);

        await response.EnsureSuccessWithBodyAsync(cancellationToken);

        return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(cancellationToken), UpstreamIdentityJson.ReadOptions);
    }

    private string BuildEntriesUrl(DateTime? from, DateTime? to, int count)
    {
        var url = $"/api/v1/entries.json?count={count}";

        if (from.HasValue)
        {
            var fromMs = from.Value.ToMills();
            url += $"&find[date][$gte]={fromMs}";
        }

        if (to.HasValue)
        {
            var toMs = to.Value.ToMills();
            url += $"&find[date][$lte]={toMs}";
        }

        return url;
    }

    private string BuildCreatedAtUrl(string collection, DateTime? from, DateTime? to, int? count = null)
    {
        var url = $"/api/v1/{collection}.json?count={count ?? _currentConfig.MaxCount}";

        if (from.HasValue)
            url += $"&find[created_at][$gte]={from.Value.ToUniversalTime():o}";

        if (to.HasValue)
            url += $"&find[created_at][$lte]={BackwardTimePager.CreatedAtUpperBound(to.Value)}";

        return url;
    }

    private Dictionary<string, string>? GetAuthHeaders() =>
        (_apiSecretHash ?? ApiSecretHeaderValue(_currentConfig.ApiSecret)) is { } hash
            ? new Dictionary<string, string> { ["api-secret"] = hash }
            : null;

    /// <summary>
    ///     The <c>api-secret</c> header for <paramref name="apiSecret"/>, or null when there is no
    ///     secret to send: a hash of an empty secret is a wrong secret, not an absent one.
    /// </summary>
    public static string? ApiSecretHeaderValue(string? apiSecret) =>
        string.IsNullOrWhiteSpace(apiSecret) ? null : ComputeApiSecretHash(apiSecret);

    internal static string ComputeApiSecretHash(string apiSecret)
    {
        if (IsAlreadySha1Hash(apiSecret))
            return apiSecret.ToLowerInvariant();

        return HashUtils.Sha1Hex(apiSecret);
    }

    private static bool IsAlreadySha1Hash(string value)
    {
        return value.Length == 40 && value.All(c => char.IsAsciiHexDigit(c));
    }

    /// <summary>
    ///     Detects WAF challenge pages (Cloudflare, Akamai, etc.) that block server-to-server API requests.
    ///     These return HTML instead of JSON and typically include challenge scripts.
    /// </summary>
    private static bool IsWafChallengePage(HttpResponseMessage response, string body)
    {
        // Check for Cloudflare server header
        if (response.Headers.TryGetValues("server", out var serverValues) &&
            serverValues.Any(v => v.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)))
        {
            // Cloudflare returning non-JSON (challenge page) for an API request
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check for cf-ray header (Cloudflare) with HTML body containing challenge markers
        if (response.Headers.Contains("cf-ray") &&
            body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}

/// <summary>
/// Nightscout connector service for syncing data from a Nightscout instance.
/// </summary>
public class NightscoutConnectorService : NightscoutConnectorServiceBase<NightscoutConnectorConfiguration>
{
    public NightscoutConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<NightscoutConnectorConfiguration> serverResolver,
        ILogger<NightscoutConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IConnectorRegistration<NightscoutConnectorConfiguration> registration,
        NightscoutLookups lookups,
        ITenantAccessor tenant,
        IConnectorPublisher? publisher = null,
        TimeProvider? timeProvider = null
    ) : base(httpClient, serverResolver, logger, retryDelayStrategy, registration, lookups, tenant, publisher, timeProvider) { }
}
