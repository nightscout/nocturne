using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class AlertOrchestratorTests
{
    private readonly Mock<IConditionEvaluator> _mockEvaluator;
    private readonly ConditionEvaluatorRegistry _registry;
    private readonly Mock<IExcursionTracker> _excursionTracker;
    private readonly Mock<IAlertRepository> _repository;
    private readonly Mock<IEscalationAdvancer> _escalationAdvancer;
    private readonly Mock<ITenantAccessor> _tenantAccessor;
    private readonly Mock<IAlertDeliveryService> _deliveryService;
    private readonly Mock<ISignalRBroadcastService> _broadcastService;
    private readonly Mock<ISensorContextEnricher> _contextEnricher;
    private readonly Mock<IAlertAcknowledgementService> _acknowledgementService;
    private readonly Mock<IExcursionResolutionHandler> _resolutionHandler;
    private readonly FakeTimeProvider _timeProvider;
    private readonly ILogger<AlertOrchestrator> _logger;
    private readonly AlertOrchestrator _sut;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _ruleId = Guid.NewGuid();
    private readonly Guid _scheduleId = Guid.NewGuid();
    private readonly Guid _excursionId = Guid.NewGuid();

    public AlertOrchestratorTests()
    {
        _mockEvaluator = new Mock<IConditionEvaluator>();
        _mockEvaluator.Setup(e => e.ConditionType).Returns(AlertConditionType.Threshold);
        _registry = new ConditionEvaluatorRegistry(new[] { _mockEvaluator.Object });

        _excursionTracker = new Mock<IExcursionTracker>();
        _repository = new Mock<IAlertRepository>();
        _escalationAdvancer = new Mock<IEscalationAdvancer>();
        _tenantAccessor = new Mock<ITenantAccessor>();
        _deliveryService = new Mock<IAlertDeliveryService>();
        _broadcastService = new Mock<ISignalRBroadcastService>();
        _contextEnricher = new Mock<ISensorContextEnricher>();
        _acknowledgementService = new Mock<IAlertAcknowledgementService>();
        _resolutionHandler = new Mock<IExcursionResolutionHandler>();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        _logger = NullLoggerFactory.Instance.CreateLogger<AlertOrchestrator>();

        _tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);
        _contextEnricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, Guid _, CancellationToken _) => c);

        _sut = new AlertOrchestrator(
            _registry,
            _excursionTracker.Object,
            _repository.Object,
            _escalationAdvancer.Object,
            _tenantAccessor.Object,
            _deliveryService.Object,
            _broadcastService.Object,
            _contextEnricher.Object,
            _acknowledgementService.Object,
            _resolutionHandler.Object,
            _timeProvider,
            _logger);
    }

    private static SensorContext MakeContext(decimal? value = 120m, decimal? trendRate = null) =>
        new()
        {
            LatestValue = value,
            LatestTimestamp = DateTime.UtcNow,
            TrendRate = trendRate,
            LastReadingAt = DateTime.UtcNow,
        };

    private AlertRuleSnapshot MakeRule(AlertConditionType conditionType = AlertConditionType.Threshold) =>
        new(_ruleId, _tenantId, "Test Rule", conditionType,
            """{"direction":"below","value":70}""", AlertRuleSeverity.Critical, "{}", 0,
            AutoResolveEnabled: false, AutoResolveParams: null);

    private AlertScheduleSnapshot MakeSchedule() =>
        new(_scheduleId, _ruleId, "Default", true, null, null, null, "UTC");

    private AlertEscalationStepSnapshot MakeStep(int order, int delaySeconds = 300) =>
        new(Guid.NewGuid(), _scheduleId, order, delaySeconds);

    [Fact]
    public async Task EvaluateAsync_EmptyTenantId_ReturnsWithoutAction()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(Guid.Empty);

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        _repository.Verify(r => r.GetEnabledRulesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EvaluateAsync_NoRules_ReturnsWithoutEvaluation()
    {
        _repository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertRuleSnapshot>());

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        _excursionTracker.Verify(
            t => t.ProcessEvaluationAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateAsync_RuleConditionNotMet_NoExcursionTransition()
    {
        var rule = MakeRule();
        _repository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _mockEvaluator.Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _excursionTracker.Setup(t => t.ProcessEvaluationAsync(_ruleId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.None));

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        _repository.Verify(r => r.CreateInstanceAsync(It.IsAny<CreateAlertInstanceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _deliveryService.Verify(d => d.DispatchAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EvaluateAsync_ExcursionOpened_CreatesInstanceAndDispatches()
    {
        var rule = MakeRule();
        var schedule = MakeSchedule();
        var steps = new[] { MakeStep(0), MakeStep(1) };
        var instanceId = Guid.NewGuid();
        var instance = new AlertInstanceSnapshot(instanceId, _tenantId, _excursionId, _scheduleId,
            0, "escalating", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5), null, 0);
        var tenantContext = new TenantAlertContext(_tenantId, "John", "john", "John Doe", true, DateTime.UtcNow);

        _repository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _mockEvaluator.Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _excursionTracker.Setup(t => t.ProcessEvaluationAsync(_ruleId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.ExcursionOpened, _excursionId));
        _repository.Setup(r => r.GetSchedulesForRuleAsync(_ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { schedule });
        _repository.Setup(r => r.GetEscalationStepsAsync(_scheduleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(steps);
        _repository.Setup(r => r.CreateInstanceAsync(It.IsAny<CreateAlertInstanceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);
        _repository.Setup(r => r.CountActiveExcursionsAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _repository.Setup(r => r.GetTenantAlertContextAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenantContext);

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        _repository.Verify(r => r.CreateInstanceAsync(It.Is<CreateAlertInstanceRequest>(req =>
            req.TenantId == _tenantId &&
            req.ExcursionId == _excursionId &&
            req.ScheduleId == _scheduleId &&
            req.InitialStepOrder == 0 &&
            req.Status == "escalating"), It.IsAny<CancellationToken>()), Times.Once);

        _deliveryService.Verify(d => d.DispatchAsync(instanceId, 0, It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_ExcursionClosed_ResolvesInstances()
    {
        var rule = MakeRule();
        var instanceId = Guid.NewGuid();
        var instances = new[]
        {
            new AlertInstanceSnapshot(instanceId, _tenantId, _excursionId, _scheduleId,
                0, "escalating", DateTime.UtcNow, null, null, 0),
        };

        _repository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _mockEvaluator.Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _excursionTracker.Setup(t => t.ProcessEvaluationAsync(_ruleId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, _excursionId));
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instances);

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        // Orchestrator delegates the close cleanup to IExcursionResolutionHandler;
        // resolution_reason / instance resolution / delivery expiry / broadcast all live there.
        _resolutionHandler.Verify(h => h.HandleClosedAsync(
            It.Is<ExcursionTransition>(t => t.ExcursionId == _excursionId
                                            && t.Type == ExcursionTransitionType.ExcursionClosed),
            _tenantId,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_ExcursionContinues_AdvancesEscalation()
    {
        var rule = MakeRule();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var dueInstance = new AlertInstanceSnapshot(Guid.NewGuid(), _tenantId, _excursionId, _scheduleId,
            0, "escalating", now.AddMinutes(-10), now.AddMinutes(-1), null, 0);

        _repository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _mockEvaluator.Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _excursionTracker.Setup(t => t.ProcessEvaluationAsync(_ruleId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.ExcursionContinues, _excursionId));
        _repository.Setup(r => r.GetEscalatingInstancesDueAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { dueInstance });

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        _escalationAdvancer.Verify(a => a.AdvanceAsync(dueInstance, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_CallsEnricher_WithEvaluableRulesAndPassesEnrichedContextToEvaluator()
    {
        var rule = MakeRule();
        var enrichedTrendRate = 99m;
        _repository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _contextEnricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, Guid _, CancellationToken _) =>
                c with { TrendRate = enrichedTrendRate });
        _mockEvaluator.Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _excursionTracker.Setup(t => t.ProcessEvaluationAsync(_ruleId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.None));

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        _contextEnricher.Verify(
            e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), _tenantId, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockEvaluator.Verify(e => e.EvaluateAsync(
            It.IsAny<string>(),
            It.Is<SensorContext>(c => c.TrendRate == enrichedTrendRate),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_DropsRulesWithUnresolvedAlertStateReferences()
    {
        var orphanRefId = Guid.NewGuid();
        var chained = new AlertRuleSnapshot(_ruleId, _tenantId, "chained", AlertConditionType.AlertState,
            $$"""{"alert_id":"{{orphanRefId}}","state":"firing"}""",
            AlertRuleSeverity.Warning, "{}", 0, AutoResolveEnabled: false, AutoResolveParams: null);

        _repository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { chained });

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        _excursionTracker.Verify(
            t => t.ProcessEvaluationAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _contextEnricher.Verify(
            e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- Auto-resolve (Task 19) ----

    private AlertRuleSnapshot MakeAutoResolveRule(string autoResolveJson) =>
        new(_ruleId, _tenantId, "Auto-resolving Rule", AlertConditionType.Threshold,
            """{"direction":"below","value":70}""", AlertRuleSeverity.Warning, "{}", 0,
            AutoResolveEnabled: true, AutoResolveParams: autoResolveJson);

    [Fact]
    public async Task AutoResolve_WhenParamsEvaluateTrue_ForceClosesAndResolvesInstances()
    {
        // Main rule: condition still met (so excursion continues, not closed by hysteresis).
        // Auto-resolve params: a threshold node that the mock evaluator returns true for on its second call.
        var autoResolveJson = """{"type":"threshold","threshold":{"direction":"above","value":120}}""";
        var rule = MakeAutoResolveRule(autoResolveJson);

        _repository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });

        // Both main rule body and auto-resolve params return true through the same mock evaluator.
        _mockEvaluator.Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Main eval -> tracker reports excursion continues (active rule, no transition).
        _excursionTracker.Setup(t => t.ProcessEvaluationAsync(_ruleId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.ExcursionContinues, _excursionId));
        _repository.Setup(r => r.GetEscalatingInstancesDueAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        // Auto-resolve sees an active excursion and force-closes it.
        _excursionTracker.Setup(t => t.GetActiveExcursionIdAsync(_ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_excursionId);
        _excursionTracker.Setup(t => t.ForceCloseAsync(_ruleId, ExcursionCloseReason.AutoResolve, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(
                ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.AutoResolve));

        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        _excursionTracker.Verify(t => t.ForceCloseAsync(_ruleId, ExcursionCloseReason.AutoResolve, It.IsAny<CancellationToken>()), Times.Once);
        // Resolution-reason propagation is the handler's responsibility; assert it received the transition.
        _resolutionHandler.Verify(h => h.HandleClosedAsync(
            It.Is<ExcursionTransition>(t => t.CloseReason == ExcursionCloseReason.AutoResolve
                                            && t.ExcursionId == _excursionId),
            _tenantId,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AutoResolve_WhenAutoResolveDisabled_DoesNotEvaluateOrForceClose()
    {
        var rule = MakeRule(); // AutoResolveEnabled = false

        _repository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _mockEvaluator.Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _excursionTracker.Setup(t => t.ProcessEvaluationAsync(_ruleId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.ExcursionContinues, _excursionId));
        _repository.Setup(r => r.GetEscalatingInstancesDueAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        _excursionTracker.Verify(
            t => t.GetActiveExcursionIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _excursionTracker.Verify(
            t => t.ForceCloseAsync(It.IsAny<Guid>(), It.IsAny<ExcursionCloseReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AutoResolve_WhenNoActiveExcursion_DoesNotEvaluateAutoResolveParams()
    {
        var rule = MakeAutoResolveRule("""{"type":"threshold","threshold":{"direction":"above","value":120}}""");

        _repository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _mockEvaluator.Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _excursionTracker.Setup(t => t.ProcessEvaluationAsync(_ruleId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.None));
        _excursionTracker.Setup(t => t.GetActiveExcursionIdAsync(_ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        // Main rule eval ran once; auto-resolve short-circuits before evaluating its params.
        _mockEvaluator.Verify(
            e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _excursionTracker.Verify(
            t => t.ForceCloseAsync(It.IsAny<Guid>(), It.IsAny<ExcursionCloseReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- Info severity (Task 19b) ----

    private AlertRuleSnapshot MakeRuleWithSeverity(AlertRuleSeverity severity) =>
        new(_ruleId, _tenantId, "Severity Rule", AlertConditionType.Threshold,
            """{"direction":"below","value":70}""", severity, "{}", 0,
            AutoResolveEnabled: false, AutoResolveParams: null);

    private void SetupExcursionOpenedScenario(AlertRuleSnapshot rule)
    {
        var schedule = MakeSchedule();
        var steps = new[] { MakeStep(0), MakeStep(1) };
        var instance = new AlertInstanceSnapshot(Guid.NewGuid(), _tenantId, _excursionId, _scheduleId,
            0, "escalating", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5), null, 0);
        var tenantContext = new TenantAlertContext(_tenantId, "John", "john", "John Doe", true, DateTime.UtcNow);

        _repository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _mockEvaluator.Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _excursionTracker.Setup(t => t.ProcessEvaluationAsync(_ruleId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.ExcursionOpened, _excursionId));
        _repository.Setup(r => r.GetSchedulesForRuleAsync(_ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { schedule });
        _repository.Setup(r => r.GetEscalationStepsAsync(_scheduleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(steps);
        _repository.Setup(r => r.CreateInstanceAsync(It.IsAny<CreateAlertInstanceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);
        _repository.Setup(r => r.CountActiveExcursionsAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _repository.Setup(r => r.GetTenantAlertContextAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenantContext);
    }

    [Fact]
    public async Task ExcursionOpened_InfoSeverity_AutoAcknowledgesExcursion()
    {
        var rule = MakeRuleWithSeverity(AlertRuleSeverity.Info);
        SetupExcursionOpenedScenario(rule);

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        _acknowledgementService.Verify(
            a => a.AcknowledgeExcursionAsync(_excursionId, "system:auto-ack-on-trigger", It.IsAny<CancellationToken>()),
            Times.Once);

        // Step-0 delivery still fires per configured channels — Info routing is a frontend concern.
        _deliveryService.Verify(
            d => d.DispatchAsync(It.IsAny<Guid>(), 0, It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(AlertRuleSeverity.Critical)]
    [InlineData(AlertRuleSeverity.Warning)]
    public async Task ExcursionOpened_NonInfoSeverity_DoesNotAutoAcknowledge(AlertRuleSeverity severity)
    {
        var rule = MakeRuleWithSeverity(severity);
        SetupExcursionOpenedScenario(rule);

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        _acknowledgementService.Verify(
            a => a.AcknowledgeExcursionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AutoResolve_WhenMainEvalAlreadyClosed_SkipsAutoResolve()
    {
        var rule = MakeAutoResolveRule("""{"type":"threshold","threshold":{"direction":"above","value":120}}""");

        _repository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rule });
        _mockEvaluator.Setup(e => e.EvaluateAsync(It.IsAny<string>(), It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        // Hysteresis-driven close from main eval.
        _excursionTracker.Setup(t => t.ProcessEvaluationAsync(_ruleId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(
                ExcursionTransitionType.ExcursionClosed, _excursionId, ExcursionCloseReason.Hysteresis));
        _repository.Setup(r => r.GetInstancesForExcursionAsync(_excursionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertInstanceSnapshot>());

        await _sut.EvaluateAsync(MakeContext(), CancellationToken.None);

        // Hysteresis-flavoured transition flows to the handler; auto-resolve never queries.
        _resolutionHandler.Verify(h => h.HandleClosedAsync(
            It.Is<ExcursionTransition>(t => t.CloseReason == ExcursionCloseReason.Hysteresis),
            _tenantId,
            It.IsAny<CancellationToken>()), Times.Once);
        _excursionTracker.Verify(
            t => t.GetActiveExcursionIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
