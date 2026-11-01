using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// The job hands the rewrite the cursor its row holds and stores where the rewrite stopped. The
/// rewrite's own paging is proven on PostgreSQL by <c>DeduplicateApsSnapshotCurvesTests</c>, which
/// resumes it a page at a time.
/// </summary>
[Trait("Category", "Unit")]
public class ApsSnapshotCurvesJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TenantContext Tenant = new(Guid.CreateVersion7(), "t", "T", true, IsDemo: false);

    [Fact]
    public async Task A_first_run_starts_at_the_beginning_and_resumes_from_the_last_row_it_walked()
    {
        var lastAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var lastId = Guid.CreateVersion7();
        var calls = new List<(Guid Tenant, (DateTime, Guid)? After)>();

        var outcome = await RunAsync(null, (tenant, after) =>
        {
            calls.Add((tenant, after));
            return new ApsSnapshotCurvesRewriter.Pass(3, (lastAt, lastId));
        });

        calls.Should().Equal([(Tenant.TenantId, ((DateTime, Guid)?)null)]);
        outcome.Should().Be(JobOutcome.Resume(new JobCursor(At: new DateTimeOffset(lastAt), Id: lastId), Now));
    }

    [Fact]
    public async Task A_run_hands_the_rewrite_the_stored_cursor_and_is_done_when_the_walk_ends()
    {
        var at = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var id = Guid.CreateVersion7();
        (DateTime, Guid)? seen = null;

        var outcome = await RunAsync(new JobCursor(At: at, Id: id), (_, after) =>
        {
            seen = after;
            return new ApsSnapshotCurvesRewriter.Pass(0, null);
        });

        seen.Should().Be((at.UtcDateTime, id));
        outcome.Should().Be(JobOutcome.Done);
    }

    private static async Task<JobOutcome> RunAsync(
        JobCursor? cursor, Func<Guid, (DateTime, Guid)?, ApsSnapshotCurvesRewriter.Pass> rewrite)
    {
        var database = $"aps_curves_job_{Guid.NewGuid()}";
        var factory = new Mock<INocturneDbContextFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(TestDbContextFactory.CreateInMemoryContext(database)));
        await using var provider = new ServiceCollection().AddSingleton(factory.Object).BuildServiceProvider();

        var job = new ApsSnapshotCurvesJob(
            NullLogger<ApsSnapshotCurvesJob>.Instance,
            (NocturneDbContext _, Guid tenant, (DateTime At, Guid Id)? after, CancellationToken _) =>
                Task.FromResult(rewrite(tenant, after)));
        return await job.RunAsync(new JobRun(Tenant, cursor, 0, provider, Now), CancellationToken.None);
    }
}
