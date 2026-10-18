using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Eversense.Configurations;
using Nocturne.Connectors.Eversense.Mappers;
using Nocturne.Connectors.Eversense.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Eversense.Services;

/// <summary>
///     Connector service for Eversense Now data source.
///     Writes SensorGlucose records from the Eversense care API the Eversense NOW follower app reads:
///     the followed patient's latest reading from the following-patient list, plus the glucose history
///     since the last stored reading from the user-event feed.
/// </summary>
public class EversenseConnectorService : BaseConnectorService<EversenseConnectorConfiguration>
{
    private readonly EversenseSensorGlucoseMapper _mapper;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly EversenseAuthTokenProvider _tokenProvider;

    public EversenseConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<EversenseConnectorConfiguration> serverResolver,
        ILogger<EversenseConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        EversenseAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, serverResolver, logger, publisher)
    {
        _retryDelayStrategy =
            retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _tokenProvider =
            tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _mapper = new EversenseSensorGlucoseMapper(logger);
    }

    protected override string ConnectorSource => DataSources.EversenseConnector;
    public override string ServiceName => "Eversense Now";

    /// <summary>
    ///     Selects the appropriate patient from the patient list.
    ///     If only one patient, auto-selects. If multiple, matches by username (case-insensitive).
    /// </summary>
    public static EversensePatientDatum? SelectPatient(
        IReadOnlyList<EversensePatientDatum> patients,
        string? patientUsername)
    {
        if (patients.Count == 0)
            return null;

        if (patients.Count == 1)
            return patients[0];

        // Multiple patients require a configured username
        if (string.IsNullOrEmpty(patientUsername))
            return null;

        return patients.FirstOrDefault(p =>
            string.Equals(p.UserName, patientUsername, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Performs sync, publishing at most one SensorGlucose record from the latest reading.
    /// </summary>
    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        EversenseConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { Success = true };

        var activeTypes = ResolveActiveTypes(request, config);
        if (!activeTypes.Contains(SyncDataType.Glucose))
        {
            return result;
        }

        if (string.IsNullOrEmpty(await _tokenProvider.GetValidTokenAsync(config, cancellationToken)))
        {
            TrackFailedAuthentication(_tokenProvider.SignInFailureReason);
            return AuthenticationFailedResult();
        }

        try
        {
            var patients = await FetchPatientListAsync(config, cancellationToken);

            if (patients == null)
            {
                // Token acquisition or the API call failed (already tracked). Surface as unhealthy
                // so the failure is visible rather than looking like a successful empty sync.
                result.Success = false;
                result.Errors.Add(
                    "Could not reach Eversense. Nocturne will try again at the next sync.");
                return result;
            }

            if (patients.Count == 0)
            {
                result.Success = false;
                result.Errors.Add(
                    "Connected to Eversense, but this account is not following anyone in Eversense NOW. " +
                    "In the Eversense NOW app, have the sensor wearer invite this account as a follower and " +
                    "accept the invite, then sync again.");
                return result;
            }

            _logger.LogInformation(
                "[{ConnectorSource}] Retrieved {Count} patient(s) from Eversense",
                ConnectorSource,
                patients.Count);

            var patient = SelectPatient(patients, config.PatientUsername);

            if (patient == null)
            {
                // Multiple followed patients and none matched the configured username.
                result.Success = false;
                result.Errors.Add(
                    "This Eversense account follows multiple people (" +
                    string.Join(", ", patients.Select(p => p.UserName)) +
                    "). Set the patient username to the one you want to sync, then sync again.");
                return result;
            }

            var history = await FetchHistoryAsync(config, patient, request, cancellationToken);
            var records = MergeReadings(history, _mapper.Map(patient));
            if (records.Count == 0)
            {
                _logger.LogDebug(
                    "[{ConnectorSource}] No readings for patient {Patient}",
                    ConnectorSource,
                    patient.UserName);
                return result;
            }

            _logger.LogInformation(
                "[{ConnectorSource}] Publishing {Count} reading(s), {HistoryCount} from history",
                ConnectorSource,
                records.Count,
                history.Count);

            await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
                records, PublishSensorGlucoseDataAsync, config, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            RecordSyncError(result, ex);
        }

        return result;
    }

    /// <summary>
    ///     How far back a sync without a resume point looks, matching the Eversense NOW graph.
    /// </summary>
    internal static readonly TimeSpan DefaultHistoryWindow = TimeSpan.FromHours(4);

    /// <summary>
    ///     The span of one history request. Kept to a day so a long catch-up is many small reads
    ///     rather than one the care API may refuse or truncate.
    /// </summary>
    internal static readonly TimeSpan HistoryChunk = TimeSpan.FromDays(1);

    /// <summary>
    ///     A first background sync reaches back one day; a longer import is a manual historical sync,
    ///     bounded by <see cref="EversenseConnectorConfiguration.MaxHistoryDays"/>.
    /// </summary>
    protected override DateTime? InitialSyncFloor => DateTime.UtcNow.AddDays(-1);

    /// <summary>
    ///     The request windows for a sync from <paramref name="from"/> to <paramref name="to"/>, oldest
    ///     first. The end runs five minutes past now, as the NOW app asks, so a reading stamped by a
    ///     transmitter clock slightly ahead is not cut off.
    /// </summary>
    internal static IReadOnlyList<(DateTime Start, DateTime End)> HistoryWindows(
        DateTime? from, DateTime? to, DateTime now)
    {
        var end = to ?? now.AddMinutes(5);
        var floor = now.AddDays(-EversenseConnectorConfiguration.MaxHistoryDays);
        var start = from ?? now - DefaultHistoryWindow;
        if (start < floor) start = floor;

        var windows = new List<(DateTime, DateTime)>();
        for (var windowStart = start; windowStart < end; windowStart += HistoryChunk)
        {
            var windowEnd = windowStart + HistoryChunk;
            windows.Add((windowStart, windowEnd < end ? windowEnd : end));
        }

        return windows;
    }

    /// <summary>
    ///     The latest reading wins over a history row at the same instant, since only it carries a trend.
    /// </summary>
    internal static List<SensorGlucose> MergeReadings(IEnumerable<SensorGlucose> history, SensorGlucose? latest)
    {
        var byMills = new Dictionary<long, SensorGlucose>();
        foreach (var record in history)
            byMills[record.Mills] = record;
        if (latest != null)
            byMills[latest.Mills] = latest;

        return byMills.Values.OrderBy(r => r.Mills).ToList();
    }

    /// <summary>
    ///     Glucose history for <paramref name="patient"/> over the request's window. A failed window
    ///     ends the fetch and keeps what was read before it, so the latest reading is still published
    ///     when the history feed is unavailable.
    /// </summary>
    private async Task<IReadOnlyList<SensorGlucose>> FetchHistoryAsync(
        EversenseConnectorConfiguration config,
        EversensePatientDatum patient,
        SyncRequest request,
        CancellationToken cancellationToken)
    {
        if (patient.UserId <= 0)
        {
            _logger.LogWarning(
                "[{ConnectorSource}] Followed patient has no user id; skipping glucose history",
                ConnectorSource);
            return [];
        }

        var token = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
        if (string.IsNullOrEmpty(token))
            return [];

        var dataBaseUrl = EversenseConstants.DataBaseUrl(config.Server);
        var records = new List<SensorGlucose>();
        foreach (var (start, end) in HistoryWindows(request.From, request.To, DateTime.UtcNow))
        {
            var url = $"{dataBaseUrl}{EversenseConstants.Endpoints.GetFollowingUserSensorGlucose}" +
                      $"?UserID={patient.UserId}" +
                      $"&startDate={Uri.EscapeDataString(start.ToString(EversenseConstants.HistoryDateFormat, CultureInfo.InvariantCulture))}" +
                      $"&endDate={Uri.EscapeDataString(end.ToString(EversenseConstants.HistoryDateFormat, CultureInfo.InvariantCulture))}";

            var events = await ExecuteWithRetryAsync(
                async () =>
                {
                    using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"HTTP {(int)response.StatusCode} {response.StatusCode}",
                            null,
                            response.StatusCode);

                    return await DeserializeResponseAsync<List<EversenseUserEvent>>(response, cancellationToken) ?? [];
                },
                _retryDelayStrategy,
                reAuthenticateOnUnauthorized: async () =>
                {
                    _tokenProvider.InvalidateToken();
                    var newToken = await _tokenProvider.GetValidTokenAsync(config);
                    if (string.IsNullOrEmpty(newToken)) return false;
                    token = newToken;
                    return true;
                },
                maxRetries: config.MaxRetryAttempts,
                operationName: "FetchEversenseGlucoseHistory"
            );

            if (events == null)
            {
                _logger.LogWarning(
                    "[{ConnectorSource}] Glucose history unavailable from {Start:O}; publishing what was read before it",
                    ConnectorSource,
                    start);
                break;
            }

            records.AddRange(_mapper.MapHistory(events));
        }

        return records;
    }

    /// <summary>
    ///     Calls the Eversense data API to retrieve the following-patient list with current glucose values.
    /// </summary>
    private async Task<List<EversensePatientDatum>?> FetchPatientListAsync(
        EversenseConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("[{ConnectorSource}] No valid token available for data fetch", ConnectorSource);
            TrackFailedAuthentication(_tokenProvider.SignInFailureReason);
            return null;
        }

        var dataBaseUrl = EversenseConstants.DataBaseUrl(config.Server);
        var url = $"{dataBaseUrl}{EversenseConstants.Endpoints.GetFollowingPatientList}";

        var result = await ExecuteWithRetryAsync(
            async () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"HTTP {(int)response.StatusCode} {response.StatusCode}",
                        null,
                        response.StatusCode);
                }

                var patients = await DeserializeResponseAsync<List<EversensePatientDatum>>(response, cancellationToken);
                return ConnectorFaults.NonNullRecords(patients, _logger, ConnectorSource, "Eversense patient").ToList();
            },
            _retryDelayStrategy,
            reAuthenticateOnUnauthorized: ReauthenticateWith(_tokenProvider, config, t => token = t, cancellationToken),
            maxRetries: config.MaxRetryAttempts,
            operationName: "FetchEversensePatientList",
            cancellationToken: cancellationToken
        );

        return result;
    }
}
