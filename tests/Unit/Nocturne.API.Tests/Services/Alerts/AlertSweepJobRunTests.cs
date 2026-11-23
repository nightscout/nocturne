using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>How one run of <see cref="AlertSweepJob"/> ends, whatever its passes find.</summary>
[Trait("Category", "Unit")]
public class AlertSweepJobRunTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly Moq.Mock<Nocturne.Core.Contracts.Alerts.IAlertRepository> _repository =
        AlertSweepRuns.Repository(Tenant, () => null);

    [Fact]
    public async Task A_pass_that_throws_does_not_stop_the_later_passes_and_the_run_fails_with_the_first_error()
    {
        var first = new InvalidOperationException("hysteresis read failed");
        _repository
            .Setup(r => r.GetExcursionsInHysteresisAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(first);
        _repository
            .Setup(r => r.GetAutoResolveExcursionsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("auto-resolve read failed"));

        var run = () => RunAsync(active: true);

        (await run.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(first);
        _repository.Verify(r => r.GetExpiredSnoozedInstancesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.ReadEnabledRulesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.GetAutoResolveExcursionsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_run_that_finds_nothing_comes_due_an_interval_later_without_reading_the_tenant()
    {
        var outcome = await RunAsync(active: true);

        outcome.Should().Be(JobOutcome.Resume(null, Now + AlertSweepJob.Interval));
        _repository.Verify(r => r.GetTenantAlertContextAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_inactive_tenant_is_parked_without_a_read()
    {
        var outcome = await RunAsync(active: false);

        outcome.Should().Be(JobOutcome.Resume(null, Now + IScheduledJob.InactiveTenantRecheck));
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Every_enabled_rule_is_checked_for_a_condition_that_cannot_be_evaluated_without_a_reading()
    {
        var rules = new[]
        {
            new AlertRuleSnapshot(Guid.NewGuid(), Tenant, "Urgent low", AlertConditionType.Threshold,
                """{"threshold": 54}""", AlertRuleSeverity.Critical, "{}", 0, false, null),
            new AlertRuleSnapshot(Guid.NewGuid(), Tenant, "High", AlertConditionType.Threshold,
                """{"direction": "above", "value": 250}""", AlertRuleSeverity.Warning, "{}", 1, false, null),
        };
        _repository
            .Setup(r => r.ReadEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnabledAlertRules(rules, []));

        await RunAsync(active: true);

        _notice.Verify(n => n.CheckAsync(
            It.Is<EnabledAlertRules>(checkedRules => checkedRules.Rules.SequenceEqual(rules)),
            It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.ReadEnabledRulesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_notice_left_standing_when_the_tenants_only_rule_was_disabled_is_resolved()
    {
        var notice = new StandingUnevaluableNotice(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "v1", Dispatched: true);
        _repository
            .Setup(r => r.GetStandingUnevaluableNoticesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([notice]);
        var resolution = new Mock<IExcursionResolutionHandler>();
        var validator = new Mock<IAlertRuleConditionValidator>();

        await using var services = new ServiceCollection()
            .AddSingleton(_repository.Object)
            .AddSingleton(Mock.Of<IAlertRuleRepository>())
            .AddSingleton(Mock.Of<ITenantAccessor>(a => a.TenantId == Tenant))
            .AddSingleton(Mock.Of<IAlertDeliveryService>())
            .AddSingleton(Mock.Of<ISensorContextEnricher>())
            .AddSingleton(resolution.Object)
            .AddSingleton(new ConditionVersionLog())
            .AddSingleton(validator.Object)
            .AddSingleton(TimeProvider.System)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddScoped<IUnevaluableRuleNotice, UnevaluableRuleNotice>()
            .AddSingleton(NoHeldRules.Notice())
            .AddSingleton(Mock.Of<IClockAnomalyNotice>())
            .AddSingleton(Mock.Of<IAlertChannelNotice>())
            .BuildServiceProvider();
        await new AlertSweepJob(NullLogger<AlertSweepJob>.Instance).RunAsync(services, Tenant, Now, active: true);

        resolution.Verify(h => h.HandleClosedAsync(
            It.Is<ExcursionTransition>(t => t.ExcursionId == notice.ExcursionId && t.CloseReason == ExcursionCloseReason.RuleDisabled),
            Tenant, It.IsAny<CancellationToken>()), Times.Once,
            "with no enabled rule, every standing notice is one the run must resolve");
        validator.VerifyNoOtherCalls();
    }

    private readonly Mock<IUnevaluableRuleNotice> _notice = new();

    private async Task<JobOutcome> RunAsync(bool active)
    {
        await using var services = new ServiceCollection()
            .AddSingleton(_repository.Object)
            .AddSingleton(_notice.Object)
            .AddSingleton(Mock.Of<ICanonicalGlucoseService>())
            .AddSingleton(NoHeldRules.Notice())
            .AddSingleton(Mock.Of<IClockAnomalyNotice>())
            .AddSingleton(Mock.Of<IAlertChannelNotice>())
            .AddScoped(_ => Mock.Of<ITenantAccessor>())
            .BuildServiceProvider();
        return await new AlertSweepJob(NullLogger<AlertSweepJob>.Instance).RunAsync(services, Tenant, Now, active);
    }
}
