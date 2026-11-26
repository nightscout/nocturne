using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Jobs;
using Nocturne.API.Services.Migration;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// A started migration is a run record and a request in the job ledger, written together, and the
/// scheduler's claim of that request is what runs it.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class MigrationRunJobIntegrationTests : ApiIntegrationTestBase
{
    public MigrationRunJobIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private TenantContext Tenant =>
        new(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, IsDemo: false);

    private IMigrationJobService Migrations => Fixture.Services.GetRequiredService<IMigrationJobService>();

    /// <summary>A source no request reaches, so a run of it ends at its first read.</summary>
    private static StartMigrationRequest Request() => new()
    {
        Mode = MigrationMode.Api,
        NightscoutUrl = "https://example-nightscout.invalid",
        NightscoutApiSecret = "synthetic-secret",
    };

    private async Task<MigrationRunEntity> RunRecordAsync(Guid jobId)
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        return await db.MigrationRuns.AsNoTracking().SingleAsync(r => r.Id == jobId);
    }

    private async Task<JobLedgerEntry> LedgerRowAsync() =>
        (await Fixture.Services.GetRequiredService<IJobLedger>().ListAsync(100_000, default)).Rows
            .Single(j => j.Job == MigrationJobService.RunJobName && j.TenantId == Fixture.TenantId);

    /// <summary>
    /// One claim of a scheduler over the host's services, which the fixture's host does not run
    /// (<see cref="ApiIntegrationTestFixture.RemoveJobScheduler"/>).
    /// </summary>
    private async Task RunDueJobsAsync()
    {
        var scheduler = ActivatorUtilities.CreateInstance<JobScheduler>(Fixture.Services);
        (await scheduler.PollAsync(default)).Should().BeGreaterThan(0, "the requested row is due");
        await scheduler.DrainAsync();
    }

    [Fact]
    public async Task AStartedMigration_IsStoredWithItsRequestEncrypted_AndRequestsItsJob()
    {
        var job = await Migrations.StartMigrationAsync(Request(), Tenant, starter: null);

        var run = await RunRecordAsync(job.Id);
        run.State.Should().Be(nameof(MigrationJobState.Pending));
        run.RequestEncrypted.Should().NotBeNullOrEmpty().And.NotContain("synthetic-secret");
        (await LedgerRowAsync()).NextDueAt.Should().NotBeNull("the row's job has been asked for");

        var refused = await Assert.ThrowsAsync<MigrationAlreadyRunningException>(
            () => Migrations.StartMigrationAsync(Request(), Tenant, starter: null));
        refused.JobId.Should().Be(job.Id);
    }

    [Fact]
    public async Task TheSchedulersClaim_RunsTheMigrationToItsEnd()
    {
        var job = await Migrations.StartMigrationAsync(Request(), Tenant, starter: null);

        await RunDueJobsAsync();

        var status = await Migrations.GetStatusAsync(Fixture.TenantId, job.Id);
        status.State.Should().Be(MigrationJobState.Failed, "the source cannot be reached");
        status.ErrorMessage.Should().NotBeNullOrEmpty();
        var run = await RunRecordAsync(job.Id);
        run.RequestEncrypted.Should().BeNull("the source's credentials are held only while the run is live");
        run.Checkpoint.Should().BeNull();
        var row = await LedgerRowAsync();
        row.NextDueAt.Should().BeNull("an ended run has no more slices");
        row.Attempts.Should().Be(0, "the run stores its own failure");

        var again = await Migrations.StartMigrationAsync(Request(), Tenant, starter: null);
        again.Id.Should().NotBe(job.Id);
        (await LedgerRowAsync()).NextDueAt.Should().NotBeNull("a finished row is due again when a new run asks for it");
    }

    [Fact]
    public async Task ACancelledMigration_IsNotRunByTheClaimItHadRequested()
    {
        var job = await Migrations.StartMigrationAsync(Request(), Tenant, starter: null);

        await Migrations.CancelAsync(Fixture.TenantId, job.Id);
        await RunDueJobsAsync();

        var run = await RunRecordAsync(job.Id);
        run.State.Should().Be(nameof(MigrationJobState.Cancelled));
        run.RequestEncrypted.Should().BeNull();
        (await LedgerRowAsync()).NextDueAt.Should().BeNull();
    }
}
