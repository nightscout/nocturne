using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// Verifies that <see cref="AlertReplayService"/> emits a per-leaf transition log alongside
/// its existing fired-event output. The transition log is compressed: the first observed
/// tick always emits a baseline point, then later points only when the leaf's truth changes.
/// </summary>
[Trait("Category", "Unit")]
public class AlertReplayServiceLeafLogTests
{
    private readonly Mock<IAlertRepository> _alertRepository = new();
    private readonly Mock<ISensorGlucoseRepository> _glucoseRepository = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly Mock<IIobCalculator> _iobCalculator = new();
    private readonly Mock<ICobCalculator> _cobCalculator = new();
    private readonly Mock<ITreatmentService> _treatmentService = new();
    private readonly Mock<IBolusRepository> _bolusRepository = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepository = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepository = new();
    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshotRepository = new();
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepository = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepository = new();
    private readonly Mock<IUploaderSnapshotRepository> _uploaderSnapshotRepository = new();
    private readonly Mock<IStateSpanService> _stateSpanService = new();
    private readonly AlertReplayService _sut;

    private readonly Guid _tenantId = Guid.NewGuid();

    public AlertReplayServiceLeafLogTests()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);

        var enricherDeps = new SensorContextEnricherDependencies(
            _iobCalculator.Object,
            _cobCalculator.Object,
            _treatmentService.Object,
            _bolusRepository.Object,
            _carbIntakeRepository.Object,
            _deviceEventRepository.Object,
            _pumpSnapshotRepository.Object,
            _apsSnapshotRepository.Object,
            _tempBasalRepository.Object,
            _uploaderSnapshotRepository.Object,
            _stateSpanService.Object,
            _alertRepository.Object,
            new Mock<Nocturne.Core.Contracts.V4.Repositories.ITargetRangeScheduleRepository>().Object,
            new Mock<Nocturne.Core.Contracts.Profiles.Resolvers.IActiveProfileResolver>().Object,
            new Mock<Nocturne.Core.Contracts.Profiles.Resolvers.ITherapySettingsResolver>().Object,
            Options.Create(new AlertEvaluationOptions()));
        var enricher = new SensorContextEnricher(
            enricherDeps,
            new ServiceCollection().BuildServiceProvider(),
            TimeProvider.System,
            NullLogger<SensorContextEnricher>.Instance);

        _sut = new AlertReplayService(
            _alertRepository.Object,
            _glucoseRepository.Object,
            enricher,
            _tenantAccessor.Object,
            NullLogger<AlertReplayService>.Instance);
    }

    private static AlertRuleSnapshot ThresholdRule(Guid id, string direction, decimal value) =>
        new(id, Guid.NewGuid(), $"{direction}-{value}", AlertConditionType.Threshold,
            $$"""{"direction":"{{direction}}","value":{{value}}}""", AlertRuleSeverity.Warning, "{}", 0,
            AutoResolveEnabled: false, AutoResolveParams: null);

    private static AlertRuleSnapshot CompositeAndOfTwoThresholds(
        Guid id, string direction1, decimal value1, string direction2, decimal value2)
    {
        string Leaf(string dir, decimal val) =>
            "{\"type\":\"threshold\",\"threshold\":{\"direction\":\"" + dir + "\",\"value\":" + val + "}}";
        var paramsJson = "{\"operator\":\"and\",\"conditions\":[" + Leaf(direction1, value1)
            + "," + Leaf(direction2, value2) + "]}";
        return new(id, Guid.NewGuid(), "and-of-two", AlertConditionType.Composite,
            paramsJson, AlertRuleSeverity.Warning, "{}", 0,
            AutoResolveEnabled: false, AutoResolveParams: null);
    }

    private static SensorGlucose Reading(DateTime at, double mgdl) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = at,
        Mgdl = mgdl,
    };

    [Fact]
    public async Task SingleThresholdLeaf_FalseThenTrueThenFalse_RecordsThreeTransitions()
    {
        var ruleId = Guid.NewGuid();
        var rule = ThresholdRule(ruleId, "below", 70m);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        // Tick cadence is 5min. Start high (false), drop low at minute 30 (true),
        // recover at minute 60 (false). Provide enough readings on the right cadence
        // so the per-tick snap finds a value.
        var readings = new List<SensorGlucose>();
        for (var minute = 0; minute < 24 * 60; minute += 5)
        {
            double mgdl = minute switch
            {
                >= 60 => 120,
                >= 30 => 60,
                _ => 120,
            };
            readings.Add(Reading(dayStart.AddMinutes(minute), mgdl));
        }

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _glucoseRepository.Setup(g => g.GetAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings);

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        result.LeafTransitionsByRule.Should().ContainKey(ruleId);
        var logs = result.LeafTransitionsByRule[ruleId];
        logs.Should().HaveCount(1);
        var leaf0 = logs[0];
        leaf0.LeafId.Should().Be(0);
        leaf0.Points.Should().HaveCount(3);
        // Baseline at t0: false (mgdl=120, not below 70).
        leaf0.Points[0].Value.Should().BeFalse();
        leaf0.Points[0].AtMs.Should().Be(new DateTimeOffset(dayStart).ToUnixTimeMilliseconds());
        // Rises to true at minute 30.
        leaf0.Points[1].Value.Should().BeTrue();
        leaf0.Points[1].AtMs.Should().Be(new DateTimeOffset(dayStart.AddMinutes(30)).ToUnixTimeMilliseconds());
        // Falls back to false at minute 60.
        leaf0.Points[2].Value.Should().BeFalse();
        leaf0.Points[2].AtMs.Should().Be(new DateTimeOffset(dayStart.AddMinutes(60)).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task LeafStableAcrossWindow_RecordsExactlyOneBaselineTransition()
    {
        var ruleId = Guid.NewGuid();
        var rule = ThresholdRule(ruleId, "below", 70m);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        var readings = new List<SensorGlucose>();
        for (var minute = 0; minute < 24 * 60; minute += 5)
        {
            // Always above threshold — leaf is always false.
            readings.Add(Reading(dayStart.AddMinutes(minute), 120));
        }

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _glucoseRepository.Setup(g => g.GetAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings);

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        var leaf0 = result.LeafTransitionsByRule[ruleId][0];
        leaf0.Points.Should().HaveCount(1);
        leaf0.Points[0].Value.Should().BeFalse();
    }

    [Fact]
    public async Task MultipleLeavesInComposite_TrackedIndependently()
    {
        var ruleId = Guid.NewGuid();
        // AND(below 70, above 100) — leaf 0 watches lows, leaf 1 watches highs.
        var rule = CompositeAndOfTwoThresholds(ruleId, "below", 70m, "above", 100m);
        var date = new DateOnly(2026, 4, 28);
        var dayStart = new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

        // Phase A (0-30): mgdl=120 → leaf0 false, leaf1 true.
        // Phase B (30-60): mgdl=60  → leaf0 true,  leaf1 false.
        // Phase C (60+):   mgdl=85  → leaf0 false, leaf1 false.
        var readings = new List<SensorGlucose>();
        for (var minute = 0; minute < 24 * 60; minute += 5)
        {
            double mgdl = minute switch
            {
                >= 60 => 85,
                >= 30 => 60,
                _ => 120,
            };
            readings.Add(Reading(dayStart.AddMinutes(minute), mgdl));
        }

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _glucoseRepository.Setup(g => g.GetAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings);

        var result = await _sut.ReplayAsync(date, "UTC", null, null, CancellationToken.None);

        var logs = result.LeafTransitionsByRule[ruleId];
        logs.Should().HaveCount(2);

        // Leaf 0: below-70. false → true at 30 → false at 60.
        var leaf0 = logs.Single(l => l.LeafId == 0);
        leaf0.Points.Select(p => p.Value).Should().Equal(false, true, false);

        // Leaf 1: above-100. true → false at 30. (Stays false for the rest.)
        var leaf1 = logs.Single(l => l.LeafId == 1);
        leaf1.Points.Select(p => p.Value).Should().Equal(true, false);
    }
}
