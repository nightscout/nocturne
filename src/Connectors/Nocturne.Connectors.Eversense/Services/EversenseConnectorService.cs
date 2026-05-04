using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Eversense.Configurations;
using Nocturne.Connectors.Eversense.Mappers;
using Nocturne.Connectors.Eversense.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Eversense.Services;

/// <summary>
///     Connector service for Eversense Now data source.
///     Writes SensorGlucose records directly from the Eversense following-patient API.
///     Each sync produces at most one reading since the API only returns the latest glucose value.
/// </summary>
public class EversenseConnectorService : BaseConnectorService<EversenseConnectorConfiguration>
{
    private readonly EversenseSensorGlucoseMapper _mapper;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly EversenseAuthTokenProvider _tokenProvider;

    public EversenseConnectorService(
        HttpClient httpClient,
        ILogger<EversenseConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        EversenseAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, logger, publisher)
    {
        _retryDelayStrategy =
            retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _tokenProvider =
            tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _mapper = new EversenseSensorGlucoseMapper(logger);
    }

    protected override string ConnectorSource => DataSources.EversenseConnector;
    public override string ServiceName => "Eversense Now";
    public override List<SyncDataType> SupportedDataTypes => [SyncDataType.Glucose];

    public override async Task<bool> AuthenticateAsync()
    {
        var token = await _tokenProvider.GetValidTokenAsync();
        if (token == null)
        {
            TrackFailedRequest("Failed to get valid token");
            return false;
        }

        TrackSuccessfulRequest();
        return true;
    }

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
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null
    )
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        var enabledTypes = config.GetEnabledDataTypes(SupportedDataTypes);
        if (!enabledTypes.Contains(SyncDataType.Glucose))
        {
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        try
        {
            var patient = await FetchSelectedPatientAsync(config, cancellationToken);

            if (patient == null)
            {
                result.EndTime = DateTimeOffset.UtcNow;
                return result;
            }

            if (!patient.IsTransmitterConnected)
            {
                _logger.LogDebug(
                    "[{ConnectorSource}] Transmitter not connected for patient {Patient}, skipping",
                    ConnectorSource,
                    patient.UserName);
                result.EndTime = DateTimeOffset.UtcNow;
                return result;
            }

            var sg = _mapper.Map(patient);
            if (sg == null)
            {
                _logger.LogDebug(
                    "[{ConnectorSource}] Mapper returned null for patient {Patient}",
                    ConnectorSource,
                    patient.UserName);
                result.EndTime = DateTimeOffset.UtcNow;
                return result;
            }

            var success = await PublishSensorGlucoseDataAsync([sg], config, cancellationToken);
            result.ItemsSynced[SyncDataType.Glucose] = 1;
            result.LastEntryTimes[SyncDataType.Glucose] = sg.Timestamp;

            if (!success)
            {
                result.Success = false;
                result.Errors.Add("SensorGlucose publish failed");
            }
            else
            {
                _logger.LogInformation(
                    "[{ConnectorSource}] Synced 1 SensorGlucose record ({Mgdl} mg/dL at {Timestamp})",
                    ConnectorSource,
                    sg.Mgdl,
                    sg.Timestamp);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ConnectorSource}] Error during Eversense sync", ConnectorSource);
            result.Success = false;
            result.Errors.Add($"Sync error: {ex.Message}");
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>
    ///     Fetches the patient list from the Eversense data API and selects the configured patient.
    /// </summary>
    private async Task<EversensePatientDatum?> FetchSelectedPatientAsync(
        EversenseConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var patients = await FetchPatientListAsync(config, cancellationToken);
        if (patients == null || patients.Count == 0)
        {
            _logger.LogWarning("[{ConnectorSource}] No patients returned from Eversense API", ConnectorSource);
            return null;
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} patient(s) from Eversense",
            ConnectorSource,
            patients.Count);

        var selected = SelectPatient(patients, config.PatientUsername);

        if (selected == null && patients.Count > 1)
        {
            _logger.LogWarning(
                "[{ConnectorSource}] Multiple patients found but no PatientUsername configured or no match. " +
                "Available patients: {Patients}",
                ConnectorSource,
                string.Join(", ", patients.Select(p => p.UserName)));
        }

        return selected;
    }

    /// <summary>
    ///     Calls the Eversense data API to retrieve the following-patient list with current glucose values.
    /// </summary>
    private async Task<List<EversensePatientDatum>?> FetchPatientListAsync(
        EversenseConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetValidTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("[{ConnectorSource}] No valid token available for data fetch", ConnectorSource);
            TrackFailedRequest("No valid token");
            return null;
        }

        var dataBaseUrl = GetDataBaseUrl(config.Server);
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
                return patients ?? [];
            },
            _retryDelayStrategy,
            reAuthenticateOnUnauthorized: async () =>
            {
                _tokenProvider.InvalidateToken();
                var newToken = await _tokenProvider.GetValidTokenAsync();
                if (string.IsNullOrEmpty(newToken)) return false;
                token = newToken;
                return true;
            },
            operationName: "FetchEversensePatientList"
        );

        return result;
    }

    private static string GetDataBaseUrl(string server) => server.ToUpperInvariant() switch
    {
        "US" => EversenseConstants.Servers.UsData,
        _ => throw new ArgumentOutOfRangeException(nameof(server), server, "Unsupported Eversense server region")
    };
}
