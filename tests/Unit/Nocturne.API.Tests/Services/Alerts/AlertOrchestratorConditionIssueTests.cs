using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The shapes the setup wizard stored before the alerts redesign (issue #853) evaluate without
/// error and never fire as set, so evaluation reports what a save would reject about a rule.
/// </summary>
[Trait("Category", "Unit")]
public class AlertOrchestratorConditionIssueTests
{
    private static readonly Guid Tenant = Guid.Parse("85385385-0000-7000-8000-0000000000aa");
    private static readonly Guid RuleId = Guid.Parse("85385385-0000-7000-8000-0000000000a1");
    private static readonly DateTime Now = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);

    private const string WizardUrgentLow = """{"threshold": 54, "direction": "below"}""";
    private const string WizardSensorLost = """{"minutes": 15}""";

    private static SensorContext Reading(decimal mgdl, DateTime? lastReadingAt = null) => new()
    {
        LatestValue = mgdl,
        LatestTimestamp = lastReadingAt ?? Now,
        TrendRate = 0,
        LastReadingAt = lastReadingAt ?? Now,
    };

    [Fact]
    public async Task A_wizard_urgent_low_never_fires_however_low_glucose_is()
    {
        (await new ThresholdEvaluator().EvaluateAsync(WizardUrgentLow, Reading(40), CancellationToken.None))
            .Should().BeFalse("with no value the threshold reads as 0, and no reading is below 0");
        (await new ThresholdEvaluator().EvaluateAsync("""{"direction": "below", "value": 54}""", Reading(40), CancellationToken.None))
            .Should().BeTrue("the shape the repair gives it fires");
    }

    [Fact]
    public async Task A_wizard_signal_loss_never_fires_however_long_the_gap()
    {
        var time = new ManualTimeProvider();
        time.SetUtcNow(Now);
        var evaluator = new SignalLossEvaluator(time);
        var gap = Reading(120, lastReadingAt: Now.AddHours(-3));

        (await evaluator.EvaluateAsync(WizardSensorLost, gap, CancellationToken.None))
            .Should().BeFalse("with no timeout_minutes the timeout reads as 0, which never fires");
        (await evaluator.EvaluateAsync("""{"timeout_minutes": 15}""", gap, CancellationToken.None))
            .Should().BeTrue("the shape the repair gives it fires");
    }

    [NativeFact]
    public void A_wizard_urgent_low_is_one_a_save_would_reject()
    {
        new AlertRuleConditionValidator(NullLogger<AlertRuleConditionValidator>.Instance)
            .Validate(AlertConditionType.Threshold, WizardUrgentLow, false, null, null)
            .Select(i => i.Reason)
            .Should().Contain("field_missing");
    }

    [Fact]
    public async Task A_rule_with_conditions_a_save_would_reject_is_reported_once_per_version()
    {
        var harness = new Harness();
        harness.Validator
            .Setup(v => v.Validate(AlertConditionType.Threshold, It.IsAny<string>(), false, null, "{}"))
            .Returns([new RustValidationIssue("condition", "threshold", "field_missing", "value")]);

        var rule = Rule(WizardUrgentLow);
        await harness.EvaluateAsync(rule);
        await harness.EvaluateAsync(rule);
        await harness.EvaluateAsync(rule);

        harness.Logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain(RuleId.ToString()).And.Contain("field_missing (value)");
        harness.Engine.Verify(
            e => e.EvaluateRuleAsync(It.IsAny<AlertRuleSnapshot>(), It.IsAny<SensorContext>(),
                It.IsAny<AlertEngineOptions>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3), "a reported rule still evaluates");

        await harness.EvaluateAsync(Rule("""{"threshold": 55, "direction": "below"}"""));
        harness.Logger.Warnings.Should().HaveCount(2, "an edited condition is a new version");
    }

    [Fact]
    public async Task A_rule_with_no_issues_is_not_reported()
    {
        var harness = new Harness();
        harness.Validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([]);

        await harness.EvaluateAsync(Rule("""{"direction": "below", "value": 54}"""));

        harness.Logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_tree_that_fails_evaluation_is_left_to_the_fault_report()
    {
        var harness = new Harness();
        harness.Validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([new RustValidationIssue("condition", "composite", "operator_missing", "operator")]);
        const string operatorless = """{"conditions": [{"type": "threshold", "threshold": {"direction": "below", "value": 70}}]}""";

        await harness.EvaluateAsync(Rule(operatorless, AlertConditionType.Composite));

        harness.Validator.Verify(
            v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task A_failing_issue_check_stops_no_rule_evaluating()
    {
        var harness = new Harness();
        harness.Validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([]);
        harness.Validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), WizardUrgentLow, It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Throws(new InvalidOperationException("validator failed"));
        var first = Rule(WizardUrgentLow);
        var second = Rule("""{"direction": "below", "value": 54}""") with
        {
            Id = Guid.Parse("85385385-0000-7000-8000-0000000000a2"),
        };

        await harness.EvaluateAsync(first, second);

        harness.Engine.Verify(
            e => e.EvaluateRuleAsync(It.Is<AlertRuleSnapshot>(r => r.Id == first.Id), It.IsAny<SensorContext>(),
                It.IsAny<AlertEngineOptions>(), It.IsAny<CancellationToken>()),
            Times.Once, "the rule whose check failed still evaluates");
        harness.Engine.Verify(
            e => e.EvaluateRuleAsync(It.Is<AlertRuleSnapshot>(r => r.Id == second.Id), It.IsAny<SensorContext>(),
                It.IsAny<AlertEngineOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Error);
    }

    private static AlertRuleSnapshot Rule(string conditionParams, AlertConditionType type = AlertConditionType.Threshold) =>
        new(RuleId, Tenant, "Urgent Low", type, conditionParams,
            AlertRuleSeverity.Critical, "{}", 0, false, null);

    private sealed class Harness
    {
        public Mock<IAlertEvaluationEngine> Engine { get; } = new();
        public Mock<IAlertRuleConditionValidator> Validator { get; } = new();
        public ListLogger<AlertOrchestrator> Logger { get; } = new();

        private readonly AlertOrchestrator _orchestrator;

        public Harness()
        {
            Engine
                .Setup(e => e.EvaluateRuleAsync(It.IsAny<AlertRuleSnapshot>(), It.IsAny<SensorContext>(),
                    It.IsAny<AlertEngineOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AlertEngineEvaluation());

            var tenant = new Mock<ITenantAccessor>();
            tenant.SetupGet(t => t.TenantId).Returns(Tenant);

            var enricher = new Mock<ISensorContextEnricher>();
            enricher
                .Setup(x => x.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(),
                    It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, Guid _, CancellationToken _) => c);

            _orchestrator = new AlertOrchestrator(
                Engine.Object,
                Mock.Of<IAlertRepository>(),
                tenant.Object,
                Mock.Of<IAlertDeliveryService>(),
                enricher.Object,
                Mock.Of<IAlertAcknowledgementService>(),
                Mock.Of<IExcursionResolutionHandler>(),
                new ConditionVersionLog(),
                Validator.Object,
                TimeProvider.System,
                Logger);
        }

        public Task EvaluateAsync(params AlertRuleSnapshot[] rules) =>
            _orchestrator.EvaluateRulesAsync(rules, rules.Select(r => r.Id).ToHashSet(), Reading(40), CancellationToken.None);
    }
}
