using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Mappers;
using Nocturne.Connectors.Twiist.Models;
using Nocturne.Connectors.Twiist.Utilities;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Twiist.Services;

/// <summary>
/// Connector service for Twiist Insight follower API.
/// Fetches glucose (from binary blobs), insulin doses, and meals.
/// </summary>
public class TwiistConnectorService
    : TokenConnectorService<TwiistConnectorConfiguration, TwiistAuthTokenProvider>
{
    private readonly TwiistGlucoseMapper _glucoseMapper;
    private readonly TwiistInsulinMapper _insulinMapper;
    private readonly TwiistMealMapper _mealMapper;
    private readonly TwiistTempBasalMapper _tempBasalMapper;

    public TwiistConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<TwiistConnectorConfiguration> serverResolver,
        ILogger<TwiistConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        TwiistAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null,
        TimeProvider? timeProvider = null)
        : base(httpClient, serverResolver, logger, retryDelayStrategy, tokenProvider, publisher, timeProvider)
    {
        _glucoseMapper = new TwiistGlucoseMapper(logger);
        _insulinMapper = new TwiistInsulinMapper(logger);
        _mealMapper = new TwiistMealMapper(logger);
        _tempBasalMapper = new TwiistTempBasalMapper(logger);
    }

    public override string ServiceName => "Twiist Insight";


    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        TwiistConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { Success = true };
        var activeTypes = ResolveActiveTypes(request, config);

        if (await ValidTokenAsync(config, cancellationToken) is null)
            return AuthenticationFailedResult();

        try
        {
            var (pwdId, resolveError) = await ResolvePatientIdAsync(config, cancellationToken);
            if (pwdId == null)
            {
                result.Success = false;
                result.Errors.Add(resolveError!);
                return result;
            }

            var package = await FetchPackageAsync(pwdId, config, cancellationToken);
            if (package?.Status == null)
            {
                _logger.LogWarning("[{Source}] Twiist returned empty package for PWD {PwdId}",
                    ConnectorSource, pwdId);
                result.Success = false;
                result.Errors.Add(
                    "Connected to Twiist, but no data was returned for the followed patient. " +
                    "If this persists, confirm the Twiist app is set up and sharing data.");
                return result;
            }

            // Glucose from binary blob
            if (activeTypes.Contains(SyncDataType.Glucose))
            {
                await SyncGlucoseAsync(package.Status, result, activeTypes, config, cancellationToken);
            }

            // Boluses from insulin history
            if (activeTypes.Contains(SyncDataType.Boluses))
            {
                await SyncBolusesAsync(package.Status, result, activeTypes, config, cancellationToken);
            }

            // Carbs from meal history
            if (activeTypes.Contains(SyncDataType.CarbIntake))
            {
                await SyncCarbIntakeAsync(package.Status, result, activeTypes, config, cancellationToken);
            }

            // Temp basals from the insulin-delivery blob (closed-loop enacted rates)
            if (activeTypes.Contains(SyncDataType.TempBasals))
            {
                await SyncTempBasalsAsync(package.Status, result, activeTypes, config, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Twiist sync was canceled");
            throw;
        }
        catch (Exception ex)
        {
            RecordSyncError(result, ex);
        }

        return result;
    }

    private async Task SyncGlucoseAsync(
        TwiistStatus status, SyncResult result, HashSet<SyncDataType> activeTypes,
        TwiistConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var glucoseRecords = TwiistBlobDecoder.ParseGlucoseRecords(status.GlucoseHistory?.Data);
        if (glucoseRecords.Count == 0)
        {
            _logger.LogDebug("[{Source}] No glucose records in package", ConnectorSource);
            return;
        }

        var sensorGlucose = _glucoseMapper.MapGlucoseRecords(
            glucoseRecords, Clock.GetUtcNow().UtcDateTime, status.Summary?.LastGlucoseTrend).ToList();

        // Apply trend to the most recent reading if available
        if (sensorGlucose.Count > 0 && status.Summary?.LastGlucoseTrend != null)
        {
            var latest = sensorGlucose[^1];
            latest.Direction = TwiistGlucoseMapper.ParseTrend(status.Summary.LastGlucoseTrend);
        }

        await PublishRecordTypeAsync(result, activeTypes,
            sensorGlucose, config, cancellationToken);
    }

    private async Task SyncBolusesAsync(
        TwiistStatus status, SyncResult result, HashSet<SyncDataType> activeTypes,
        TwiistConnectorConfiguration config, CancellationToken cancellationToken)
    {
        await PublishRecordTypeAsync(result, activeTypes,
            _insulinMapper.MapBoluses(status.InsulinHistory, Clock.GetUtcNow().UtcDateTime).ToList(), config, cancellationToken);
    }

    private async Task SyncCarbIntakeAsync(
        TwiistStatus status, SyncResult result, HashSet<SyncDataType> activeTypes,
        TwiistConnectorConfiguration config, CancellationToken cancellationToken)
    {
        await PublishRecordTypeAsync(result, activeTypes,
            _mealMapper.MapMeals(status.MealHistory, Clock.GetUtcNow().UtcDateTime).ToList(), config, cancellationToken);
    }

    private async Task SyncTempBasalsAsync(
        TwiistStatus status, SyncResult result, HashSet<SyncDataType> activeTypes,
        TwiistConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var scheduledRate = _tempBasalMapper.ParseScheduledRate(status.Details?.BasalRateUnitsPerHour);
        var now = Clock.GetUtcNow().UtcDateTime;
        var tempBasals = _tempBasalMapper.MapTempBasals(status.InsulinDelivery?.Data, scheduledRate, now)
            .Concat(_tempBasalMapper.MapSuspensions(status.InsulinHistory, now))
            .ToList();

        await PublishRecordTypeAsync(result, activeTypes,
            tempBasals, config, cancellationToken);
    }

    /// <summary>
    /// Resolves the PWD id to follow. Uses the configured id when present (advanced override),
    /// otherwise auto-discovers it from the follower overviews endpoint. Returns an instructive
    /// error message (for the connector health state) when no single patient can be determined.
    /// </summary>
    private async Task<(string? PwdId, string? Error)> ResolvePatientIdAsync(
        TwiistConnectorConfiguration config, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(config.PatientId))
            return (config.PatientId, null);

        var overviews = await FetchOverviewsAsync(config, cancellationToken);

        if (overviews == null)
            return (null, "Could not reach Twiist. Nocturne will try again at the next sync.");

        if (overviews.Count == 0)
            return (null,
                "Connected to Twiist, but this account is not following anyone. In the Twiist app, " +
                "share data with this account (or sign in with the account the data is shared with), then sync again.");

        var withId = overviews.Where(o => !string.IsNullOrWhiteSpace(o.PwdId)).ToList();

        if (withId.Count == 1)
        {
            _logger.LogInformation("[{Source}] Auto-discovered Twiist patient from overviews", ConnectorSource);
            return (withId[0].PwdId, null);
        }

        if (withId.Count == 0)
            return (null,
                "Connected to Twiist, but no shared patient id was returned. Confirm data sharing is active in the Twiist app, then sync again.");

        // More than one followed patient: we can't pick automatically.
        var names = string.Join(", ", withId.Select(o => o.PwdNickname ?? o.PwdId));
        return (null,
            $"This Twiist account follows multiple people ({names}). Following more than one person isn't supported yet.");
    }

    private async Task<List<TwiistOverview>?> FetchOverviewsAsync(
        TwiistConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var accessToken = await ValidTokenAsync(config, cancellationToken);
        if (accessToken is null)
        {
            _logger.LogWarning("[{Source}] Failed to get valid token for overviews fetch", ConnectorSource);
            return null;
        }

        return await ExecuteWithRetryAsync(
            async () => await FetchOverviewsCoreAsync(accessToken, cancellationToken),
            ReauthenticateWith(config, t => accessToken = t, cancellationToken),
            maxRetries: config.MaxRetryAttempts,
            operationName: "FetchTwiistOverviews",
            cancellationToken: cancellationToken);
    }

    private async Task<List<TwiistOverview>?> FetchOverviewsCoreAsync(
        string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{TwiistConstants.FollowerServiceBaseUrl}{TwiistConstants.OverviewsPath}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.Add("User-Agent", TwiistConstants.UserAgent);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        await response.EnsureSuccessWithBodyAsync(cancellationToken);

        return await DeserializeResponseAsync<List<TwiistOverview>>(response) ?? [];
    }

    private async Task<TwiistPackage?> FetchPackageAsync(
        string pwdId, TwiistConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var accessToken = await ValidTokenAsync(config, cancellationToken);
        if (accessToken is null)
        {
            _logger.LogWarning("[{Source}] Failed to get valid token for package fetch", ConnectorSource);
            return null;
        }

        var result = await ExecuteWithRetryAsync(
            async () => await FetchPackageCoreAsync(accessToken, pwdId, cancellationToken),
            ReauthenticateWith(config, t => accessToken = t, cancellationToken),
            maxRetries: config.MaxRetryAttempts,
            operationName: "FetchTwiistPackage",
            cancellationToken: cancellationToken);

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

        await response.EnsureSuccessWithBodyAsync(cancellationToken);

        return await DeserializeResponseAsync<TwiistPackage>(response);
    }
}
