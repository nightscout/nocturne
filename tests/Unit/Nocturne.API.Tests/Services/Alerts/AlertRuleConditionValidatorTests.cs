using FluentAssertions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class AlertRuleConditionValidatorTests
{
    private const string SmartSnoozeOn = """{"snooze":{"smartSnooze":true,"conditions":[{"type":"threshold","threshold":{"value":70}}]}}""";
    private const string SmartSnoozeOff = """{"snooze":{"smartSnooze":false,"conditions":[{"type":"threshold","threshold":{"value":70}}]}}""";

    private readonly ListLogger<AlertRuleConditionValidator> _logger = new();

    private AlertRuleConditionValidator Unavailable() => new(_logger, () => false);

    [Fact]
    public void Without_the_native_engine_rejects_a_body_the_managed_engine_would_skip()
    {
        var issues = Unavailable().Validate(
            AlertConditionType.Composite,
            """{"operator":"and","conditions":[{"type":"threshold","threshold":{"value":70}}]}""",
            false, null, null);

        issues.Should().Equal(new RustValidationIssue("condition", "composite[0].threshold", "direction_missing", "direction"));
    }

    [Fact]
    public void Without_the_native_engine_rejects_an_enabled_auto_resolve_tree_the_managed_engine_would_skip()
    {
        const string autoResolve = """{"type":"composite","composite":{"conditions":[{"type":"trend"}]}}""";

        Unavailable().Validate(AlertConditionType.Threshold, """{"direction":"below","value":70}""", true, autoResolve, null)
            .Should().Equal(new RustValidationIssue("auto_resolve", "auto_resolve", "operator_missing", "operator"));
        Unavailable().Validate(AlertConditionType.Threshold, """{"direction":"below","value":70}""", false, autoResolve, null)
            .Should().BeEmpty();
    }

    [Fact]
    public void Without_the_native_engine_rejects_smart_snooze_conditions_only_when_smart_snooze_is_on()
    {
        Unavailable().Validate(AlertConditionType.Threshold, """{"direction":"below","value":70}""", false, null, SmartSnoozeOn)
            .Should().Equal(new RustValidationIssue("snooze", "snooze[0].threshold", "direction_missing", "direction"));
        Unavailable().Validate(AlertConditionType.Threshold, """{"direction":"below","value":70}""", false, null, SmartSnoozeOff)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"direction":"below","value":"low"}""", "invalid_field")]
    [InlineData("null", "payload_missing")]
    [InlineData("{", "not_an_object")]
    public void Without_the_native_engine_rejects_a_body_that_does_not_read(string body, string reason)
    {
        Unavailable().Validate(AlertConditionType.Threshold, body, false, null, null)
            .Should().Equal(new RustValidationIssue("condition", "threshold", reason, null));
    }

    [Fact]
    public void Without_the_native_engine_accepts_an_evaluable_rule_and_warns_once()
    {
        var validator = Unavailable();

        validator.Validate(AlertConditionType.Threshold, """{"direction":"below","value":70}""", false, null, null)
            .Should().BeEmpty();
        validator.Validate(AlertConditionType.Threshold, """{"direction":"below","value":70}""", false, null, null)
            .Should().BeEmpty();

        _logger.Warnings.Should().ContainSingle().Which.Should().Contain("native library unavailable");
    }

    [Fact]
    public void Without_the_native_engine_still_rejects_an_unresolvable_timezone()
    {
        Unavailable().Validate(
                AlertConditionType.TimeOfDay, """{"from":"22:00","to":"06:00","timezone":"Etc/Unknown"}""", false, null, null)
            .Should().Equal(new RustValidationIssue("condition", "time_of_day", "invalid_field", "timezone"));
    }

    [NativeTheory]
    [InlineData("""{"operator":"and","conditions":[{"type":"threshold","threshold":{"value":70}}]}""", false, null, null)]
    [InlineData("""{"operator":"and","conditions":[{"type":"threshold","threshold":{"direction":"below","value":70}}]}""",
        true, """{"type":"composite","composite":{"conditions":[{"type":"trend"}]}}""", null)]
    [InlineData("""{"operator":"and","conditions":[{"type":"threshold","threshold":{"direction":"below","value":70}}]}""",
        false, null, SmartSnoozeOn)]
    public void The_fallback_reports_evaluation_faults_as_the_native_engine_does(
        string body, bool autoResolveEnabled, string? autoResolve, string? clientConfiguration)
    {
        var native = new AlertRuleConditionValidator(_logger)
            .Validate(AlertConditionType.Composite, body, autoResolveEnabled, autoResolve, clientConfiguration);
        var managed = Unavailable()
            .Validate(AlertConditionType.Composite, body, autoResolveEnabled, autoResolve, clientConfiguration);

        managed.Should().NotBeEmpty().And.BeSubsetOf(native);
    }
}
