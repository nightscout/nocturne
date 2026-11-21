using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class LowCoverageTests
{
    private static readonly IReadOnlyList<RustValidationIssue> NoIssues = [];

    private static AlertRuleRecord Rule(
        RuleScopeClass scopeClass = RuleScopeClass.Low,
        bool enabled = true,
        int channels = 1,
        AlertRuleSeverity severity = AlertRuleSeverity.Warning)
    {
        var id = Guid.NewGuid();
        return new AlertRuleRecord(
            new AlertRuleSnapshot(
                id, Guid.NewGuid(), "Rule", AlertConditionType.Threshold, "{}", severity, "{}", 0,
                false, null, ScopeClass: scopeClass),
            null,
            enabled,
            null,
            null,
            [.. Enumerable.Range(0, channels).Select(i => new AlertRuleChannelSnapshot(
                Guid.NewGuid(), id, ChannelType.Webhook, $"https://example.test/{i}", null, i, null, null))]);
    }

    private static IReadOnlySet<Guid> Covering(params (AlertRuleRecord Rule, IReadOnlyList<RustValidationIssue> Issues)[] rules) =>
        LowCoverage.CoveringRuleIds(rules);

    [Fact]
    public void A_rule_whose_issues_could_not_be_determined_does_not_cover_lows()
    {
        LowCoverage.Covers(Rule(), null).Should().BeFalse();
    }

    [Fact]
    public void A_rule_with_all_four_conditions_covers_lows()
    {
        LowCoverage.Covers(Rule(), NoIssues).Should().BeTrue();
    }

    [Theory]
    [InlineData(RuleScopeClass.Low)]
    [InlineData(RuleScopeClass.Composite)]
    public void A_low_or_composite_rule_covers_lows(RuleScopeClass scopeClass)
    {
        LowCoverage.Covers(Rule(scopeClass), NoIssues).Should().BeTrue();
    }

    [Theory]
    [InlineData(RuleScopeClass.High)]
    [InlineData(RuleScopeClass.Undirected)]
    public void A_high_or_undirected_rule_does_not_cover_lows(RuleScopeClass scopeClass)
    {
        LowCoverage.Covers(Rule(scopeClass), NoIssues).Should().BeFalse();
    }

    [Fact]
    public void A_disabled_rule_does_not_cover_lows()
    {
        LowCoverage.Covers(Rule(enabled: false), NoIssues).Should().BeFalse();
    }

    [Fact]
    public void A_rule_with_no_channel_does_not_cover_lows()
    {
        LowCoverage.Covers(Rule(channels: 0), NoIssues).Should().BeFalse();
    }

    [Fact]
    public void A_rule_whose_body_cannot_be_evaluated_does_not_cover_lows()
    {
        var issue = new RustValidationIssue("condition", "threshold", "field_missing", "value");

        LowCoverage.Covers(Rule(), [issue]).Should().BeFalse();
    }

    [Theory]
    [InlineData("auto_resolve")]
    [InlineData("snooze")]
    public void A_problem_outside_the_body_leaves_the_rule_covering_lows(string scope)
    {
        var issue = new RustValidationIssue(scope, "threshold", "field_missing", "value");

        LowCoverage.Covers(Rule(), [issue]).Should().BeTrue();
    }

    [Theory]
    [InlineData(AlertRuleSeverity.Info)]
    [InlineData(AlertRuleSeverity.Warning)]
    [InlineData(AlertRuleSeverity.Critical)]
    public void Severity_does_not_decide_coverage(AlertRuleSeverity severity)
    {
        LowCoverage.Covers(Rule(severity: severity), NoIssues).Should().BeTrue();
    }

    [Fact]
    public void Only_the_rules_that_cover_are_listed()
    {
        var covering = Rule();
        var high = Rule(RuleScopeClass.High);

        Covering((covering, NoIssues), (high, NoIssues))
            .Should().BeEquivalentTo([covering.Rule.Id]);
    }
}
