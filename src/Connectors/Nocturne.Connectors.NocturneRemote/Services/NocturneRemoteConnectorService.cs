using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.NocturneRemote.Services;

/// <summary>
///     Connector service that pulls data from a remote Nocturne V4 instance.
///     Uses direct grant bearer token authentication and the V4 paginated API.
/// </summary>
public class NocturneRemoteConnectorService : BaseConnectorService<NocturneRemoteConnectorConfiguration>
{
    private string? _resolvedBaseUrl;
    private Dictionary<string, string>? _authHeaders;

    public NocturneRemoteConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<NocturneRemoteConnectorConfiguration> serverResolver,
        ILogger<NocturneRemoteConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IConnectorPublisher? publisher = null,
        TimeProvider? timeProvider = null
    )
        : base(httpClient, serverResolver, logger, retryDelayStrategy, publisher, timeProvider)
    {
    }

    public override string ServiceName => "Nocturne Remote";

    /// <summary>
    ///     The remote is another Nocturne instance, so a type with nothing stored takes everything
    ///     it holds.
    /// </summary>
    protected override DateTime? InitialSyncFloor => null;

    /// <summary>
    ///     Resolves the run's base URL and bearer header, then asks the remote whether it accepts the
    ///     credential. Answers with the result that ends the run, or null for a run that may proceed.
    /// </summary>
    /// <remarks>
    ///     The question is put to a data endpoint, so of the answers it can get only a 401 is this
    ///     connector's to report: a 403 is a grant the tenant scoped deliberately, and any other
    ///     status is that endpoint being unwell. Failing the run on either sends the tenant to
    ///     re-authorize a credential that was never the problem and costs them every other type; both
    ///     are left to the per-type crawls, which reach the same endpoint under the tenant's retry
    ///     budget and scope the failure to the types that asked for it.
    ///     <para>
    ///     A remote that answers nothing at all is neither, and is not per-endpoint either: every
    ///     enabled type reads the same silent host, so there is nothing for a crawl to scope and each
    ///     of them would spend the client's whole timeout rediscovering it. That ends the run here
    ///     instead, under its own message rather than the credential's.
    ///     </para>
    ///     <para>
    ///     One attempt, not the tenant's budget: a 401 is terminal in
    ///     <see cref="BaseConnectorService{TConfig}.ExecuteWithRetryAsync{T}"/> and never retried, and
    ///     every other answer proceeds regardless, so a second attempt cannot change what this
    ///     returns — it can only spend another request on a remote already known to be unwell.
    ///     </para>
    /// </remarks>
    private async Task<SyncResult?> AuthenticateWithConfigAsync(
        NocturneRemoteConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        ResolveConfiguration(config);

        // Captured as in FetchOrFailAsync, and for the same reason: the retry loop answers with
        // nothing rather than with the status it ended on.
        HttpStatusCode? answered = null;
        string? transportFailure = null;

        try
        {
            await ExecuteWithRetryAsync<HttpStatusCode>(
                async () =>
                {
                    try
                    {
                        var response = await GetWithHeadersAsync(
                            BuildAbsoluteUrl($"{NocturneRemoteConstants.SensorGlucose}?limit=1"),
                            _authHeaders,
                            cancellationToken);

                        answered = response.StatusCode;

                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException(
                                $"HTTP {(int)response.StatusCode} {response.StatusCode}: " +
                                await ReadFailureBodyAsync(response, config, cancellationToken),
                                null,
                                response.StatusCode);

                        return response.StatusCode;
                    }
                    catch (Exception ex) when (IsTransportFailure(ex))
                    {
                        transportFailure = DescribeTransportFailure(ex);
                        throw;
                    }
                },
                maxRetries: 1,
                operationName: "auth check",
                cancellationToken: cancellationToken);
        }
        catch (HttpRequestException)
        {
            // A status the retry loop re-threw; `answered` holds it.
        }
        // The retry loop re-throws every cancellation, and a client timeout reaches it as one. Only
        // the caller's own token means the run was withdrawn and must travel; the rest is the remote
        // falling silent.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UnansweredResult(transportFailure);
        }

        if (answered is null && transportFailure is not null)
            return UnansweredResult(transportFailure);

        if (answered != HttpStatusCode.Unauthorized)
            return null;

        _logger.LogError(
            "[{ConnectorSource}] The remote Nocturne instance at {Url} rejected the connector's credential",
            ConnectorSource,
            _resolvedBaseUrl);
        return AuthenticationFailedResult();
    }

    /// <summary>
    ///     The result of a run whose remote accepted the connection and then said nothing. Carries
    ///     the same two fields as
    ///     <see cref="BaseConnectorService{TConfig}.AuthenticationFailedResult"/> and for the same
    ///     reasons, but must not borrow its wording: the credential is not what failed.
    /// </summary>
    private SyncResult UnansweredResult(string? detail = null)
    {
        var unanswered = $"The remote Nocturne instance at {_resolvedBaseUrl} did not answer" +
                         (detail is null ? string.Empty : $": {detail}");

        _logger.LogError("[{ConnectorSource}] {Detail}", ConnectorSource, unanswered);

        return new SyncResult
        {
            Success = false,
            Message = unanswered,
            Errors = { unanswered },
        };
    }

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        NocturneRemoteConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        // Both entry points converge here, and this is the only one of them holding the run's own
        // configuration — the background one authenticates through the parameterless overload above,
        // which has none.
        if (await AuthenticateWithConfigAsync(config, cancellationToken) is { } refused)
            return refused;

        var result = new SyncResult { Success = true };

        var activeTypes = ResolveActiveTypes(request, config);

        // Glucose resolves its bound through GlucoseFromAsync, and every other type through
        // ResumeFrom from the request and its own resume point, because each is fetched from its
        // own endpoint. An explicit range (request.To set, as a cursor reset sends) bypasses the
        // catch-up bounds entirely.
        //
        // Each bound is resolved inside the loop's error boundary, so a watermark the publisher
        // cannot answer fails only the type whose bound it was.
        var openEnded = request.To is null;

        async Task<DateTime?> BoundAsync(SyncDataType type, Func<Task<DateTime?>> resumePoint) =>
            openEnded ? ResumeFrom(request, await ResumePointAsync(type.ToString(), await resumePoint())) : request.From;

        Task<DateTime?> TreatmentFromAsync(SyncDataType type) =>
            BoundAsync(type, () => CalculateTreatmentSinceTimestampAsync(config, type));

        Task CrawlV4Async<T>(
            SyncDataType type, string endpoint, DateTime? from)
            where T : class, IV4Record =>
            CrawlAsync(type, from, request.To,
                (pageFrom, pageTo) => FetchPagesAsync<T>(endpoint, pageFrom, pageTo, config, cancellationToken),
                r => r.Timestamp,
                page => PublishByRouteAsync(ImportHelper.PrepareForImport(page), config, cancellationToken),
                config, result);

        foreach (var type in activeTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await (type switch
                {
                    SyncDataType.Glucose => CrawlV4Async<SensorGlucose>(type, NocturneRemoteConstants.SensorGlucose, await GlucoseFromAsync(request, type.ToString())),
                    SyncDataType.ManualBG => CrawlV4Async<BGCheck>(type, NocturneRemoteConstants.BGChecks, await TreatmentFromAsync(type)),
                    SyncDataType.Boluses => CrawlV4Async<Bolus>(type, NocturneRemoteConstants.Boluses, await TreatmentFromAsync(type)),
                    SyncDataType.CarbIntake => CrawlV4Async<CarbIntake>(type, NocturneRemoteConstants.CarbIntake, await TreatmentFromAsync(type)),
                    SyncDataType.BolusCalculations => CrawlV4Async<BolusCalculation>(type, NocturneRemoteConstants.BolusCalculations, await TreatmentFromAsync(type)),
                    SyncDataType.Notes => CrawlV4Async<Note>(type, NocturneRemoteConstants.Notes, await TreatmentFromAsync(type)),
                    SyncDataType.DeviceEvents => CrawlV4Async<DeviceEvent>(type, NocturneRemoteConstants.DeviceEvents, await TreatmentFromAsync(type)),
                    SyncDataType.StateSpans => SyncStateSpansAsync(await BoundAsync(type, () => CalculateStateSpanSinceTimestampAsync(config)), request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.Profiles => SyncProfilesAsync(config, result, activeTypes, cancellationToken),
                    SyncDataType.DeviceStatus => SyncDeviceStatusAsync(await BoundAsync(type, () => CalculateDeviceStatusCatchUpSinceAsync(config)), request.To, config, result, cancellationToken),
                    SyncDataType.Activity => SyncActivityAsync(openEnded ? ResumeFrom(request, await CalculateActivityCatchUpSinceAsync(config)) : request.From, request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.Food => SyncFoodAsync(config, result, activeTypes, cancellationToken),
                    _ => Task.CompletedTask
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                RecordTypeSyncError(result, type.ToString(), ex);
            }
        }

        return result;
    }

    /// <summary>
    ///     Crawls one type through <see cref="BaseConnectorService{TConfig}.CrawlAndPublishAsync{T}"/>,
    ///     keyed by the type, and records its count.
    /// </summary>
    private async Task<PagedCrawlOutcome> CrawlAsync<T>(
        SyncDataType type, DateTime? from, DateTime? to,
        Func<DateTime?, DateTime?, IAsyncEnumerable<T[]>> pages,
        Func<T, DateTime?> timeOf,
        Func<List<T>, Task<bool>> publish,
        NocturneRemoteConnectorConfiguration config,
        SyncResult result)
    {
        var outcome = await CrawlAndPublishAsync(
            type.ToString(), from, to, pages, timeOf, page => publish(page.ToList()), config.MaxCount);

        RecordPublishOutcome(result, type, outcome.Count, outcome.Success);
        return outcome;
    }

    private async Task SyncStateSpansAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var spans = await CrawlAsync(SyncDataType.StateSpans, from, to,
            (pageFrom, pageTo) => FetchPagesAsync<StateSpan>(NocturneRemoteConstants.StateSpans, pageFrom, pageTo, config, ct),
            s => s.StartTimestamp,
            page => PublishByRouteAsync(page, config, ct),
            config, result);

        // A crawl that resumed below its bound replayed spans there, so the activations reach as far.
        var activationsFrom = spans.Oldest < from ? spans.Oldest : from;
        if (await FetchAppliedProfileActivationsAsync(activationsFrom, to, config, ct) is { } activations)
            await PublishRecordTypeAsync(result, activeTypes,
                activations.Select(ToProfileActivation).ToList(), config, ct,
                "profile activations");
    }

    /// <summary>
    ///     The activations in force on the remote during the window, or null from a remote that predates
    ///     them, whose profile switches arrive as the Profile spans <see cref="SyncStateSpansAsync"/>
    ///     already replayed.
    /// </summary>
    private async Task<List<ProfileActivationDto>?> FetchAppliedProfileActivationsAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        var url = $"{NocturneRemoteConstants.ProfileActivations}?applied=true";
        if (from.HasValue)
            url += $"&from={from.Value.ToUniversalTime():o}";
        if (to.HasValue)
            url += $"&to={to.Value.ToUniversalTime():o}";

        try
        {
            using var response = await GetWithHeadersAsync(BuildAbsoluteUrl(url), _authHeaders, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (response.IsSuccessStatusCode
                && await DeserializeResponseAsync<List<ProfileActivationDto>>(response, ct) is { } activations)
                return activations;
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            _logger.LogDebug(ex, "[{ConnectorSource}] Retrying the profile activation fetch", ConnectorSource);
        }

        return await FetchOrFailAsync<List<ProfileActivationDto>>(
            url, NocturneRemoteConstants.ProfileActivations, config, ct);
    }

    /// <summary>
    ///     A remote activation as this instance stores it: keyed by the remote's id, with the end the
    ///     remote settled it with, and a temporary one's length.
    /// </summary>
    private static ProfileActivation ToProfileActivation(ProfileActivationDto remote) => new()
    {
        StartTimestamp = remote.StartMills.ToUtcDateTime(),
        EndTimestamp = remote.EndMills is { } end ? end.ToUtcDateTime() : null,
        RequestedDurationMs = remote.IsTemporary && remote.EndMills is { } temporaryEnd
            ? temporaryEnd - remote.StartMills
            : null,
        BaseProfileName = remote.BaseProfileName,
        Percentage = remote.Percentage,
        TimeshiftMs = (long)Math.Round(remote.TimeshiftHours * 3_600_000),
        App = remote.App,
        SyncIdentifier = remote.Id.ToString(),
    };

    private async Task SyncProfilesAsync(
        NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchAllAsync<Profile>(
            NocturneRemoteConstants.ProfileRecords, null, null, config, ct);

        await PublishRecordTypeAsync(result, activeTypes,
            records, config, ct);
    }

    /// <remarks>
    ///     DeviceStatus uses the v1 API because the publisher only supports the legacy model. The
    ///     remote instance exposes v1 compatibility endpoints.
    /// </remarks>
    private Task SyncDeviceStatusAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        CancellationToken ct) =>
        CrawlAsync(SyncDataType.DeviceStatus, from, to,
            (pageFrom, pageTo) => FetchV1DeviceStatusPagesAsync(pageFrom, pageTo, config, ct),
            DeviceStatusTime,
            page => PublishByRouteAsync(page, config, ct),
            config, result);

    /// <remarks>
    ///     Accumulated rather than crawled through a low-water mark: the remote's activity list
    ///     takes no time bounds, so a mark could not resume anything.
    /// </remarks>
    private async Task SyncActivityAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchAllAsync<Activity>(
            NocturneRemoteConstants.Activity, from, to, config, ct);

        await PublishRecordTypeAsync(result, activeTypes,
            records, config, ct);
    }

    private async Task SyncFoodAsync(
        NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        // Foods endpoint returns a flat array, not PaginatedResponse
        var foods = await FetchOrFailAsync<Food[]>(
            $"{NocturneRemoteConstants.Foods}?count={config.MaxCount}",
            NocturneRemoteConstants.Foods, config, ct);

        // Food carries no per-record time, so no timestamp selector is supplied.
        await PublishRecordTypeAsync(result, activeTypes,
            foods.ToList(), config, ct);
    }

    /// <summary>
    ///     One payload from the remote instance, fetched under the tenant's retry budget, raising
    ///     <see cref="BaseConnectorService{TConfig}.FetchFailed"/> rather than answering with nothing.
    /// </summary>
    /// <remarks>
    ///     Mirrors the Nightscout connector's fetch layer so both crawls give up on the same
    ///     condition. A transient status is retried; a status the remote keeps returning and a body
    ///     that will not parse are reported as no payload, which becomes the raised failure here. An
    ///     exhausted budget does not reach that point — the retry loop re-throws the last transient
    ///     exception, which already names the status.
    /// </remarks>
    private async Task<T> FetchOrFailAsync<T>(
        string relativeUrl,
        string operationName,
        NocturneRemoteConnectorConfiguration config,
        CancellationToken ct) where T : class
    {
        // Captured rather than logged alone, because the tenant reads the sync card and not the
        // connector logs, and a refused scope is the failure they can actually act on.
        string? refusal = null;
        string? transportFailure = null;
        string? failure = null;

        var payload = await ExecuteWithRetryAsync(
            async () =>
            {
                refusal = null;
                transportFailure = null;
                failure = null;
                try
                {
                    var response = await GetWithHeadersAsync(BuildAbsoluteUrl(relativeUrl), _authHeaders, ct);

                    if (response.IsSuccessStatusCode)
                        return await DeserializeResponseAsync<T>(response, ct);

                    refusal = $"HTTP {(int)response.StatusCode} {response.StatusCode}";
                    throw new HttpRequestException(
                        $"{refusal}: {await ReadFailureBodyAsync(response, config, ct)}",
                        null,
                        response.StatusCode);
                }
                catch (Exception ex) when (IsTransportFailure(ex))
                {
                    transportFailure = DescribeTransportFailure(ex);
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // ExecuteWithRetryAsync deliberately converts parse/unexpected failures to a
                    // null payload. Preserve the cause here so a tenant does not have to inspect
                    // connector logs to distinguish malformed responses from an empty page.
                    failure = DescribeFailure(ex);
                    throw;
                }
            },
            maxRetries: config.MaxRetryAttempts,
            operationName: operationName,
            cancellationToken: ct);

        return payload ?? throw FetchFailed(operationName, refusal ?? transportFailure ?? failure);
    }

    private static bool IsTransportFailure(Exception ex) =>
        ex is HttpRequestException { StatusCode: null } or IOException or TimeoutException;

    private static string DescribeTransportFailure(Exception ex)
        => DescribeFailure(ex);

    private static string DescribeFailure(Exception ex)
    {
        var detail = ex.GetBaseException().Message.Trim();
        return detail.Length <= 200 ? detail : detail[..200] + "...";
    }

    /// <summary>
    ///     As much of a failed response's body as is worth quoting back to the tenant.
    /// </summary>
    /// <remarks>
    ///     The body ends up in the connector's last-error message and its logs, and the remote is the
    ///     tenant's own instance behind their own proxy — an error page that echoes the request would
    ///     otherwise put the bearer token there. The token is removed and the rest is clamped, since
    ///     what identifies the failure is at the front of the body.
    /// </remarks>
    private static async Task<string> ReadFailureBodyAsync(
        HttpResponseMessage response,
        NocturneRemoteConnectorConfiguration config,
        CancellationToken ct)
    {
        const int maxQuotedBody = 200;

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!string.IsNullOrEmpty(config.AccessToken))
            body = body.Replace(config.AccessToken, "[redacted]", StringComparison.Ordinal);

        return body.Length <= maxQuotedBody ? body : body[..maxQuotedBody] + "...";
    }

    /// <summary>
    ///     The pages of a V4 paginated endpoint, newest first. <typeparamref name="T"/> spans both the
    ///     V4 records and the legacy models the publishers still take, which page identically.
    /// </summary>
    /// <remarks>
    ///     Each page is fetched under the retry budget, so a raised failure means the page did not
    ///     survive <see cref="BaseConnectorConfiguration.MaxRetryAttempts"/> attempts rather than one
    ///     unlucky moment. See <see cref="BaseConnectorService{TConfig}.FetchFailed"/>. Offset paging over
    ///     a newest-first list repeats a record for each one the remote stores in range mid-crawl, and
    ///     skips one for each it deletes above the crawl's position; nothing re-reads a skipped record
    ///     short of a cursor reset.
    ///     <para>
    ///     A V4 list refuses a range wider than <see cref="V4ApiLimits.MaxDateSpanDays"/>, so a wider
    ///     one is read a window at a time, newest window first. Both bounds are inclusive, so each
    ///     window ends a microsecond below the one above it: the remote stores times to the
    ///     microsecond, and a record on the boundary is read once, in the newer window, whichever way
    ///     its store rounds a bound finer than that.
    ///     </para>
    /// </remarks>
    private async IAsyncEnumerable<T[]> FetchPagesAsync<T>(
        string endpoint, DateTime? from, DateTime? to,
        NocturneRemoteConnectorConfiguration config,
        [EnumeratorCancellation] CancellationToken ct) where T : class
    {
        var widest = TimeSpan.FromDays(V4ApiLimits.MaxDateSpanDays);
        var top = to;
        while (from is { } floor && top is { } windowTop && windowTop - floor > widest)
        {
            await foreach (var page in FetchWindowAsync<T>(endpoint, windowTop - widest, windowTop, config, ct))
                yield return page;
            top = windowTop - widest - TimeSpan.FromMicroseconds(1);
        }

        await foreach (var page in FetchWindowAsync<T>(endpoint, from, top, config, ct))
            yield return page;
    }

    private async IAsyncEnumerable<T[]> FetchWindowAsync<T>(
        string endpoint, DateTime? from, DateTime? to,
        NocturneRemoteConnectorConfiguration config,
        [EnumeratorCancellation] CancellationToken ct) where T : class
    {
        var offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var page = await FetchOrFailAsync<PaginatedResponse<T>>(
                BuildPaginatedUrl(endpoint, from, to, config.MaxCount, offset), endpoint, config, ct);

            // An envelope that parses supplies an empty page rather than a null one, so this is a
            // remote answering with the envelope and no page in it.
            if (page.Data == null)
                throw FetchFailed(endpoint, "response carried no page");

            var items = page.Data.ToArray();
            if (items.Length == 0)
                yield break;

            yield return items;

            offset += items.Length;
            if (offset >= page.Pagination.Total || items.Length < config.MaxCount)
                yield break;
        }
    }

    private async Task<List<T>> FetchAllAsync<T>(
        string endpoint, DateTime? from, DateTime? to,
        NocturneRemoteConnectorConfiguration config, CancellationToken ct) where T : class
    {
        var all = new List<T>();
        await foreach (var page in FetchPagesAsync<T>(endpoint, from, to, config, ct))
            all.AddRange(page);
        return all;
    }

    /// <summary>
    ///     Legacy DeviceStatus pages from the v1 API of the remote instance through
    ///     <see cref="BackwardTimePager.PageAsync{T}"/>. The remote parses the created_at bound into
    ///     a time and compares it against each record's Mills, sorting newest-first by Mills, so a
    ///     bound admits exactly its own instant and the crawl steps on Mills. The created_at a remote
    ///     sends back is its save time on older servers, which on a bulk-loaded remote lies after
    ///     every reading and would pin the bound in place; it is read only when no row has Mills.
    /// </summary>
    private async IAsyncEnumerable<DeviceStatus[]> FetchV1DeviceStatusPagesAsync(
        DateTime? from, DateTime? to,
        NocturneRemoteConnectorConfiguration config,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var pages = BackwardTimePager.PageAsync<DeviceStatus>(
            from,
            to,
            config.MaxCount,
            BackwardTimePager.WidestPageSize(config.MaxCount),
            async (bound, count) =>
            {
                ct.ThrowIfCancellationRequested();
                var statuses = await FetchOrFailAsync<DeviceStatus[]>(
                    BuildV1DeviceStatusUrl(from, bound, count), V1DeviceStatusEndpoint, config, ct);
                return new TimePage<DeviceStatus>(statuses, statuses.Length);
            },
            OldestDeviceStatusTime,
            bound => bound,
            _logger,
            ConnectorSource,
            "devicestatus");

        await foreach (var page in pages)
            if (page.Length > 0)
                yield return page;
    }

    private static DateTime? DeviceStatusTime(DeviceStatus status) =>
        status.Mills > 0
            ? status.Mills.ToUtcDateTime()
            : UploaderTimestamp.ParseUtcDateTime(status.CreatedAt);

    private static DateTime? OldestDeviceStatusTime(DeviceStatus[] statuses)
    {
        var mills = statuses.Where(d => d.Mills > 0).Select(d => (long?)d.Mills).Min();
        if (mills.HasValue)
            return mills.Value.ToUtcDateTime();

        return statuses
            .Select(d => UploaderTimestamp.ParseUtcDateTime(d.CreatedAt))
            .Where(dt => dt.HasValue)
            .Min();
    }

    private static string BuildPaginatedUrl(
        string endpoint, DateTime? from, DateTime? to, int limit, int offset)
    {
        // Newest first is the remote's default; it is asked for because the low-water mark relies on it.
        var url = $"{endpoint}?limit={limit}&offset={offset}&sort=timestamp_desc";

        if (from.HasValue)
            url += $"&from={from.Value.ToUniversalTime():o}";

        if (to.HasValue)
            url += $"&to={to.Value.ToUniversalTime():o}";

        return url;
    }

    private const string V1DeviceStatusEndpoint = "/api/v1/devicestatus.json";

    private static string BuildV1DeviceStatusUrl(DateTime? from, DateTime? to, int count)
    {
        var url = $"{V1DeviceStatusEndpoint}?count={count}";

        if (from.HasValue)
            url += $"&find[created_at][$gte]={from.Value.ToUniversalTime():o}";

        if (to.HasValue)
            url += $"&find[created_at][$lte]={to.Value.ToUniversalTime():o}";

        return url;
    }

    private void ResolveConfiguration(NocturneRemoteConnectorConfiguration config)
    {
        _resolvedBaseUrl = config.ResolveBaseUrl();

        _authHeaders = !string.IsNullOrEmpty(config.AccessToken)
            ? new Dictionary<string, string> { ["Authorization"] = $"Bearer {config.AccessToken}" }
            : null;
    }

    private string BuildAbsoluteUrl(string relativePath)
    {
        return _resolvedBaseUrl != null ? $"{_resolvedBaseUrl}{relativePath}" : relativePath;
    }
}
