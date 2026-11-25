using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// The rule list says which rules cover low glucose and which of them is the last one, so the
/// page can ask before the last one is turned off or deleted.
/// </summary>
[Trait("Category", "Unit")]
public class AlertRulesControllerLowCoverageTests
{
    private const string Unreadable = "unreadable";
    private static readonly Guid Tenant = Guid.Parse("36163616-aaaa-7aaa-8aaa-aaaaaaaaaaaa");

    private static AlertRuleEntity Rule(
        string name,
        RuleScopeClass scopeClass = RuleScopeClass.Low,
        bool enabled = true,
        bool channel = true)
    {
        var id = Guid.NewGuid();
        return new AlertRuleEntity
        {
            Id = id,
            TenantId = Tenant,
            Name = name,
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = "{}",
            IsEnabled = enabled,
            ScopeClass = scopeClass,
            Channels = channel
                ? [new AlertRuleChannelEntity
                {
                    Id = Guid.NewGuid(),
                    TenantId = Tenant,
                    AlertRuleId = id,
                    ChannelType = ChannelType.Webhook,
                    Destination = "https://example.test/hook",
                }]
                : [],
        };
    }

    private static AlertRulesController Create(params AlertRuleEntity[] rules) =>
        Create(false, rules);

    private static AlertRulesController Create(bool engineRejectsUnreadable, params AlertRuleEntity[] rules)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new NocturneDbContext(options) { TenantId = Tenant };
        db.AlertRules.AddRange(rules);
        db.SaveChanges();

        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.Validate(
                It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(new List<RustValidationIssue>());
        if (engineRejectsUnreadable)
        {
            validator
                .Setup(v => v.Validate(
                    It.IsAny<AlertConditionType>(), It.Is<string>(p => p == Unreadable), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .Throws(new RustAlertEngineException("cannot check"));
        }

        return new AlertRulesController(
            new TestNocturneDbContextFactory(db),
            new TenantMembershipRepository(new TestNocturneDbContextFactory(db), AccountLinkRoutes.Off),
            new SubjectRepository(new TestNocturneDbContextFactory(db), AccountLinkRoutes.Off),
            new AlertRepository(new TestNocturneDbContextFactory(db)),
            new AlertRuleRepository(new TestNocturneDbContextFactory(db), TimeProvider.System),
            MockTenantAccessor.Create(db.TenantId).Object,
            Mock.Of<IAlertReferenceService>(),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            validator.Object,
            Mock.Of<ISecretEncryptionService>(),
            Rearms.Unannounced(),
            new AlertRuleRetirement(
                Mock.Of<IExcursionTracker>(), Mock.Of<IExcursionResolutionHandler>(), Mock.Of<IUnevaluableRuleNotice>()),
            Mock.Of<ILogger<AlertRulesController>>(), TimeProvider.System);
    }

    private static async Task<List<AlertRuleResponse>> List(AlertRulesController controller)
    {
        var result = await controller.GetRules(CancellationToken.None);
        return result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<List<AlertRuleResponse>>().Subject;
    }

    [Fact]
    public async Task The_only_covering_rule_is_the_last_cover_and_no_other_rule_is()
    {
        var low = Rule("Low");
        var high = Rule("High", RuleScopeClass.High);
        var off = Rule("Low off", enabled: false);

        var rules = await List(Create(low, high, off));

        rules.Single(r => r.Id == low.Id).Should().Match<AlertRuleResponse>(r => r.CoversLow && r.IsLastLowCover);
        rules.Where(r => r.Id != low.Id).Should().OnlyContain(r => !r.CoversLow && !r.IsLastLowCover);
    }

    [Fact]
    public async Task With_two_covering_rules_neither_is_the_last_cover()
    {
        var rules = await List(Create(Rule("Low"), Rule("Composite", RuleScopeClass.Composite)));

        rules.Should().OnlyContain(r => r.CoversLow && !r.IsLastLowCover);
    }

    [Fact]
    public async Task A_tenant_with_no_covering_rule_has_no_rule_marked_covering()
    {
        var rules = await List(Create(
            Rule("High", RuleScopeClass.High),
            Rule("Low with no channel", channel: false)));

        rules.Should().OnlyContain(r => !r.CoversLow && !r.IsLastLowCover);
    }

    [Fact]
    public async Task A_single_rule_read_judges_the_rule_against_the_tenants_other_rules()
    {
        var low = Rule("Low");
        var other = Rule("Other low");
        var controller = Create(low, other);

        var result = await controller.GetRule(low.Id, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<AlertRuleResponse>()
            .Which.Should().Match<AlertRuleResponse>(r => r.CoversLow && !r.IsLastLowCover);
    }

    [Fact]
    public async Task Turning_off_the_last_cover_is_not_refused_and_leaves_no_rule_covering()
    {
        var low = Rule("Low");
        var controller = Create(low);

        var result = await controller.ToggleRule(low.Id, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<AlertRuleResponse>()
            .Which.Should().Match<AlertRuleResponse>(r => !r.IsEnabled && !r.CoversLow && !r.IsLastLowCover);
    }

    [Fact]
    public async Task A_rule_the_engine_could_not_check_does_not_cover_lows()
    {
        var unreadable = Rule("Unreadable");
        unreadable.ConditionParams = Unreadable;
        var rules = await List(Create(true, unreadable));

        rules.Should().ContainSingle()
            .Which.Should().Match<AlertRuleResponse>(r => !r.CoversLow && !r.IsLastLowCover);
    }

    [Fact]
    public async Task A_rule_the_engine_could_not_check_leaves_the_other_rule_the_last_cover()
    {
        var unreadable = Rule("Unreadable");
        unreadable.ConditionParams = Unreadable;
        var low = Rule("Low");
        var rules = await List(Create(true, unreadable, low));

        rules.Single(r => r.Id == low.Id).IsLastLowCover.Should().BeTrue();
        rules.Single(r => r.Id == unreadable.Id).CoversLow.Should().BeFalse();
    }
}
