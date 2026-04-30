using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class AlertReplayServiceTests
{
    private readonly Mock<IAlertRepository> _alertRepository = new();
    private readonly Mock<ISensorGlucoseRepository> _glucoseRepository = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly AlertReplayService _sut;

    private readonly Guid _tenantId = Guid.NewGuid();

    public AlertReplayServiceTests()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);
        _sut = new AlertReplayService(
            _alertRepository.Object,
            _glucoseRepository.Object,
            _tenantAccessor.Object,
            NullLogger<AlertReplayService>.Instance);
    }

    private static AlertRuleSnapshot ThresholdRule(Guid id, string direction, decimal value,
        AlertRuleSeverity severity = AlertRuleSeverity.Warning) =>
        new(id, Guid.NewGuid(), $"{direction}-{value}", AlertConditionType.Threshold,
            $$"""{"direction":"{{direction}}","value":{{value}}}""", severity, "{}", 0,
            AutoResolveEnabled: false, AutoResolveParams: null);

    private static SensorGlucose Reading(DateTime at, double mgdl) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = at,
        Mgdl = mgdl,
    };

    [Fact]
    public async Task EmptyTenantId_ReturnsEmpty()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(Guid.Empty);

        var result = await _sut.ReplayAsync(null, null, CancellationToken.None);

        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task NoRules_ReturnsEmptyEventsButValidWindow()
    {
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertRuleSnapshot>());

        var result = await _sut.ReplayAsync(
            new DateOnly(2026, 4, 28), "UTC", CancellationToken.None);

        result.Events.Should().BeEmpty();
        result.WindowStart.Should().Be(new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc));
        result.WindowEnd.Should().Be(new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc));
        result.Limitations.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ThresholdBelow_LowReading_FiresOneEventAtLeadingEdge()
    {
        var ruleId = Guid.NewGuid();
        var rule = ThresholdRule(ruleId, "below", 70m);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });

        // Single low reading at 04:00, then a clear at 04:30, then another low at 05:00.
        var readings = new[]
        {
            Reading(dayStart.AddHours(4), 65),
            Reading(dayStart.AddHours(4).AddMinutes(30), 95),
            Reading(dayStart.AddHours(5), 60),
        };
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings);

        var result = await _sut.ReplayAsync(date, "UTC", CancellationToken.None);

        result.Events.Should().HaveCount(2);
        result.Events[0].RuleId.Should().Be(ruleId);
        result.Events[0].At.Should().Be(dayStart.AddHours(4));
        result.Events[1].At.Should().Be(dayStart.AddHours(5));
    }

    [Fact]
    public async Task ContinuouslyMet_ProducesSingleLeadingEdgeEvent()
    {
        var rule = ThresholdRule(Guid.NewGuid(), "below", 70m);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });

        // Continuous low for an hour — should produce ONE event at the first low tick.
        var readings = Enumerable.Range(0, 12)
            .Select(i => Reading(dayStart.AddHours(2).AddMinutes(i * 5), 60))
            .ToArray();
        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings);

        var result = await _sut.ReplayAsync(date, "UTC", CancellationToken.None);

        result.Events.Should().HaveCount(1);
        result.Events[0].At.Should().Be(dayStart.AddHours(2));
    }

    [Fact]
    public async Task MultipleRules_OrderedByTopologicalDependency()
    {
        // Rule A: threshold < 70
        // Rule B: alert_state(A, "firing") — should fire at the SAME tick as A, but only after.
        var ruleAId = Guid.NewGuid();
        var ruleBId = Guid.NewGuid();
        var ruleA = ThresholdRule(ruleAId, "below", 70m);
        var ruleB = new AlertRuleSnapshot(ruleBId, _tenantId, "B-chains-A",
            AlertConditionType.AlertState,
            $$"""{"alert_id":"{{ruleAId}}","state":"firing"}""",
            AlertRuleSeverity.Critical, "{}", 0, false, null);

        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        // Insertion order is B then A (deliberately reversed) — topo-sort must put A first
        // so B sees A's "firing" snapshot in the same tick.
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ruleB, ruleA });

        _glucoseRepository.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Reading(dayStart.AddHours(3), 60) });

        var result = await _sut.ReplayAsync(date, "UTC", CancellationToken.None);

        // Both should fire. A first (its event was added first within the tick), then B.
        result.Events.Should().HaveCount(2);
        result.Events[0].RuleId.Should().Be(ruleAId);
        result.Events[1].RuleId.Should().Be(ruleBId);
    }

    [Fact]
    public async Task RollingWindow_When_Date_IsNull()
    {
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertRuleSnapshot>());

        var result = await _sut.ReplayAsync(null, null, CancellationToken.None);

        (result.WindowEnd - result.WindowStart).Should().BeCloseTo(TimeSpan.FromHours(24), TimeSpan.FromSeconds(5));
    }
}
