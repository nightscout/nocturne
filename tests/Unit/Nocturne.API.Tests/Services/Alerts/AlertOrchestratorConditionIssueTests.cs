using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.API.Tests.Services.BackgroundServices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The shapes the setup wizard stored before the alerts redesign (issue #853) cannot be evaluated,
/// so evaluation reports every problem with a rule's conditions.
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

    /// <summary>A stored rule body evaluated as its root node, as the engine reads it.</summary>
    private static bool Evaluate(AlertConditionType type, string conditionParams, SensorContext context)
    {
        var node = RustEnvelopeMapper.ParseJson(
            RustEnvelopeMapper.WrapPayload(AlertConditionTypeNames.ToWireString(type), conditionParams));
        var errors = new AlertEngineErrors(new TestMeterFactory(), TimeProvider.System);
        return RustAuxiliaryEvaluation.EvaluateNode(
            errors, RuleId, node, "condition", context, Now, new Dictionary<string, DateTime>()).Value!.Value;
    }

    [NativeFact]
    public void A_wizard_urgent_low_cannot_be_evaluated()
    {
        var act = () => Evaluate(AlertConditionType.Threshold, WizardUrgentLow, Reading(40));
        act.Should().Throw<RustAlertEngineException>().WithMessage("*field_missing*(field 'value')*");
        Evaluate(AlertConditionType.Threshold, """{"direction": "below", "value": 54}""", Reading(40))
            .Should().BeTrue("the shape the repair gives it fires");
    }

    [NativeFact]
    public void A_wizard_signal_loss_cannot_be_evaluated()
    {
        var gap = Reading(120, lastReadingAt: Now.AddHours(-3));

        var act = () => Evaluate(AlertConditionType.SignalLoss, WizardSensorLost, gap);
        act.Should().Throw<RustAlertEngineException>().WithMessage("*field_missing*(field 'timeout_minutes')*");
        Evaluate(AlertConditionType.SignalLoss, """{"timeout_minutes": 15}""", gap)
            .Should().BeTrue("the shape the repair gives it fires");
    }

    [NativeFact]
    public void A_wizard_urgent_low_is_one_a_save_would_reject()
    {
        new AlertRuleConditionValidator()
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

    [NativeFact]
    public async Task A_rule_the_reference_filter_drops_is_still_reported()
    {
        var harness = new Harness();
        harness.Validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([new RustValidationIssue("condition", "alert_state", "invalid_field", "alert_id")]);

        await harness.EvaluateAsync(Rule(
            """{"alert_id": "00000000-0000-0000-0000-0000000000aa", "state": "firing"}""",
            AlertConditionType.AlertState));

        harness.Logger.Warnings.Should().ContainSingle().Which.Should().Contain("invalid_field (alert_id)");
        harness.Engine.Verify(
            e => e.EvaluateRuleAsync(It.IsAny<AlertRuleSnapshot>(), It.IsAny<SensorContext>(),
                It.IsAny<AlertEngineOptions>(), It.IsAny<CancellationToken>()),
            Times.Never, "a rule naming no enabled rule is not evaluated");
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
    public async Task A_tree_that_fails_evaluation_is_checked_once_like_any_other()
    {
        var harness = new Harness();
        harness.Validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([new RustValidationIssue("condition", "composite", "field_missing", "operator")]);
        const string operatorless = """{"conditions": [{"type": "threshold", "threshold": {"direction": "below", "value": 70}}]}""";

        await harness.EvaluateAsync(Rule(operatorless, AlertConditionType.Composite));

        harness.Validator.Verify(
            v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);
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
                .Setup(x => x.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) => c);

            _orchestrator = new AlertOrchestrator(
                Engine.Object,
                Mock.Of<IAlertRepository>(),
                Mock.Of<IAlertRuleRepository>(),
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
