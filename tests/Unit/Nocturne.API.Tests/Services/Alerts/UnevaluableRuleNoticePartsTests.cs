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

    private static AlertRuleSnapshot Rule(
        string body = Body, bool autoResolveEnabled = false, string? autoResolve = null, string clientConfiguration = "{}") =>
        new(RuleId, Tenant, "Urgent Low", AlertConditionType.Threshold, body,
            AlertRuleSeverity.Warning, clientConfiguration, 0, autoResolveEnabled, autoResolve);

    [Fact]
    public void The_parts_version_follows_the_auto_resolve_tree_and_the_snooze_conditions_only()
    {
        var rule = Rule(autoResolveEnabled: true, autoResolve: SchemaOneAutoResolve, clientConfiguration: SchemaOneSnooze);
        var version = UnevaluableRuleNotice.PartsVersionOf(rule);

        UnevaluableRuleNotice.PartsVersionOf(rule with { ConditionParams = """{"schema_version": 2, "operator": "<", "value": 60}""" })
            .Should().Be(version, "an edit to the body leaves the auto-resolve and snooze as they were");
        UnevaluableRuleNotice.PartsVersionOf(rule with { ClientConfiguration = SchemaOneSnooze.Replace("\"enabled\": true", "\"enabled\": false") })
            .Should().Be(version, "the audio settings are not a tree the engine reads");
        UnevaluableRuleNotice.PartsVersionOf(rule with { AutoResolveParams = SchemaOneAutoResolve.Replace("80", "90") })
            .Should().NotBe(version);
        UnevaluableRuleNotice.PartsVersionOf(rule with { ClientConfiguration = SchemaOneSnooze.Replace("rising", "falling") })
            .Should().NotBe(version);
        UnevaluableRuleNotice.PartsVersionOf(rule with { AutoResolveEnabled = false })
            .Should().NotBe(version, "a disabled auto-resolve is not handed to the engine");
        version.Should().NotBe(UnevaluableRuleNotice.VersionOf(rule));
    }

    [Fact]
    public void Snooze_conditions_that_are_off_are_not_part_of_the_version()
    {
        var off = SchemaOneSnooze.Replace("\"smartSnooze\": true", "\"smartSnooze\": false");

        UnevaluableRuleNotice.PartsVersionOf(Rule(clientConfiguration: off))
            .Should().Be(UnevaluableRuleNotice.PartsVersionOf(Rule(clientConfiguration: off.Replace("rising", "falling"))));
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
        var (notice, repository) = Notice([new RustValidationIssue(scope, path, "unsupported_schema", "schema_version")]);

        await notice.CheckAsync(new EnabledAlertRules([rule], []), CancellationToken.None);

        repository.Verify(r => r.RecordUnevaluableNoticeAsync(
            rule, UnevaluableRuleNotice.PartsVersionOf(rule), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.RecordUnevaluableNoticeAsync(
            It.IsAny<AlertRuleSnapshot>(), UnevaluableRuleNotice.VersionOf(rule), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_rule_whose_body_cannot_be_evaluated_either_is_recorded_for_its_body_version()
    {
        var rule = Rule(autoResolveEnabled: true, autoResolve: SchemaOneAutoResolve);
        var (notice, repository) = Notice([
            new RustValidationIssue("condition", "threshold", "field_missing", "value"),
            new RustValidationIssue("auto_resolve", "auto_resolve", "unsupported_schema", "schema_version"),
        ]);

        await notice.CheckAsync(new EnabledAlertRules([rule], []), CancellationToken.None);

        repository.Verify(r => r.RecordUnevaluableNoticeAsync(
            rule, UnevaluableRuleNotice.VersionOf(rule), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.RecordUnevaluableNoticeAsync(
            It.IsAny<AlertRuleSnapshot>(), UnevaluableRuleNotice.PartsVersionOf(rule), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
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
