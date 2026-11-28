using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class RuleScopeClassBackfillJobTests
{
    private static readonly TenantContext Tenant = new(Guid.CreateVersion7(), "t", "T", true, IsDemo: false);

    [Fact]
    public async Task It_writes_only_the_rules_whose_class_changed_one_call_a_class_with_the_body_it_classified_and_is_done()
    {
        var unchanged = Rule("""{"unchanged":true}""", RuleScopeClass.Low);
        var toLow = Rule("""{"low":1}""", RuleScopeClass.Undirected);
        var alsoToLow = Rule("""{"low":2}""", RuleScopeClass.Undirected);
        var toHigh = Rule("""{"high":1}""", RuleScopeClass.Undirected);

        var classifier = new Mock<IRuleScopeClassifier>();
        classifier.Setup(c => c.Classify(It.IsAny<AlertConditionType>(), It.IsAny<string>()))
            .Returns((AlertConditionType _, string json) =>
                json.Contains("high") ? RuleScopeClass.High : RuleScopeClass.Low);
        var alerts = new Mock<IAlertRepository>();
        alerts.Setup(a => a.GetRuleSnapshotsAsync(Tenant.TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([unchanged, toLow, alsoToLow, toHigh]);
        var rules = new Mock<IAlertRuleRepository>();
        rules.Setup(r => r.SetScopeClassAsync(Tenant.TenantId, It.IsAny<RuleScopeClass>(), It.IsAny<IReadOnlyCollection<ClassifiedRule>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, RuleScopeClass _, IReadOnlyCollection<ClassifiedRule> written, CancellationToken _) => written.Count);

        await using var provider = new ServiceCollection()
            .AddSingleton(classifier.Object)
            .AddSingleton(alerts.Object)
            .AddSingleton(rules.Object)
            .BuildServiceProvider();
        var outcome = await new RuleScopeClassBackfillJob(NullLogger<RuleScopeClassBackfillJob>.Instance).RunAsync(
            new JobRun(Tenant, null, 0, provider, DateTimeOffset.UnixEpoch), CancellationToken.None);

        outcome.Should().Be(JobOutcome.Done);
        rules.Verify(r => r.SetScopeClassAsync(
            Tenant.TenantId, RuleScopeClass.Low,
            It.Is<IReadOnlyCollection<ClassifiedRule>>(written => written.Count == 2
                && written.Contains(Classified(toLow)) && written.Contains(Classified(alsoToLow))),
            It.IsAny<CancellationToken>()), Times.Once);
        rules.Verify(r => r.SetScopeClassAsync(
            Tenant.TenantId, RuleScopeClass.High,
            It.Is<IReadOnlyCollection<ClassifiedRule>>(written => written.Count == 1 && written.Contains(Classified(toHigh))),
            It.IsAny<CancellationToken>()), Times.Once);
        rules.VerifyNoOtherCalls();
    }

    [Fact]
    public void Its_name_differs_from_the_name_every_tenant_already_finished()
    {
        new RuleScopeClassBackfillJob(NullLogger<RuleScopeClassBackfillJob>.Instance).Name
            .Should().NotBe("alert-rule-scope-class-backfill");
    }

    private static ClassifiedRule Classified(AlertRuleSnapshot rule) =>
        new(rule.Id, rule.ConditionType, rule.ConditionParams);

    private static AlertRuleSnapshot Rule(string conditionParams, RuleScopeClass stored) =>
        new(Guid.CreateVersion7(), Tenant.TenantId, "rule", AlertConditionType.Threshold, conditionParams,
            AlertRuleSeverity.Warning, "{}", 0, false, null, ScopeClass: stored);
}
