using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeConnectorService(
    HttpClient httpClient,
    IOptions<MyLifeConnectorConfiguration> config,
    ILogger<MyLifeConnectorService> logger,
    MyLifeAuthTokenProvider tokenProvider,
    MyLifeEventsCache eventsCache,
    MyLifeEventProcessor eventMapper,
    MyLifeSessionStore sessionStore,
    IConnectorPublisher? publisher = null
)
    : BaseConnectorService<MyLifeConnectorConfiguration>(
        httpClient,
        logger,
        publisher)
{
    private readonly MyLifeConnectorConfiguration _config = config.Value;

    public override string ServiceName => "MyLife";
    protected override string ConnectorSource => DataSources.MyLifeConnector;

    public override List<SyncDataType> SupportedDataTypes =>
        [SyncDataType.Glucose, SyncDataType.Treatments];

    public override bool IsHealthy =>
        _failedRequestCount < MaxFailedRequestsBeforeUnhealthy && !tokenProvider.IsTokenExpired;

    public override async Task<bool> AuthenticateAsync()
    {
        var token = await tokenProvider.GetValidTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
        {
            sessionStore.Clear();
            TrackFailedRequest("Token missing");
            return false;
        }

        TrackSuccessfulRequest();
        return true;
    }

    public override async Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
    {
        var actualSince = await CalculateSinceTimestampAsync(_config, since);
        var events = await eventsCache.GetEventsAsync(
            actualSince,
            DateTime.UtcNow,
            CancellationToken.None
        );

        var filtered = FilterEventsBySince(events, actualSince);
        return eventMapper.MapEntries(filtered, _config.EnableGlucoseSync);
    }

    protected override async Task<IEnumerable<Treatment>> FetchTreatmentsAsync(
        DateTime? from,
        DateTime? to
    )
    {
        var actualSince = await CalculateTreatmentSinceTimestampAsync(_config, from);
        var actualUntil = to ?? DateTime.UtcNow;
        var events = await eventsCache.GetEventsAsync(
            actualSince,
            actualUntil,
            CancellationToken.None
        );

        var filtered = FilterEventsBySince(events, actualSince);
        return eventMapper.MapTreatments(
            filtered,
            _config.EnableManualBgSync,
            _config.EnableMealCarbConsolidation,
            _config.EnableTempBasalConsolidation,
            _config.TempBasalConsolidationWindowMinutes);
    }

    /// <summary>
    ///     Fetches BasalDelivery StateSpans from MyLife events.
    ///     These provide pump-confirmed basal delivery tracking with implicit duration model.
    /// </summary>
    protected async Task<IEnumerable<StateSpan>> FetchStateSpansAsync(
        DateTime? from,
        DateTime? to
    )
    {
        var actualSince = await CalculateTreatmentSinceTimestampAsync(_config, from);
        var actualUntil = to ?? DateTime.UtcNow;
        var events = await eventsCache.GetEventsAsync(
            actualSince,
            actualUntil,
            CancellationToken.None
        );

        var filtered = FilterEventsBySince(events, actualSince);
        return eventMapper.MapStateSpans(
            filtered,
            _config.EnableTempBasalConsolidation,
            _config.TempBasalConsolidationWindowMinutes);
    }

    /// <summary>
    ///     Override sync to also publish BasalDelivery StateSpans alongside Treatments.
    /// </summary>
    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        MyLifeConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        // Call the base implementation first
        var result = await base.PerformSyncInternalAsync(request, config, cancellationToken);

        // If treatments were synced, also sync BasalDelivery StateSpans
        if (request.DataTypes.Contains(SyncDataType.Treatments))
            try
            {
                var stateSpans = await FetchStateSpansAsync(request.From, request.To);
                var stateSpanList = stateSpans.ToList();

                if (stateSpanList.Count > 0)
                {
                    var stateSpanSuccess = await PublishStateSpanDataAsync(
                        stateSpanList,
                        config,
                        cancellationToken);

                    if (stateSpanSuccess)
                        _logger.LogInformation(
                            "Successfully synced {Count} BasalDelivery StateSpans",
                            stateSpanList.Count);
                    else
                        _logger.LogWarning(
                            "Failed to sync some BasalDelivery StateSpans");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing BasalDelivery StateSpans");
                // Don't fail the overall sync if StateSpan sync fails
            }

        return result;
    }

    private static IEnumerable<MyLifeEvent> FilterEventsBySince(
        IEnumerable<MyLifeEvent> events,
        DateTime since)
    {
        var sinceTicks = new DateTimeOffset(since).ToUnixTimeMilliseconds() * 10_000;
        return events.Where(e => e.EventDateTime >= sinceTicks);
    }
}