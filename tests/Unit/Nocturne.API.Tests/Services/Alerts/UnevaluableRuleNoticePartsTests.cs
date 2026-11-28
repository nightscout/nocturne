using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A rule whose body can be evaluated but whose auto-resolve tree or smart-snooze conditions cannot
/// is noticed for the version of those trees (issue #4483). The sends and resolutions are in
/// <see cref="UnevaluableRuleNoticePostgresTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public class UnevaluableRuleNoticePartsTests
{
    private static readonly Guid Tenant = Guid.Parse("44834483-0000-7000-8000-0000000000aa");
    private static readonly Guid RuleId = Guid.Parse("44834483-0000-7000-8000-0000000000a1");

    private const string Body = """{"schema_version": 2, "operator": "<", "value": 54}""";
    private const string SchemaOneAutoResolve = """{"type": "threshold", "threshold": {"direction": "above", "value": 80}}""";
    private const string SchemaOneSnooze =
        """{"audio": {"enabled": true}, "snooze": {"smartSnooze": true, "conditions": [{"type": "trend", "trend": {"bucket": "rising"}}]}}""";

    private const string EmptySnooze =
        """{"audio": {"enabled": true}, "snooze": {"smartSnooze": true, "smartSnoozeExtendMinutes": 10, "maxCount": 3, "conditions": []}}""";

    private static readonly RustValidationIssue BodyIssue = new("condition", "threshold", "field_missing", "value");
    private static readonly RustValidationIssue AutoResolveIssue = new("auto_resolve", "auto_resolve", "unsupported_schema", "schema_version");
    private static readonly RustValidationIssue SnoozeIssue = new("snooze", "snooze[0]", "unsupported_schema", "schema_version");

    private static AlertRuleSnapshot Rule(
        string body = Body, bool autoResolveEnabled = false, string? autoResolve = null, string clientConfiguration = "{}") =>
        new(RuleId, Tenant, "Urgent Low", AlertConditionType.Threshold, body,
            AlertRuleSeverity.Warning, clientConfiguration, 0, autoResolveEnabled, autoResolve);

    private static AlertRuleSnapshot BothParts() =>
        Rule(autoResolveEnabled: true, autoResolve: SchemaOneAutoResolve, clientConfiguration: SchemaOneSnooze);

    [Fact]
    public void A_broken_auto_resolve_is_versioned_by_the_auto_resolve_tree_alone()
    {
        var rule = BothParts();
        var version = UnevaluableRuleNotice.PartsVersionOf(rule, [AutoResolveIssue]);

        UnevaluableRuleNotice.PartsVersionOf(rule with { ConditionParams = """{"schema_version": 2, "operator": "<", "value": 60}""" }, [AutoResolveIssue])
            .Should().Be(version, "an edit to the body leaves the auto-resolve as it was");
        UnevaluableRuleNotice.PartsVersionOf(rule with { ClientConfiguration = SchemaOneSnooze.Replace("rising", "falling") }, [AutoResolveIssue])
            .Should().Be(version, "the snooze conditions can be evaluated, so an edit to them is not one to a broken tree");
        UnevaluableRuleNotice.PartsVersionOf(rule with { ClientConfiguration = SchemaOneSnooze.Replace("\"enabled\": true", "\"enabled\": false") }, [AutoResolveIssue])
            .Should().Be(version, "the audio settings are not a tree the engine reads");
        UnevaluableRuleNotice.PartsVersionOf(rule with { AutoResolveParams = SchemaOneAutoResolve.Replace("80", "90") }, [AutoResolveIssue])
            .Should().NotBe(version);
        version.Should().HaveLength(64).And.NotBe(UnevaluableRuleNotice.VersionOf(rule));
    }

    [Fact]
    public void A_broken_snooze_is_versioned_by_its_conditions_alone()
    {
        var rule = BothParts();
        var version = UnevaluableRuleNotice.PartsVersionOf(rule, [SnoozeIssue]);

        UnevaluableRuleNotice.PartsVersionOf(rule with { AutoResolveEnabled = false }, [SnoozeIssue])
            .Should().Be(version, "turning a valid auto-resolve off is not an edit to a broken tree");
        UnevaluableRuleNotice.PartsVersionOf(rule with { AutoResolveParams = SchemaOneAutoResolve.Replace("80", "90") }, [SnoozeIssue])
            .Should().Be(version);
        UnevaluableRuleNotice.PartsVersionOf(
                rule with { ClientConfiguration = SchemaOneSnooze.Replace("\"smartSnooze\": true", "\"smartSnooze\": true, \"maxCount\": 5") },
                [SnoozeIssue])
            .Should().Be(version, "the snooze count is not a tree the engine reads");
        UnevaluableRuleNotice.PartsVersionOf(rule with { ClientConfiguration = SchemaOneSnooze.Replace("rising", "falling") }, [SnoozeIssue])
            .Should().NotBe(version);
    }

    [Theory]
    [InlineData("\"enabled\": true", "\"enabled\": false")]
    [InlineData("\"smartSnoozeExtendMinutes\": 10", "\"smartSnoozeExtendMinutes\": 15")]
    [InlineData("\"maxCount\": 3", "\"maxCount\": 4")]
    public void An_empty_snooze_list_is_not_the_whole_configuration(string from, string to)
    {
        var rule = Rule(autoResolveEnabled: true, autoResolve: SchemaOneAutoResolve, clientConfiguration: EmptySnooze);
        var edited = rule with { ClientConfiguration = EmptySnooze.Replace(from, to) };

        UnevaluableRuleNotice.PartsVersionOf(edited, [AutoResolveIssue])
            .Should().Be(UnevaluableRuleNotice.PartsVersionOf(rule, [AutoResolveIssue]));
        UnevaluableRuleNotice.PartsVersionOf(edited, [AutoResolveIssue, SnoozeIssue])
            .Should().Be(UnevaluableRuleNotice.PartsVersionOf(rule, [AutoResolveIssue, SnoozeIssue]),
                "an empty list is read, so only the list counts");
    }

    [Fact]
    public void Snooze_conditions_that_cannot_be_read_count_as_the_whole_configuration()
    {
        var unreadable = """{"audio": {"enabled": true}, "snooze": {"smartSnooze": true, "conditions": "rising"}}""";
        var rule = Rule(clientConfiguration: unreadable);

        UnevaluableRuleNotice.PartsVersionOf(rule with { ClientConfiguration = unreadable.Replace("true}", "false}") }, [SnoozeIssue])
            .Should().NotBe(UnevaluableRuleNotice.PartsVersionOf(rule, [SnoozeIssue]));
    }

    [Fact]
    public void A_notice_for_both_parts_still_holds_when_one_is_fixed_but_not_when_the_other_changes_or_breaks()
    {
        var rule = BothParts();
        var both = UnevaluableRuleNotice.PartsVersionOf(rule, [AutoResolveIssue, SnoozeIssue]);
        var autoResolveOnly = UnevaluableRuleNotice.PartsVersionOf(rule, [AutoResolveIssue]);
        var snoozeOnly = UnevaluableRuleNotice.PartsVersionOf(rule, [SnoozeIssue]);

        UnevaluableRuleNotice.PartsVersionCovers(both, autoResolveOnly).Should().BeTrue();
        UnevaluableRuleNotice.PartsVersionCovers(both, snoozeOnly).Should().BeTrue();
        UnevaluableRuleNotice.PartsVersionCovers(both, both).Should().BeTrue();
        UnevaluableRuleNotice.PartsVersionCovers(autoResolveOnly, both).Should().BeFalse("the snooze newly cannot be evaluated");
        UnevaluableRuleNotice.PartsVersionCovers(
                both,
                UnevaluableRuleNotice.PartsVersionOf(rule with { AutoResolveParams = SchemaOneAutoResolve.Replace("80", "90") }, [AutoResolveIssue]))
            .Should().BeFalse("the broken auto-resolve was edited");
        UnevaluableRuleNotice.PartsVersionCovers(UnevaluableRuleNotice.VersionOf(rule), autoResolveOnly)
            .Should().BeFalse("a body notice is never a parts notice");
    }

    [Fact]
    public void The_partly_notice_of_an_urgent_rule_that_pierces_do_not_disturb_is_info_and_does_not()
    {
        var urgent = Rule() with { Severity = AlertRuleSeverity.Critical, AllowThroughDnd = true, ScopeClass = RuleScopeClass.Low };

        var notice = UnevaluableRuleNotice.PartlyNoticeRule(urgent);

        (notice.Severity, notice.AllowThroughDnd, notice.ScopeClass)
            .Should().Be((AlertRuleSeverity.Info, false, RuleScopeClass.Low));
        DndSuppressionGate.SuppressingScope(notice.Severity, notice.AllowThroughDnd, notice.ScopeClass, new HashSet<DndScope> { DndScope.Lows })
            .Should().Be(DndScope.Lows);
        DndSuppressionGate.SuppressingScope(urgent, new HashSet<DndScope> { DndScope.Lows })
            .Should().BeNull("the rule's own alerts still pierce it");
    }

    [Fact]
    public void The_issue_log_validates_again_when_only_the_auto_resolve_or_the_client_configuration_changes()
    {
        var log = new ConditionVersionLog();
        var validations = 0;
        IReadOnlyList<RustValidationIssue> Validate()
        {
            validations++;
            return [];
        }

        var rule = Rule(autoResolveEnabled: true, autoResolve: SchemaOneAutoResolve);
        log.IssuesFor(rule, Validate, out var first);
        log.IssuesFor(rule, Validate, out var again);
        log.IssuesFor(rule with { AutoResolveParams = SchemaOneAutoResolve.Replace("80", "90") }, Validate, out var autoResolveEdited);
        log.IssuesFor(rule with { ClientConfiguration = SchemaOneSnooze }, Validate, out var snoozeEdited);

        (first, again, autoResolveEdited, snoozeEdited).Should().Be((true, false, true, true));
        validations.Should().Be(3);
    }

    [Theory]
    [InlineData("auto_resolve", "auto_resolve")]
    [InlineData("snooze", "snooze[0]")]
    public async Task A_rule_with_only_an_auto_resolve_or_snooze_issue_is_recorded_for_its_parts_version(string scope, string path)
    {
        var rule = Rule(autoResolveEnabled: true, autoResolve: SchemaOneAutoResolve, clientConfiguration: SchemaOneSnooze);
        var issue = new RustValidationIssue(scope, path, "unsupported_schema", "schema_version");
        var (notice, repository) = Notice([issue]);

        var partsVersion = UnevaluableRuleNotice.PartsVersionOf(rule, [issue]);

        await notice.CheckAsync(new EnabledAlertRules([rule], []), CancellationToken.None);

        repository.Verify(r => r.RecordUnevaluableNoticeAsync(
            rule, partsVersion, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.RecordUnevaluableNoticeAsync(
            It.IsAny<AlertRuleSnapshot>(), UnevaluableRuleNotice.VersionOf(rule), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_rule_whose_body_cannot_be_evaluated_either_is_recorded_for_its_body_version()
    {
        var rule = Rule(autoResolveEnabled: true, autoResolve: SchemaOneAutoResolve);
        var (notice, repository) = Notice([BodyIssue, AutoResolveIssue]);
        var partsVersion = UnevaluableRuleNotice.PartsVersionOf(rule, [AutoResolveIssue]);

        await notice.CheckAsync(new EnabledAlertRules([rule], []), CancellationToken.None);

        repository.Verify(r => r.RecordUnevaluableNoticeAsync(
            rule, UnevaluableRuleNotice.VersionOf(rule), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.RecordUnevaluableNoticeAsync(
            It.IsAny<AlertRuleSnapshot>(), partsVersion, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A notice over a store that records nothing, so nothing is dispatched.</summary>
    private static (UnevaluableRuleNotice Notice, Mock<IAlertRepository> Repository) Notice(IReadOnlyList<RustValidationIssue> issues)
    {
        var repository = new Mock<IAlertRepository>();
        repository
            .Setup(r => r.GetStandingUnevaluableNoticesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository
            .Setup(r => r.RecordUnevaluableNoticeAsync(It.IsAny<AlertRuleSnapshot>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NoticeRecorded?)null);
        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(issues);

        var notice = new UnevaluableRuleNotice(
            repository.Object,
            Mock.Of<IAlertRuleRepository>(),
            SettableTenantAccessor.Resolved(Tenant),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<ISensorContextEnricher>(),
            Mock.Of<IExcursionResolutionHandler>(),
            new ConditionVersionLog(),
            validator.Object,
            TimeProvider.System,
            NullLogger<UnevaluableRuleNotice>.Instance);
        return (notice, repository);
    }
}
