using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

[Trait("Category", "Unit")]
public class DeduplicationReconciliationJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IDeduplicationService> _dedup = new();
    private readonly List<string> _calls = [];

    public DeduplicationReconciliationJobTests()
    {
        _dedup
            .Setup(d => d.ReconcileNewLinksAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("reconcile"))
            .ReturnsAsync(new ReconcileResult(0, true));
        _dedup
            .Setup(d => d.RepairStateSpanPrimariesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("repair"))
            .ReturnsAsync(0);
        _dedup
            .Setup(d => d.PruneSingletonLinksAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("prune"))
            .ReturnsAsync(0);
    }

    [Fact]
    public async Task A_run_reconciles_then_repairs_then_prunes_and_comes_due_an_interval_later()
    {
        var outcome = await RunAsync(active: true);

        _calls.Should().Equal(["reconcile", "repair", "prune"], "a prune may only delete what reconcile has already passed");
        outcome.Should().Be(JobOutcome.Resume(null, Now + DeduplicationReconciliationJob.Interval));
    }

    [Fact]
    public async Task A_failed_reconcile_fails_the_run_before_the_repair_and_the_prune()
    {
        _dedup
            .Setup(d => d.ReconcileNewLinksAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var run = () => RunAsync(active: true);

        await run.Should().ThrowAsync<InvalidOperationException>();
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_inactive_tenant_is_parked_without_a_reconcile()
    {
        var outcome = await RunAsync(active: false);

        _dedup.VerifyNoOtherCalls();
        outcome.Should().Be(JobOutcome.Resume(null, Now + IScheduledJob.InactiveTenantRecheck));
    }

    private async Task<JobOutcome> RunAsync(bool active)
    {
        await using var services = new ServiceCollection().AddSingleton(_dedup.Object).BuildServiceProvider();
        var tenant = new TenantContext(Guid.CreateVersion7(), "t", "T", active, IsDemo: false);
        return await new DeduplicationReconciliationJob(NullLogger<DeduplicationReconciliationJob>.Instance)
            .RunAsync(new JobRun(tenant, null, 0, services, Now), CancellationToken.None);
    }
}
