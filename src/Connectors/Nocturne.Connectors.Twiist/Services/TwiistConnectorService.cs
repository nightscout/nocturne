using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Mappers;
using Nocturne.Connectors.Twiist.Models;
using Nocturne.Connectors.Twiist.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Twiist.Services;

/// <summary>
/// Connector service for Twiist Insight follower API.
/// Fetches glucose (from binary blobs), insulin doses, and meals.
/// </summary>
public class TwiistConnectorService : BaseConnectorService<TwiistConnectorConfiguration>
{
    private readonly TwiistGlucoseMapper _glucoseMapper;
    private readonly TwiistInsulinMapper _insulinMapper;
    private readonly TwiistMealMapper _mealMapper;
    private readonly IRateLimitingStrategy _rateLimitingStrategy;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly TwiistAuthTokenProvider _tokenProvider;

    public TwiistConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<TwiistConnectorConfiguration> serverResolver,
        ILogger<TwiistConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        TwiistAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null)
        : base(httpClient, serverResolver, logger, publisher)
    {
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _rateLimitingStrategy = rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _glucoseMapper = new TwiistGlucoseMapper(logger);
        _insulinMapper = new TwiistInsulinMapper(logger);
        _mealMapper = new TwiistMealMapper(logger);
    }

    protected override string ConnectorSource => DataSources.TwiistConnector;
    public override string ServiceName => "Twiist Insight";

    public override List<SyncDataType> SupportedDataTypes =>
        [SyncDataType.Glucose, SyncDataType.Boluses, SyncDataType.CarbIntake];

    public override Task<bool> AuthenticateAsync()
    {
        // Auth happens per-tenant inside PerformSyncInternalAsync where config is available
        TrackSuccessfulRequest();
        return Task.FromResult(true);
    }

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        TwiistConnectorConfiguration config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null)
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };
        var enabledTypes = config.GetEnabledDataTypes(SupportedDataTypes);

        try
        {
            var package = await FetchPackageAsync(config, cancellationToken);
            if (package?.Status == null)
            {
                _logger.LogWarning("[{Source}] Twiist returned empty package for PWD {PwdId}",
                    ConnectorSource, config.PwdId);
                result.EndTime = DateTimeOffset.UtcNow;
                return result;
            }

            // Glucose from binary blob
            if (enabledTypes.Contains(SyncDataType.Glucose))
            {
                await SyncGlucoseAsync(package.Status, result, config, cancellationToken);
            }

            // Boluses from insulin history
            if (enabledTypes.Contains(SyncDataType.Boluses))
            {
                await SyncBolusesAsync(package.Status, result, config, cancellationToken);
            }

            // Carbs from meal history
            if (enabledTypes.Contains(SyncDataType.CarbIntake))
            {
                await SyncCarbIntakeAsync(package.Status, result, config, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Twiist sync was canceled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Twiist sync");
            result.Success = false;
            result.Errors.Add($"Sync error: {ex.Message}");
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    private async Task SyncGlucoseAsync(
        TwiistStatus status, SyncResult result,
        TwiistConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var glucoseRecords = TwiistBlobDecoder.ParseGlucoseRecords(status.GlucoseHistory?.Data);
        if (glucoseRecords.Count == 0)
        {
            _logger.LogDebug("[{Source}] No glucose records in package", ConnectorSource);
            return;
        }

        var sensorGlucose = _glucoseMapper.MapGlucoseRecords(
            glucoseRecords, status.Summary?.LastGlucoseTrend).ToList();

        // Apply trend to the most recent reading if available
        if (sensorGlucose.Count > 0 && status.Summary?.LastGlucoseTrend != null)
        {
            var latest = sensorGlucose[^1];
            latest.Direction = TwiistGlucoseMapper.ParseTrend(status.Summary.LastGlucoseTrend);
        }

        if (sensorGlucose.Count > 0)
        {
            var success = await PublishSensorGlucoseDataAsync(sensorGlucose, config, cancellationToken);
            result.ItemsSynced[SyncDataType.Glucose] = sensorGlucose.Count;
            result.LastEntryTimes[SyncDataType.Glucose] = DateTimeOffset
                .FromUnixTimeMilliseconds(sensorGlucose.Max(s => s.Mills))
                .UtcDateTime;

            if (!success)
            {
                result.Success = false;
                result.Errors.Add("SensorGlucose publish failed");
            }
            else
            {
                _logger.LogInformation("[{Source}] Synced {Count} glucose records from Twiist",
                    ConnectorSource, sensorGlucose.Count);
            }
        }
    }

    private async Task SyncBolusesAsync(
        TwiistStatus status, SyncResult result,
        TwiistConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var boluses = _insulinMapper.MapBoluses(status.InsulinHistory).ToList();
        if (boluses.Count == 0) return;

        var success = await PublishBolusDataAsync(boluses, config, cancellationToken);
        result.ItemsSynced[SyncDataType.Boluses] = boluses.Count;
        result.LastEntryTimes[SyncDataType.Boluses] = DateTimeOffset
            .FromUnixTimeMilliseconds(boluses.Max(b => b.Mills))
            .UtcDateTime;

        if (!success)
        {
            result.Success = false;
            result.Errors.Add("Bolus publish failed");
        }
        else
        {
            _logger.LogInformation("[{Source}] Synced {Count} bolus records from Twiist",
                ConnectorSource, boluses.Count);
        }
    }

    private async Task SyncCarbIntakeAsync(
        TwiistStatus status, SyncResult result,
        TwiistConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var carbs = _mealMapper.MapMeals(status.MealHistory).ToList();
        if (carbs.Count == 0) return;

        var success = await PublishCarbIntakeDataAsync(carbs, config, cancellationToken);
        result.ItemsSynced[SyncDataType.CarbIntake] = carbs.Count;
        result.LastEntryTimes[SyncDataType.CarbIntake] = DateTimeOffset
            .FromUnixTimeMilliseconds(carbs.Max(c => c.Mills))
            .UtcDateTime;

        if (!success)
        {
            result.Success = false;
            result.Errors.Add("CarbIntake publish failed");
        }
        else
        {
            _logger.LogInformation("[{Source}] Synced {Count} meal records from Twiist",
                ConnectorSource, carbs.Count);
        }
    }

    private async Task<TwiistPackage?> FetchPackageAsync(
        TwiistConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var accessToken = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.LogWarning("[{Source}] Failed to get valid token for package fetch", ConnectorSource);
            TrackFailedRequest("Failed to get valid token");
            return null;
        }

        await _rateLimitingStrategy.ApplyDelayAsync(0);

        var result = await ExecuteWithRetryAsync(
            async () => await FetchPackageCoreAsync(accessToken, config.PwdId, cancellationToken),
            _retryDelayStrategy,
            async () =>
            {
                _tokenProvider.InvalidateToken();
                var newToken = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
                if (string.IsNullOrEmpty(newToken)) return false;
                accessToken = newToken;
                return true;
            },
            operationName: "FetchTwiistPackage");

        return result;
    }

    private async Task<TwiistPackage?> FetchPackageCoreAsync(
        string accessToken, string pwdId, CancellationToken cancellationToken)
    {
        var url = $"{TwiistConstants.FollowerServiceBaseUrl}/pwd/{pwdId.ToLowerInvariant()}/package";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.Add("User-Agent", TwiistConstants.UserAgent);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errorContent}",
                null,
                response.StatusCode);
        }

        return await DeserializeResponseAsync<TwiistPackage>(response);
    }
}
