using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

[Trait("Category", "Unit")]
public class GlucoseStreamRecomputeJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<ISensorGlucoseRepository> _glucose = new();

    [Fact]
    public void Each_tenant_runs_once_a_day_spread_over_the_day()
    {
        var job = new GlucoseStreamRecomputeJob(NullLogger<GlucoseStreamRecomputeJob>.Instance);

        job.Name.Should().Be(ISensorGlucoseRepository.RecomputeStreamsJob);
        job.Enrolment.Should().Be(JobEnrolment.PerTenant);
        job.Spread.Should().Be(TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task A_run_walks_the_tenants_history_and_comes_due_a_day_later()
    {
        _glucose.Setup(g => g.RecomputeStreamsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);

        var outcome = await RunAsync(active: true);

        _glucose.Verify(g => g.RecomputeStreamsAsync(It.IsAny<CancellationToken>()), Times.Once);
        outcome.Should().Be(JobOutcome.Resume(null, Now + TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task An_inactive_tenant_is_parked_without_a_walk()
    {
        var outcome = await RunAsync(active: false);

        _glucose.VerifyNoOtherCalls();
        outcome.Should().Be(JobOutcome.Resume(null, Now + IScheduledJob.InactiveTenantRecheck));
    }

    private async Task<JobOutcome> RunAsync(bool active)
    {
        await using var services = new ServiceCollection().AddSingleton(_glucose.Object).BuildServiceProvider();
        var tenant = new TenantContext(Guid.CreateVersion7(), "t", "T", active, IsDemo: false);
        return await new GlucoseStreamRecomputeJob(NullLogger<GlucoseStreamRecomputeJob>.Instance)
            .RunAsync(new JobRun(tenant, null, 0, services, Now), CancellationToken.None);
    }
}
