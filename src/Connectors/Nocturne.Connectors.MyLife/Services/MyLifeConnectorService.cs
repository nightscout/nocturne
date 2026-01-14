using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeConnectorService(
    HttpClient httpClient,
    IOptions<MyLifeConnectorConfiguration> config,
    ILogger<MyLifeConnectorService> logger,
    IAuthTokenProvider tokenProvider,
    MyLifeEventsCache eventsCache,
    MyLifeEventProcessor eventMapper,
    MyLifeSessionStore sessionStore,
    IApiDataSubmitter? apiDataSubmitter = null,
    IConnectorMetricsTracker? metricsTracker = null,
    IConnectorStateService? stateService = null
)
    : BaseConnectorService<MyLifeConnectorConfiguration>(
        httpClient,
        logger,
        apiDataSubmitter,
        metricsTracker,
        stateService)
{
    private readonly MyLifeConnectorConfiguration _config = config.Value;

    public override string ServiceName => "MyLife";
    public override string ConnectorSource => DataSources.MyLifeConnector;

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
            _config.SyncMonths,
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
        var events = await eventsCache.GetEventsAsync(
            actualSince,
            _config.SyncMonths,
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

    protected override Task<IEnumerable<Activity>> FetchActivitiesAsync(
        DateTime? from,
        DateTime? to
    )
    {
        return Task.FromResult<IEnumerable<Activity>>(Array.Empty<Activity>());
    }

    private static IEnumerable<MyLifeEvent> FilterEventsBySince(
        IEnumerable<MyLifeEvent> events,
        DateTime since)
    {
        var sinceTicks = new DateTimeOffset(since).ToUnixTimeMilliseconds() * 10_000;
        return events.Where(e => e.EventDateTime >= sinceTicks);
    }
}
