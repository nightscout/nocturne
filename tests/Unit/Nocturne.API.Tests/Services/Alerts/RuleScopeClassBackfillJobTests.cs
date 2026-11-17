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
    public async Task It_writes_only_the_rules_whose_class_changed_one_statement_a_class_and_is_done()
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
        rules.Setup(r => r.SetScopeClassAsync(Tenant.TenantId, It.IsAny<RuleScopeClass>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, RuleScopeClass _, IReadOnlyCollection<Guid> ids, CancellationToken _) => ids.Count);

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
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(toLow.Id) && ids.Contains(alsoToLow.Id)),
            It.IsAny<CancellationToken>()), Times.Once);
        rules.Verify(r => r.SetScopeClassAsync(
            Tenant.TenantId, RuleScopeClass.High,
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(toHigh.Id)),
            It.IsAny<CancellationToken>()), Times.Once);
        rules.VerifyNoOtherCalls();
    }

    private static AlertRuleSnapshot Rule(string conditionParams, RuleScopeClass stored) =>
        new(Guid.CreateVersion7(), Tenant.TenantId, "rule", AlertConditionType.Threshold, conditionParams,
            AlertRuleSeverity.Warning, "{}", 0, false, null, ScopeClass: stored);
}
