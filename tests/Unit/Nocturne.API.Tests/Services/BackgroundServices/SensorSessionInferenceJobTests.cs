using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

[Trait("Category", "Unit")]
public class SensorSessionInferenceJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<ISensorSessionInferenceService> _inference = new();

    [Fact]
    public async Task A_run_infers_over_the_live_window_and_comes_due_an_interval_later()
    {
        var outcome = await RunAsync(active: true);

        _inference.Verify(i => i.InferAsync(
            Now.UtcDateTime - SensorSessionInferenceService.LiveWindow, Now.UtcDateTime, It.IsAny<CancellationToken>()));
        outcome.Should().Be(JobOutcome.Resume(null, Now + SensorSessionInferenceJob.Interval));
    }

    [Fact]
    public async Task An_inactive_tenant_is_not_inferred()
    {
        var outcome = await RunAsync(active: false);

        _inference.VerifyNoOtherCalls();
        outcome.Should().Be(JobOutcome.Resume(null, Now + SensorSessionInferenceJob.Interval));
    }

    private async Task<JobOutcome> RunAsync(bool active)
    {
        await using var services = new ServiceCollection().AddSingleton(_inference.Object).BuildServiceProvider();
        var tenant = new TenantContext(Guid.CreateVersion7(), "t", "T", active, IsDemo: false);
        return await new SensorSessionInferenceJob().RunAsync(new JobRun(tenant, null, 0, services, Now), CancellationToken.None);
    }
}
