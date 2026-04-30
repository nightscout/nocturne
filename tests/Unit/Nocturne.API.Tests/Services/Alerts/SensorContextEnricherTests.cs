using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class SensorContextEnricherTests
{
    private readonly Mock<IIobService> _iobService = new();
    private readonly Mock<ICobService> _cobService = new();
    private readonly Mock<ITreatmentService> _treatmentService = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepository = new();
    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshotRepository = new();
    private readonly Mock<IAlertRepository> _alertRepository = new();
    private readonly Mock<IPredictionService> _predictionService = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _tenantId = Guid.NewGuid();

    [Fact]
    public async Task BgAndTrend_only_rule_triggers_no_external_fetches()
    {
        var enricher = BuildEnricher();
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"above","value":180}""");

        await enricher.EnrichAsync(BaseContext(trendRate: 1.5m), new[] { rule }, _tenantId, CancellationToken.None);

        _treatmentService.Verify(s => s.GetTreatmentsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _iobService.Verify(s => s.CalculateTotalAsync(It.IsAny<List<Treatment>>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<CancellationToken>()), Times.Never);
        _cobService.Verify(s => s.CobTotalAsync(It.IsAny<List<Treatment>>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _predictionService.Verify(s => s.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _pumpSnapshotRepository.Verify(s => s.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _deviceEventRepository.Verify(s => s.GetLatestByEventTypeAsync(It.IsAny<DeviceEventType>(), It.IsAny<CancellationToken>()), Times.Never);
        _alertRepository.Verify(s => s.GetActiveAlertSnapshotsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TrendLeaf_derives_bucket_from_trend_rate_without_external_fetches()
    {
        var enricher = BuildEnricher();
        var rule = MakeRule(AlertConditionType.Trend, """{"bucket":"rising_fast"}""");

        var enriched = await enricher.EnrichAsync(BaseContext(trendRate: 4.5m), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.TrendBucket.Should().Be(TrendBucket.RisingFast);
        _treatmentService.Verify(s => s.GetTreatmentsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _predictionService.Verify(s => s.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IobAndCob_share_one_treatment_fetch()
    {
        var enricher = BuildEnricher();
        _treatmentService.Setup(s => s.GetTreatmentsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Treatment>());
        _iobService.Setup(s => s.CalculateTotalAsync(It.IsAny<List<Treatment>>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobResult { Iob = 1.5 });
        _cobService.Setup(s => s.CobTotalAsync(It.IsAny<List<Treatment>>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CobResult { Cob = 24.0 });

        var json = """
        {
          "operator": "and",
          "conditions": [
            { "type": "iob", "iob": { "operator": ">", "value": 1 } },
            { "type": "cob", "cob": { "operator": ">", "value": 10 } }
          ]
        }
        """;
        var rule = MakeRule(AlertConditionType.Composite, json);

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.IobUnits.Should().Be(1.5m);
        enriched.CobGrams.Should().Be(24.0m);
        _treatmentService.Verify(s => s.GetTreatmentsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Predictions_returns_empty_when_service_unregistered()
    {
        var enricher = BuildEnricher(includePredictionService: false);
        var rule = MakeRule(AlertConditionType.Predicted, """{"operator":"<","value":70,"within_minutes":30}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.Predictions.Should().BeEmpty();
    }

    [Fact]
    public async Task Predictions_swallows_invalid_operation_and_returns_empty()
    {
        var enricher = BuildEnricher();
        _predictionService.Setup(p => p.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no readings available"));
        var rule = MakeRule(AlertConditionType.Predicted, """{"operator":"<","value":70,"within_minutes":30}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.Predictions.Should().BeEmpty();
    }

    [Fact]
    public async Task Predictions_maps_curve_to_offset_minutes_using_response_interval()
    {
        var enricher = BuildEnricher();
        _predictionService.Setup(p => p.GetPredictionsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GlucosePredictionResponse
            {
                IntervalMinutes = 5,
                Predictions = new PredictionCurves { Default = new List<double> { 110, 120, 130 } }
            });
        var rule = MakeRule(AlertConditionType.Predicted, """{"operator":"<","value":70,"within_minutes":30}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.Predictions.Should().HaveCount(3);
        enriched.Predictions[0].OffsetMinutes.Should().Be(5);
        enriched.Predictions[0].Mgdl.Should().Be(110m);
        enriched.Predictions[2].OffsetMinutes.Should().Be(15);
    }

    [Fact]
    public async Task Reservoir_pulls_latest_pump_snapshot()
    {
        var enricher = BuildEnricher();
        _pumpSnapshotRepository.Setup(r => r.GetAsync(null, null, null, null, 1, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PumpSnapshot { Reservoir = 42.5 } });
        var rule = MakeRule(AlertConditionType.Reservoir, """{"operator":"<","value":50}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.ReservoirUnits.Should().Be(42.5m);
    }

    [Fact]
    public async Task SiteAge_pulls_latest_site_change_event()
    {
        var enricher = BuildEnricher();
        var siteChangeAt = new DateTime(2026, 3, 20, 8, 0, 0, DateTimeKind.Utc);
        _deviceEventRepository.Setup(r => r.GetLatestByEventTypeAsync(DeviceEventType.SiteChange, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceEvent { Timestamp = siteChangeAt, EventType = DeviceEventType.SiteChange });
        var rule = MakeRule(AlertConditionType.SiteAge, """{"operator":">","value":72}""");

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.LastSiteChangeAt.Should().Be(siteChangeAt);
        _deviceEventRepository.Verify(r => r.GetLatestByEventTypeAsync(DeviceEventType.SensorStart, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActiveAlerts_pulled_only_when_alert_state_referenced()
    {
        var enricher = BuildEnricher();
        var snapshot = new ActiveAlertSnapshot("firing", new DateTime(2026, 3, 22, 11, 50, 0, DateTimeKind.Utc), null);
        var dict = new Dictionary<Guid, ActiveAlertSnapshot> { [Guid.NewGuid()] = snapshot };
        _alertRepository.Setup(r => r.GetActiveAlertSnapshotsAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dict);

        var json = $$"""{"alert_id":"{{Guid.NewGuid()}}","state":"firing"}""";
        var rule = MakeRule(AlertConditionType.AlertState, json);

        var enriched = await enricher.EnrichAsync(BaseContext(), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.ActiveAlerts.Should().BeSameAs(dict);
        _alertRepository.Verify(r => r.GetActiveAlertSnapshotsAsync(_tenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(3.5, TrendBucket.RisingFast)]
    [InlineData(3.0, TrendBucket.RisingFast)]
    [InlineData(1.0, TrendBucket.Rising)]
    [InlineData(0.0, TrendBucket.Flat)]
    [InlineData(-1.0, TrendBucket.Flat)]
    [InlineData(-1.5, TrendBucket.Falling)]
    [InlineData(-3.0, TrendBucket.Falling)]
    [InlineData(-3.5, TrendBucket.FallingFast)]
    public async Task TrendBucket_derivation_boundaries(double? rateInput, TrendBucket? expected)
    {
        var enricher = BuildEnricher();
        var rule = MakeRule(AlertConditionType.Trend, """{"bucket":"flat"}""");
        decimal? rate = rateInput is null ? null : (decimal)rateInput.Value;

        var enriched = await enricher.EnrichAsync(BaseContext(trendRate: rate), new[] { rule }, _tenantId, CancellationToken.None);

        enriched.TrendBucket.Should().Be(expected);
    }

    private SensorContextEnricher BuildEnricher(bool includePredictionService = true)
    {
        var services = new ServiceCollection();
        if (includePredictionService)
        {
            services.AddSingleton(_predictionService.Object);
        }
        var provider = services.BuildServiceProvider();

        return new SensorContextEnricher(
            provider,
            _iobService.Object,
            _cobService.Object,
            _treatmentService.Object,
            _deviceEventRepository.Object,
            _pumpSnapshotRepository.Object,
            _alertRepository.Object,
            _timeProvider,
            new NullLogger<SensorContextEnricher>());
    }

    private SensorContext BaseContext(decimal? trendRate = 0m) => new()
    {
        LatestValue = 110m,
        LatestTimestamp = _timeProvider.GetUtcNow().UtcDateTime,
        TrendRate = trendRate,
        LastReadingAt = _timeProvider.GetUtcNow().UtcDateTime,
    };

    private static AlertRuleSnapshot MakeRule(AlertConditionType type, string json) =>
        new(Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Name: "test",
            ConditionType: type,
            ConditionParams: json,
            Severity: AlertRuleSeverity.Warning,
            ClientConfiguration: "{}",
            SortOrder: 0,
            AutoResolveEnabled: false,
            AutoResolveParams: null);
}
