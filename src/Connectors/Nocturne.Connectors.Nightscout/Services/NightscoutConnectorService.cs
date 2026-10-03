using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Net;

namespace Nocturne.Connectors.Nightscout.Services;

public class NightscoutConnectorServiceBase<TConfig> : BaseConnectorService<TConfig>
    where TConfig : NightscoutConnectorConfiguration
{
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly IRateLimitingStrategy _rateLimitingStrategy;
    private readonly TimeProvider _timeProvider;

    // Starts as the startup defaults (from IConnectorRegistration); replaced with the
    // per-tenant config when AuthenticateWithConfigAsync runs at the start of a sync.
    // Per-instance, no concurrency: connectors are resolved into a fresh DI scope per
    // tenant sync, and SyncDataAsync is not invoked concurrently on the same instance.
    private TConfig _currentConfig;
    private string? _apiSecretHash;
    private string? _resolvedBaseUrl;

    public NightscoutConnectorServiceBase(
        HttpClient httpClient,
        IConnectorServerResolver<TConfig> serverResolver,
        ILogger logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        IConnectorRegistration<TConfig> registration,
        IConnectorPublisher? publisher = null,
        TimeProvider? timeProvider = null
    )
        : base(httpClient, serverResolver, logger, publisher)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _rateLimitingStrategy = rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
        _currentConfig = registration?.Defaults ?? throw new ArgumentNullException(nameof(registration));
    }

    protected override string ConnectorSource => DataSources.NightscoutConnector;
    public override string ServiceName => "Nightscout";

    // A Nightscout instance is a full data export, so the initial sync (no prior data) imports the
    // source's entire history rather than the default bounded window — capping the first backfill
    // would silently drop older records. Catch-up syncs still resume from each type's own cursor.
    protected override DateTime? InitialSyncFloor => null;


    public override async Task<bool> AuthenticateAsync()
    {
        // Legacy no-config overload; uses whatever config the service was last primed
        // with (startup defaults until AuthenticateWithConfigAsync replaces it).
        // Per-tenant sync uses AuthenticateWithConfigAsync instead.
        return await AuthenticateWithConfigAsync(_currentConfig, CancellationToken.None);
    }

    private async Task<bool> AuthenticateWithConfigAsync(TConfig config, CancellationToken cancellationToken)
    {
        _currentConfig = config;
        _resolvedBaseUrl = ConnectorUrl.ResolveBase(config.Url, "Nightscout");

        if (string.IsNullOrEmpty(config.ApiSecret))
        {
            _logger.LogError(
                "[{ConnectorSource}] API secret is not configured",
                ConnectorSource);
            TrackFailedAuthentication(NightscoutMessages.ApiSecretMissing);
            return false;
        }

        _apiSecretHash = ComputeApiSecretHash(config.ApiSecret);

        _logger.LogDebug(
            "[{ConnectorSource}] Authenticating with Nightscout at {Url}",
            ConnectorSource,
            _resolvedBaseUrl);

        try
        {
            var headers = GetAuthHeaders();
            var response = await GetWithHeadersAsync(
                $"{_resolvedBaseUrl}/api/v1/entries.json?count=1", headers, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (IsWafChallengePage(response, body))
                {
                    _logger.LogError(
                        "[{ConnectorSource}] Nightscout instance at {Url} is behind a WAF (e.g. Cloudflare) that is blocking API requests",
                        ConnectorSource,
                        _resolvedBaseUrl);
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
                    response.StatusCode, "the connection check", NightscoutRead.ConnectorProbe));
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
        catch (Exception ex) when (ex is not OperationCanceledException
                                   || !cancellationToken.IsCancellationRequested)
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
                _resolvedBaseUrl);
            return false;
        }
    }

    public override async Task<SyncResult> SyncDataAsync(
        TConfig config,
        CancellationToken cancellationToken = default,
        DateTime? since = null,
        ISyncProgressReporter? progressReporter = null)
    {
        // _currentConfig starts as startup defaults (empty URL). Prime it with the
        // tenant config before base calls AuthenticateAsync(), which delegates to
        // AuthenticateWithConfigAsync(_currentConfig).
        _currentConfig = config;
        return await base.SyncDataAsync(config, cancellationToken, since, progressReporter);
    }

    protected override Task<bool> EnsureAuthenticatedAsync(
        TConfig config,
        CancellationToken cancellationToken) => AuthenticateWithConfigAsync(config, cancellationToken);

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        TConfig config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { Success = true };

        var activeTypes = ResolveActiveTypes(request, config);

        // On an open-ended catch-up (no explicit upper bound) each data type below resolves its
        // bound through ResumeFrom, from request.From and its own resume point. Explicit ranged
        // syncs (request.To set, e.g. a manual re-import) honour request.From/To as-is.
        var openEnded = request.To is null;

        // Glucose keeps request.From — for background syncs the framework already derived
        // it from the latest glucose entry, so it is glucose's own independent cursor.
        //
        // Each data type below streams fetch-page → publish-page rather than accumulating
        // the whole range first: a multi-year backfill of a high-volume collection held in
        // one list has taken the process out with OutOfMemory, failing unrelated tenants'
        // publishes with it. Pages arrive newest first, so anything a broken crawl never
        // stored sits BELOW the newest stored record where an ordinary catch-up never
        // returns; CrawlAndPublishAsync persists a low-water mark as pages land and resumes
        // below it on the next sync, so a crawl killed by a restart or a failing store
        // self-heals instead of stranding the older history.
        if (activeTypes.Contains(SyncDataType.Glucose))
        {
            try
            {
                var recent = openEnded && request.From is { } crawlFrom
                    ? new RecentRecords<Entry>(crawlFrom - RecentReconcileWindow, Full: false, EntryTime)
                    : null;

                var outcome = await CrawlAndPublishAsync(
                    "Glucose", request.From, request.To,
                    (from, to) => FetchGlucosePagesAsync(from, to, recent),
                    oldestOf: OldestEntryTime,
                    publishAsync: p => PublishGlucoseDataInBatchesAsync(p, config, cancellationToken));

                if (recent is not null && outcome.Success)
                    outcome = outcome.Plus(await PublishUncrawledAsync(recent, request.From!.Value,
                        async (p, uncrawled) => await p.Glucose.PublishRecentEntriesAsync(
                            uncrawled, ConnectorSource, await GlucosePublishOriginAsync(), cancellationToken)));

                RecordPublishOutcome(result, SyncDataType.Glucose, outcome.Count, outcome.Success);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Glucose: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Glucose for {Connector}", ConnectorSource);
            }
        }

        // Nightscout fetches every treatment type as one batch, so each active sub-type
        // carries the whole batch's outcome.
        SyncDataType[] treatmentTypes =
        [
            SyncDataType.Boluses, SyncDataType.CarbIntake, SyncDataType.ManualBG,
            SyncDataType.BolusCalculations, SyncDataType.Notes, SyncDataType.DeviceEvents
        ];
        if (activeTypes.Any(t => treatmentTypes.Contains(t)))
        {
            try
            {
                // The treatment cursor resolves to a bound rather than to an absent resume point:
                // with none stored it is this connector's own open InitialSyncFloor.
                var treatmentFrom = openEnded
                    ? ResumeFrom(request.From, await CalculateTreatmentSinceTimestampAsync(config))
                    : request.From;

                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var recent = openEnded && treatmentFrom is { } crawlFrom
                    ? await RecentTreatmentsForAsync(crawlFrom, now)
                    : null;

                var outcome = await CrawlAndPublishAsync(
                    "Treatments", treatmentFrom, request.To,
                    (from, to) => FetchTreatmentPagesAsync(from, to, recent),
                    oldestOf: p => OldestCreatedAt(p, t => t.CreatedAt),
                    publishAsync: p => PublishTreatmentDataInBatchesAsync(p, config, cancellationToken));

                if (recent is not null && outcome.Success)
                {
                    var reconciled = await ReconcileRecentTreatmentsAsync(recent, treatmentFrom!.Value, cancellationToken);
                    outcome = outcome.Plus(reconciled);
                    if (reconciled.Success && recent.Full)
                        await RecordFullReconcileAsync(now);
                }

                foreach (var treatmentType in treatmentTypes.Where(activeTypes.Contains))
                    RecordPublishOutcome(result, treatmentType, outcome.Count, outcome.Success);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Treatments: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Treatments for {Connector}", ConnectorSource);
            }
        }

        if (activeTypes.Contains(SyncDataType.Profiles))
        {
            try
            {
                var profiles = await FetchProfilesAsync();
                await PublishRecordTypeAsync(result, SyncDataType.Profiles, activeTypes,
                    profiles.ToList(), PublishProfileDataAsync, config, cancellationToken);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Profiles: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Profiles for {Connector}", ConnectorSource);
            }
        }

        if (activeTypes.Contains(SyncDataType.DeviceStatus))
        {
            try
            {
                var deviceStatusFrom = openEnded
                    ? ResumeFrom(request.From, await CalculateDeviceStatusCatchUpSinceAsync(config) ?? request.From)
                    : request.From;

                var recent = openEnded && deviceStatusFrom is { } crawlFrom
                    ? new RecentRecords<DeviceStatus>(crawlFrom - RecentReconcileWindow, Full: false,
                        d => ParseCreatedAt(d.CreatedAt)?.UtcDateTime)
                    : null;

                var outcome = await CrawlAndPublishAsync(
                    "DeviceStatus", deviceStatusFrom, request.To,
                    (from, to) => FetchCreatedAtPagesAsync<DeviceStatus>(
                        from, to, "devicestatus", d => d.CreatedAt, "FetchDeviceStatus", observe: page => recent?.Collect(page)),
                    oldestOf: p => OldestCreatedAt(p, d => d.CreatedAt),
                    publishAsync: p => PublishDeviceStatusAsync(p, config, cancellationToken));

                if (recent is not null && outcome.Success)
                    outcome = outcome.Plus(await PublishUncrawledAsync(recent, deviceStatusFrom!.Value,
                        async (p, uncrawled) => await p.Device.PublishRecentDeviceStatusAsync(
                            uncrawled, ConnectorSource, await DevicePublishOriginAsync(), cancellationToken)));

                RecordPublishOutcome(result, SyncDataType.DeviceStatus, outcome.Count, outcome.Success);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync DeviceStatus: {ex.Message}");
                _logger.LogError(ex, "Failed to sync DeviceStatus for {Connector}", ConnectorSource);
            }
        }

        if (activeTypes.Contains(SyncDataType.Food))
        {
            try
            {
                var foods = await FetchFoodAsync();
                await PublishRecordTypeAsync(result, SyncDataType.Food, activeTypes,
                    foods.ToList(), PublishFoodDataAsync, config, cancellationToken);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Food: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Food for {Connector}", ConnectorSource);
            }
        }

        if (activeTypes.Contains(SyncDataType.Activity))
        {
            try
            {
                var activityFrom = openEnded
                    ? ResumeFrom(request.From, await CalculateActivityCatchUpSinceAsync(config) ?? request.From)
                    : request.From;

                var recent = openEnded && activityFrom is { } crawlFrom
                    ? new RecentRecords<Activity>(crawlFrom - RecentReconcileWindow, Full: false,
                        a => ParseCreatedAt(a.CreatedAt)?.UtcDateTime)
                    : null;

                var outcome = await CrawlAndPublishAsync(
                    "Activity", activityFrom, request.To,
                    (from, to) => FetchCreatedAtPagesAsync<Activity>(
                        from, to, "activity", a => a.CreatedAt, "FetchActivity", observe: page => recent?.Collect(page)),
                    oldestOf: p => OldestCreatedAt(p, a => a.CreatedAt),
                    publishAsync: p => PublishActivityDataAsync(p, config, cancellationToken));

                if (recent is not null && outcome.Success)
                    outcome = outcome.Plus(await PublishUncrawledAsync(recent, activityFrom!.Value,
                        async (p, uncrawled) => await p.Metadata.PublishRecentActivityAsync(
                            uncrawled, ConnectorSource, WriteOrigin.Live, cancellationToken)));

                RecordPublishOutcome(result, SyncDataType.Activity, outcome.Count, outcome.Success);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync Activity: {ex.Message}");
                _logger.LogError(ex, "Failed to sync Activity for {Connector}", ConnectorSource);
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
    private static DateTime? AnchorUnboundedFetch(DateTime? from, DateTime? to) =>
        from is null && to is null ? DateTime.UtcNow : to;

    /// <summary>Outcome of one crawled collection, spanning every page of the crawl.</summary>
    private sealed record PagedCrawlOutcome(int Count, bool Success)
    {
        /// <summary>This outcome followed by <paramref name="next"/>, which ran only because this one succeeded.</summary>
        public PagedCrawlOutcome Plus(PagedCrawlOutcome next) => new(Count + next.Count, next.Success);
    }

    private Task<DateTime?> GetBackfillLowWaterMarkAsync(string collection) =>
        Publisher is { IsAvailable: true } p
            ? p.Metadata.GetBackfillLowWaterMarkAsync(ConnectorSource, collection)
            : Task.FromResult<DateTime?>(null);

    private Task SetBackfillLowWaterMarkAsync(string collection, DateTime? mark) =>
        Publisher is { IsAvailable: true } p
            ? p.Metadata.SetBackfillLowWaterMarkAsync(ConnectorSource, collection, mark)
            : Task.CompletedTask;

    /// <summary>
    ///     Crawls a collection newest-first, publishing each page as it lands, then — when an
    ///     earlier crawl of the collection left a persisted low-water mark — resumes that
    ///     incomplete backfill below the mark. Pages descend from "now", so anything a killed
    ///     crawl never reached sits BELOW the newest stored record and an ordinary catch-up
    ///     never returns for it; the mark is what carries "history below X is still missing"
    ///     across process restarts and store failures.
    /// </summary>
    /// <remarks>
    ///     The resume crawl is deliberately UNBOUNDED below the mark, and that is load-bearing:
    ///     the mark's value only decides where re-crawling starts, never where it stops, so a
    ///     raised or stale mark can only cost redundant (idempotent) re-fetching — every
    ///     missing region below any surviving mark is eventually reached. Bounding the resume
    ///     (e.g. stopping at a previous mark, or persisting gap floors) would turn those same
    ///     states into data loss. The known cost: a failed bounded catch-up leaves a near-now
    ///     mark whose resume re-crawls the full history for a minutes-wide gap — rare, safe,
    ///     and preferred over a more fragile gap bookkeeping.
    /// </remarks>
    private async Task<PagedCrawlOutcome> CrawlAndPublishAsync<T>(
        string collection,
        DateTime? from,
        DateTime? to,
        Func<DateTime?, DateTime?, IAsyncEnumerable<T[]>> pages,
        Func<T[], DateTime?> oldestOf,
        Func<T[], Task<bool>> publishAsync)
    {
        var mark = await GetBackfillLowWaterMarkAsync(collection);

        var primary = await CrawlRangeAsync(
            collection, from, to, pages, oldestOf, publishAsync, fullCrawl: from is null);

        // Resume the incomplete backfill only when this cycle's primary crawl stored cleanly —
        // a store that is failing right now shouldn't be hammered with the deep history too.
        // A full primary crawl (open lower bound) already covers everything below the mark.
        if (mark is null || !primary.Success || from is null)
            return primary;

        var resume = await CrawlRangeAsync(
            collection, null, mark.Value,
            pages, oldestOf, publishAsync, fullCrawl: true);

        return new PagedCrawlOutcome(primary.Count + resume.Count, resume.Success);
    }

    /// <summary>
    ///     One newest-first crawl over a range. A page publish failure stops the crawl (pages
    ///     below a gap would strand it above the resume point) and records the low-water mark
    ///     so the next sync resumes there. Full crawls (open lower bound) also advance the mark
    ///     after every published page — crash protection for multi-hour histories — and clear
    ///     it on reaching the source's beginning; bounded catch-up crawls only ever raise it.
    /// </summary>
    private async Task<PagedCrawlOutcome> CrawlRangeAsync<T>(
        string collection,
        DateTime? from,
        DateTime? to,
        Func<DateTime?, DateTime?, IAsyncEnumerable<T[]>> pages,
        Func<T[], DateTime?> oldestOf,
        Func<T[], Task<bool>> publishAsync,
        bool fullCrawl)
    {
        var count = 0;
        DateTime? lowestPublished = null;
        var success = true;

        try
        {
            await foreach (var page in pages(from, to))
            {
                count += page.Length;

                if (!await publishAsync(page))
                {
                    success = false;
                    break;
                }

                var pageOldest = oldestOf(page);
                if (pageOldest.HasValue)
                    lowestPublished = pageOldest;

                if (fullCrawl && lowestPublished.HasValue)
                    await SetBackfillLowWaterMarkAsync(collection, lowestPublished);
            }
        }
        catch
        {
            // A fetch failure mid-crawl leaves the same gap a publish failure does: record the
            // resume point for what already published before surfacing the error.
            await RaiseBackfillLowWaterMarkAsync(collection, lowestPublished);
            throw;
        }

        if (success && fullCrawl)
        {
            // Reached the source's beginning: the backfill is complete.
            await SetBackfillLowWaterMarkAsync(collection, null);
        }
        else if (!success)
        {
            await RaiseBackfillLowWaterMarkAsync(collection, lowestPublished);
        }

        return new PagedCrawlOutcome(count, success);
    }

    /// <summary>
    ///     Raises the collection's low-water mark to <paramref name="candidate"/> — never lowers
    ///     it: a deeper mark from an earlier failure still describes missing history further down.
    /// </summary>
    private async Task RaiseBackfillLowWaterMarkAsync(string collection, DateTime? candidate)
    {
        if (!candidate.HasValue)
            return;

        var existing = await GetBackfillLowWaterMarkAsync(collection);
        if (existing is null || existing < candidate)
            await SetBackfillLowWaterMarkAsync(collection, candidate);
    }

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
        Func<T[], T[]>? keep = null)
        where T : ProcessableDocumentBase
    {
        var pages = BackwardTimePager.PageAsync<T>(
            from,
            AnchorUnboundedFetch(from, to),
            _currentConfig.MaxCount,
            BackwardTimePager.WidestPageSize(_currentConfig.MaxCount),
            async (bound, count) =>
            {
                // FetchDataAsync reports failure (retries exhausted, non-retryable HTTP, bad JSON) as
                // null rather than throwing; <see cref="BaseConnectorService{TConfig}.FetchFailed"/> is
                // why that is not the end of the range.
                var page = await FetchDataAsync<T[]>(buildUrl(from, bound, count), operationName)
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
        entry.Mills > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(entry.Mills).UtcDateTime : null;

    private static DateTime? OldestEntryTime(Entry[] page)
    {
        var oldestMs = page.Min(e => e.Mills);
        return oldestMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(oldestMs).UtcDateTime
            : null;
    }

    private static DateTimeOffset? ParseCreatedAt(string? createdAt) =>
        UploaderTimestamp.TryParse(createdAt, out var parsed) ? parsed : null;

    /// <summary>
    ///     Oldest created_at on a page. Uses DateTimeOffset for consistent UTC comparison
    ///     regardless of system timezone.
    /// </summary>
    private static DateTime? OldestCreatedAt<T>(T[] page, Func<T, string?> createdAtOf) =>
        page.Select(item => ParseCreatedAt(createdAtOf(item))?.UtcDateTime).Min();

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
        Action<T[]>? observe = null)
        where T : ProcessableDocumentBase
    {
        var anchoredTo = AnchorUnboundedFetch(from, to);

        return FetchPagesAsync<T>(
            from - BackwardTimePager.CreatedAtOffsetEnvelope,
            anchoredTo + BackwardTimePager.CreatedAtOffsetEnvelope,
            (pageFrom, pageTo, count) => BuildCreatedAtUrl(collection, pageFrom, pageTo, count),
            page => BackwardTimePager.OldestWrittenCreatedAt(page, createdAtOf),
            BackwardTimePager.CreatedAtAdmittedThrough,
            collection,
            operationName,
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
    private IAsyncEnumerable<Entry[]> FetchGlucosePagesAsync(DateTime? from, DateTime? to, RecentRecords<Entry>? recent)
    {
        var readFrom = recent is not null && from > recent.ReadFrom ? recent.ReadFrom : from;

        return FetchPagesAsync<Entry>(
            readFrom, to, BuildEntriesUrl, OldestEntryTime, bound => bound, "entries", "FetchGlucosePages",
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
        DateTime? from, DateTime? to, RecentRecords<Treatment>? recent = null)
    {
        var readFrom = recent is { Full: true } && from > recent.ReadFrom ? recent.ReadFrom : from;

        await foreach (var page in FetchCreatedAtPagesAsync<Treatment>(
            readFrom, to, "treatments", t => t.CreatedAt, "FetchTreatments",
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
    ///     The connector-metadata key the last full reconcile's time is kept under. It shares the
    ///     per-collection timestamp map with the backfill low-water marks, the only durable
    ///     per-connector timestamp store there is, and no collection is named this.
    /// </summary>
    private const string FullReconcileKey = "TreatmentsFullReconcile";

    private Task<DateTime?> LastFullReconcileAsync() => GetBackfillLowWaterMarkAsync(FullReconcileKey);

    private Task RecordFullReconcileAsync(DateTime at) => SetBackfillLowWaterMarkAsync(FullReconcileKey, at);

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

    /// <summary>
    ///     Treatments a lookup found upstream although the read missed them, keyed by source URL and
    ///     id. A treatment the read keeps missing is then looked up once rather than every sync.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTime> ConfirmedPresent = new();

    private static readonly TimeSpan ConfirmedPresentFor = TimeSpan.FromHours(6);

    /// <summary>
    ///     What a catch-up's read of one collection returned from <see cref="ReadFrom"/> on, for
    ///     <see cref="PublishUncrawledAsync{T}"/>.
    /// </summary>
    /// <param name="Full">Whether this is the hourly full treatment reconcile, which may extend the read.</param>
    /// <param name="TimeOf">The time the collection is read and crawled on.</param>
    private sealed record RecentRecords<T>(DateTime WindowStart, bool Full, Func<T, DateTime?> TimeOf)
        where T : ProcessableDocumentBase
    {
        public DateTime ReadFrom => WindowStart - ReconcileReadMargin;

        public List<T> Records { get; } = [];

        public void Collect(IEnumerable<T> page) =>
            Records.AddRange(page.Where(r => TimeOf(r) is not { } at || at >= ReadFrom));

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
    /// <param name="publishRecentAsync">Writes what the store does not already hold, returning how many, or null on failure.</param>
    /// <returns>How many records were written, not how many were compared.</returns>
    private async Task<PagedCrawlOutcome> PublishUncrawledAsync<T>(
        RecentRecords<T> recent,
        DateTime crawledFrom,
        Func<IConnectorPublisher, List<T>, Task<int?>> publishRecentAsync)
        where T : ProcessableDocumentBase
    {
        if (Publisher is not { IsAvailable: true } publisher)
            return new PagedCrawlOutcome(0, true);

        var uncrawled = recent.Uncrawled(crawledFrom);
        if (uncrawled.Count == 0)
            return new PagedCrawlOutcome(0, true);

        return await publishRecentAsync(publisher, uncrawled) is { } written
            ? new PagedCrawlOutcome(written, true)
            : new PagedCrawlOutcome(0, false);
    }

    /// <summary>
    ///     This catch-up's reconcile window: <see cref="FullReconcileWindow"/> once
    ///     <see cref="FullReconcileEvery"/> has passed since the last full one, otherwise
    ///     <see cref="RecentReconcileWindow"/> below the crawl's resume point.
    /// </summary>
    private async Task<RecentRecords<Treatment>> RecentTreatmentsForAsync(DateTime crawlFrom, DateTime now)
    {
        var lastFull = await LastFullReconcileAsync();
        return lastFull is { } last && last <= now && now - last < FullReconcileEvery
            ? new RecentRecords<Treatment>(crawlFrom - RecentReconcileWindow, Full: false, TreatmentTime)
            : new RecentRecords<Treatment>(now - FullReconcileWindow, Full: true, TreatmentTime);
    }

    private static DateTime? TreatmentTime(Treatment treatment) => ParseCreatedAt(treatment.CreatedAt)?.UtcDateTime;

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
        RecentRecords<Treatment> recent, DateTime crawledFrom, CancellationToken cancellationToken)
    {
        if (Publisher is not { IsAvailable: true } publisher || recent.Records.Count == 0)
            return new PagedCrawlOutcome(0, true);

        var republished = await PublishUncrawledAsync(recent, crawledFrom,
            async (p, uncrawled) => await p.Treatments.PublishRecentTreatmentsAsync(
                uncrawled, ConnectorSource, await TreatmentPublishOriginAsync(), cancellationToken));
        if (!republished.Success)
            return republished;

        var read = new Dictionary<string, DateTime>();
        foreach (var treatment in recent.Records.Where(t => t.Id is { Length: > 0 }))
        {
            if (TreatmentTime(treatment) is not { } at)
                continue;

            read.TryAdd(treatment.Id!, at);
            if (TreatmentClientId.Of(treatment) is { Length: > 0 } clientId)
                read.TryAdd(clientId, at);
        }

        await DeleteTreatmentsGoneUpstreamAsync(publisher.Treatments, recent.WindowStart, read, cancellationToken);

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
    /// <param name="read">The ids and client ids the read returned, each with its created_at.</param>
    private async Task DeleteTreatmentsGoneUpstreamAsync(
        ITreatmentPublisher treatments,
        DateTime windowStart,
        IReadOnlyDictionary<string, DateTime> read,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var stored = await treatments.GetStoredTreatmentIdsAsync(ConnectorSource, windowStart, now, cancellationToken);
        var candidates = stored
            .Where(s => !read.ContainsKey(s.Key) && !IsConfirmedPresent(s.Key, now))
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

                if (!await TreatmentExistsUpstreamAsync(canary.Key, canary.Value))
                {
                    _logger.LogWarning(
                        "[{ConnectorSource}] The source could not find a treatment it had just returned by id; deleting none",
                        ConnectorSource);
                    return;
                }

                foreach (var (id, at) in kind)
                {
                    if (await TreatmentExistsUpstreamAsync(id, at))
                        ConfirmedPresent[PresenceKey(id)] = now;
                    else
                        gone.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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

    private string PresenceKey(string id) => $"{_resolvedBaseUrl}\n{id}";

    private bool IsConfirmedPresent(string id, DateTime now)
    {
        foreach (var (key, at) in ConfirmedPresent)
        {
            if (now - at > ConfirmedPresentFor)
                ConfirmedPresent.TryRemove(key, out _);
        }

        return ConfirmedPresent.ContainsKey(PresenceKey(id));
    }

    /// <summary>
    ///     Looks a treatment up by the field its id came from. A treatment is read under its <c>_id</c>,
    ///     but a row can still be stored under an uploader's own <c>id</c> (Trio's UUID) if no read has
    ///     moved it since (<see cref="ITreatmentPublisher.PublishTreatmentsAsync"/>). An id that is not
    ///     an ObjectId is therefore looked up by <c>id</c>. Asking for it by <c>_id</c> is an error on
    ///     Nightscout releases that cast the value to an ObjectId. An ObjectId-shaped id is looked up by
    ///     <c>_id</c> and then by <c>id</c>. Neither field is indexed, so the lookup is bounded to the
    ///     created_at range the treatment can sit in: <paramref name="at"/>, give or take
    ///     <see cref="BackwardTimePager.CreatedAtOffsetEnvelope"/> and <see cref="ReconcileReadMargin"/>.
    /// </summary>
    private async Task<bool> TreatmentExistsUpstreamAsync(string id, DateTime at)
    {
        var reach = BackwardTimePager.CreatedAtOffsetEnvelope + ReconcileReadMargin;
        return (IsObjectId(id) && await AnyAsync("_id")) || await AnyAsync("id");

        async Task<bool> AnyAsync(string field)
        {
            const string operation = "LookUpTreatment";
            var url = BuildCreatedAtUrl("treatments", at - reach, at + reach)
                + $"&find[{field}]={Uri.EscapeDataString(id)}";

            var found = await FetchDataAsync<Treatment[]>(url, operation) ?? throw FetchFailed(operation);
            return found.Length > 0;
        }
    }

    private static bool IsObjectId(string id) => id.Length == 24 && id.All(char.IsAsciiHexDigit);
    protected override async Task<IEnumerable<Profile>> FetchProfilesAsync()
    {
        var profiles = await FetchDataAsync<Profile[]>(
            "/api/v1/profile.json",
            "FetchProfiles");

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

    private async Task<IEnumerable<Food>> FetchFoodAsync()
    {
        var foods = await FetchDataAsync<Food[]>(
            $"/api/v1/food.json?count={_currentConfig.MaxCount}",
            "FetchFood");

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

    private async Task<T?> FetchDataAsync<T>(string url, string operationName) where T : class
    {
        await _rateLimitingStrategy.ApplyDelayAsync(0);

        return await ExecuteWithRetryAsync(
            async () => await FetchDataCoreAsync<T>(url),
            _retryDelayStrategy,
            maxRetries: _currentConfig.MaxRetryAttempts,
            operationName: operationName);
    }

    private async Task<T?> FetchDataCoreAsync<T>(string url) where T : class
    {
        var headers = GetAuthHeaders();
        var absoluteUrl = _resolvedBaseUrl != null ? $"{_resolvedBaseUrl}{url}" : url;
        var response = await GetWithHeadersAsync(absoluteUrl, headers);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errorContent}",
                null,
                response.StatusCode);
        }

        return await DeserializeResponseAsync<T>(response);
    }

    private string BuildEntriesUrl(DateTime? from, DateTime? to, int count)
    {
        var url = $"/api/v1/entries.json?count={count}";

        if (from.HasValue)
        {
            var fromMs = new DateTimeOffset(from.Value, TimeSpan.Zero).ToUnixTimeMilliseconds();
            url += $"&find[date][$gte]={fromMs}";
        }

        if (to.HasValue)
        {
            var toMs = new DateTimeOffset(to.Value, TimeSpan.Zero).ToUnixTimeMilliseconds();
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

    private Dictionary<string, string> GetAuthHeaders()
    {
        return new Dictionary<string, string>
        {
            ["api-secret"] = _apiSecretHash ?? ComputeApiSecretHash(_currentConfig.ApiSecret)
        };
    }

    internal static string ComputeApiSecretHash(string apiSecret)
    {
        if (IsAlreadySha1Hash(apiSecret))
            return apiSecret.ToLowerInvariant();

        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexStringLower(bytes);
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
        IRateLimitingStrategy rateLimitingStrategy,
        IConnectorRegistration<NightscoutConnectorConfiguration> registration,
        IConnectorPublisher? publisher = null,
        TimeProvider? timeProvider = null
    ) : base(httpClient, serverResolver, logger, retryDelayStrategy, rateLimitingStrategy, registration, publisher, timeProvider) { }
}
