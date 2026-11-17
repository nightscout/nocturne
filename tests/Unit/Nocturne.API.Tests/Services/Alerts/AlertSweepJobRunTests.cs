using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
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
        _repository.Verify(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()), Times.Once);
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

    private async Task<JobOutcome> RunAsync(bool active)
    {
        await using var services = new ServiceCollection()
            .AddSingleton(_repository.Object)
            .AddScoped(_ => Mock.Of<ITenantAccessor>())
            .BuildServiceProvider();
        return await new AlertSweepJob(NullLogger<AlertSweepJob>.Instance).RunAsync(services, Tenant, Now, active);
    }
}
