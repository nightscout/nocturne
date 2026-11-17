using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

[Trait("Category", "Unit")]
public class DeduplicationRunJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IDeduplicationAdmin> _admin = new();

    [Fact]
    public async Task A_slice_that_stops_short_is_due_again_at_once_from_where_it_stopped()
    {
        var from = new JobCursor(At: Now.AddDays(-30), Page: 2);
        var next = new JobCursor(At: Now.AddDays(-20), Page: 3);
        _admin.Setup(a => a.RunAsync(from, It.IsAny<CancellationToken>())).ReturnsAsync(next);

        var outcome = await RunAsync(from);

        outcome.Should().Be(JobOutcome.Resume(next, Now));
    }

    [Fact]
    public async Task A_run_that_has_ended_is_done()
    {
        _admin.Setup(a => a.RunAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync((JobCursor?)null);

        (await RunAsync(null)).Should().Be(JobOutcome.Done);
    }

    [Fact]
    public void The_run_has_rows_only_when_requested_and_one_at_a_time()
    {
        var job = new DeduplicationRunJob();

        job.Name.Should().Be(IDeduplicationAdmin.RunJob);
        job.Enrolment.Should().Be(JobEnrolment.OnRequest);
        job.MaxConcurrency.Should().Be(1);
    }

    private async Task<JobOutcome> RunAsync(JobCursor? cursor)
    {
        await using var services = new ServiceCollection().AddSingleton(_admin.Object).BuildServiceProvider();
        var tenant = new TenantContext(Guid.CreateVersion7(), "t", "T", true, IsDemo: false);
        return await new DeduplicationRunJob().RunAsync(new JobRun(tenant, cursor, 0, services, Now), CancellationToken.None);
    }
}
