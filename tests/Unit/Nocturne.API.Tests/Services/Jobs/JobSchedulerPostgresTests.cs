using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Jobs;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.API.Tests.Services.Jobs;

/// <summary>
/// <see cref="JobScheduler"/> over a migrated PostgreSQL ledger. A "restart" is a fresh scheduler
/// over the same database: it has nothing in memory, so whatever it does it read from the ledger.
/// Each test starts with no tenants and an empty ledger, since a per-tenant job enrols every tenant.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JobSchedulerPostgresTests(JobSchedulerPostgresTests.Database database)
    : IClassFixture<JobSchedulerPostgresTests.Database>, IAsyncLifetime
{
    public sealed class Database : IAsyncLifetime
    {
        public TestDatabase Created { get; private set; } = null!;

        public async Task InitializeAsync() =>
            Created = await SharedPostgres.CreateMigratedDatabaseAsync("job_scheduler");

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly List<ServiceProvider> _providers = [];
    private readonly string _app = database.Created.AppConnectionString;
    private readonly string _migrator = database.Created.MigratorConnectionString;

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var reset = new NpgsqlCommand("DELETE FROM job_ledger; DELETE FROM tenants;", conn);
        await reset.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();
    }

    [Fact]
    public async Task A_restarted_scheduler_resumes_from_the_stored_cursor_and_never_reruns_a_finished_row()
    {
        var tenant = await SeedTenantAsync();
        var firstRuns = new PagingJob();
        var first = Scheduler(firstRuns);

        await first.EnrolAsync(default);
        await RunDueAsync(first);
        firstRuns.Pages.Should().Equal([(tenant, (int?)null)], "the first process runs the first page and stops");

        var restartedRuns = new PagingJob();
        var restarted = Scheduler(restartedRuns);
        await restarted.EnrolAsync(default);
        while (await RunDueAsync(restarted) > 0)
        {
        }

        restartedRuns.Pages.Select(p => p.Page).Should().Equal(2, 3, 4);

        _clock.Advance(TimeSpan.FromDays(30));
        var again = new PagingJob();
        var third = Scheduler(again);
        await third.EnrolAsync(default);
        (await RunDueAsync(third)).Should().Be(0);
        again.Pages.Should().BeEmpty("a row the job finished is never due again");
        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().BeNull();
    }

    [Fact]
    public async Task Two_schedulers_on_one_ledger_never_run_a_row_twice()
    {
        var tenants = new List<Guid>();
        for (var i = 0; i < 6; i++)
            tenants.Add(await SeedTenantAsync());
        var job = new GatedJob();
        var a = Scheduler(job);
        var b = Scheduler(job);
        await a.EnrolAsync(default);

        var claimed = await Task.WhenAll(a.PollAsync(default), b.PollAsync(default));
        await job.AllStarted(6).WaitAsync(TimeSpan.FromSeconds(30));
        job.Release();
        await Task.WhenAll(a.DrainAsync(), b.DrainAsync());

        claimed.Sum().Should().Be(6);
        job.Overlapped.Should().BeFalse();
        job.Runs.Should().BeEquivalentTo(tenants);
        (await Task.WhenAll(a.PollAsync(default), b.PollAsync(default))).Sum().Should().Be(0);
    }

    [Fact]
    public async Task A_row_whose_holder_died_is_claimed_once_its_lease_runs_out()
    {
        var tenant = await SeedTenantAsync();
        var job = new PagingJob { DoneAfter = 1 };
        var survivor = Scheduler(job);
        await survivor.EnrolAsync(default);
        var ledger = Ledger();
        var dead = (await ledger.ClaimDueAsync("dead", Only(PagingJob.JobName), NoGroups, 1, _clock.GetUtcNow(), _clock.GetUtcNow() + JobScheduler.LeaseLength, default)).Single();

        _clock.Advance(JobScheduler.LeaseLength - TimeSpan.FromSeconds(1));
        (await RunDueAsync(survivor)).Should().Be(0, "the dead holder's lease has not run out");

        _clock.Advance(TimeSpan.FromSeconds(2));
        (await RunDueAsync(survivor)).Should().Be(1);
        job.Pages.Should().Equal([(tenant, (int?)null)]);

        await ledger.CompleteAsync(dead, _clock.GetUtcNow(), null, _clock.GetUtcNow(), default);
        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().BeNull("a holder that lost the row writes nothing");
    }

    [Fact]
    public async Task A_failing_job_keeps_its_cursor_and_backs_off_one_two_four_minutes_up_to_an_hour()
    {
        var tenant = await SeedTenantAsync();
        var job = new FailingJob(failures: 8);
        var scheduler = Scheduler(job);
        await scheduler.EnrolAsync(default);
        (await RunDueAsync(scheduler)).Should().Be(1, "the first run succeeds and leaves a cursor");

        var expected = new[] { 1, 2, 4, 8, 16, 32, 60, 60 };
        for (var attempt = 1; attempt <= expected.Length; attempt++)
        {
            (await RunDueAsync(scheduler)).Should().Be(1);
            var row = await RowAsync(FailingJob.JobName, tenant);
            row.Attempts.Should().Be(attempt);
            row.LastError.Should().Be($"InvalidOperationException: failure {attempt}");
            (row.NextDueAt - _clock.GetUtcNow()).Should().Be(TimeSpan.FromMinutes(expected[attempt - 1]));

            _clock.Advance(TimeSpan.FromMinutes(expected[attempt - 1]) - TimeSpan.FromSeconds(1));
            (await RunDueAsync(scheduler)).Should().Be(0, "the row is not due before its backoff ends");
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        (await RunDueAsync(scheduler)).Should().Be(1);
        var healed = await RowAsync(FailingJob.JobName, tenant);
        healed.Attempts.Should().Be(0);
        healed.LastError.Should().BeNull();
        job.Cursors.Skip(1).Should().OnlyContain(c => c == FailingJob.Cursor, "every retry resumes from the cursor the last success left");
    }

    [Fact]
    public async Task A_job_with_a_lower_cap_backs_off_no_longer_than_it()
    {
        var tenant = await SeedTenantAsync();
        var scheduler = Scheduler(new FailingJob(failures: 3) { MaxBackoff = TimeSpan.FromMinutes(3) });
        await scheduler.EnrolAsync(default);
        await RunDueAsync(scheduler);

        var waits = new List<TimeSpan>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await RunDueAsync(scheduler);
            var wait = (await RowAsync(FailingJob.JobName, tenant)).NextDueAt!.Value - _clock.GetUtcNow();
            waits.Add(wait);
            _clock.Advance(wait);
        }

        waits.Should().Equal(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task A_recurring_job_is_not_run_again_by_a_restart_before_it_is_due()
    {
        var tenant = await SeedTenantAsync();
        var job = new DailyJob();
        var first = Scheduler(job);
        await first.EnrolAsync(default);
        (await RunDueAsync(first)).Should().Be(1);

        _clock.Advance(TimeSpan.FromHours(23));
        var restarted = Scheduler(job);
        await restarted.EnrolAsync(default);
        (await RunDueAsync(restarted)).Should().Be(0, "the restart replays nothing that is not yet due");

        _clock.Advance(TimeSpan.FromHours(1));
        (await RunDueAsync(restarted)).Should().Be(1);
        job.Runs.Should().Be(2);
        (await RowAsync(DailyJob.JobName, tenant)).NextDueAt.Should().Be(Start + TimeSpan.FromDays(2));
    }

    [Fact]
    public async Task A_run_cancelled_by_shutdown_releases_its_row_for_the_next_holder_at_once()
    {
        var tenant = await SeedTenantAsync();
        var job = new GatedJob();
        var stopping = Scheduler(job);
        await stopping.EnrolAsync(default);
        using var shutdown = new CancellationTokenSource();

        (await stopping.PollAsync(shutdown.Token)).Should().Be(1);
        await job.AllStarted(1).WaitAsync(TimeSpan.FromSeconds(30));
        await shutdown.CancelAsync();
        await stopping.DrainAsync();

        var row = await RowAsync(GatedJob.JobName, tenant);
        row.LeaseHolder.Should().BeNull();
        row.Attempts.Should().Be(0, "a shutdown is not a failure");
        row.NextDueAt.Should().Be(Start);

        job.Release();
        (await RunDueAsync(Scheduler(job))).Should().Be(1);
    }

    [Fact]
    public async Task A_row_naming_a_job_this_instance_lacks_is_left_for_an_instance_that_has_it()
    {
        var tenant = await SeedTenantAsync();
        await Ledger().RequestAsync("newer-job", tenant, default);

        (await RunDueAsync(Scheduler(new DailyJob()))).Should().Be(0);

        var row = await RowAsync("newer-job", tenant);
        row.LeaseHolder.Should().BeNull();
        row.Attempts.Should().Be(0);
        row.NextDueAt.Should().Be(Start);
    }

    [Fact]
    public async Task A_claim_that_lost_its_row_writes_nothing_even_when_the_same_process_reclaimed_it()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        var first = (await ledger.ClaimDueAsync("process", Only(PagingJob.JobName), NoGroups, 1, Start, Start + JobScheduler.LeaseLength, default)).Single();

        _clock.Advance(JobScheduler.LeaseLength + TimeSpan.FromSeconds(1));
        var now = _clock.GetUtcNow();
        var second = (await ledger.ClaimDueAsync("process", Only(PagingJob.JobName), NoGroups, 1, now, now + JobScheduler.LeaseLength, default)).Single();

        second.Holder.Should().NotBe(first.Holder);
        await ledger.CompleteAsync(first, now, null, null, default);
        await ledger.FailAsync(first, "late", now.AddHours(1), default);
        await ledger.ReleaseAsync(first, default);
        (await ledger.ExtendLeaseAsync(first, now.AddHours(1), default)).Should().BeFalse();

        var row = await RowAsync(PagingJob.JobName, tenant);
        row.LeaseHolder.Should().Be(second.Holder);
        row.LeaseUntil.Should().Be(now + JobScheduler.LeaseLength);
        row.NextDueAt.Should().Be(Start);
        row.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task The_heartbeat_keeps_a_long_run_leased_past_its_first_lease()
    {
        var tenant = await SeedTenantAsync();
        var job = new GatedJob();
        var running = Scheduler(job);
        await running.EnrolAsync(default);
        await running.PollAsync(default);
        await job.AllStarted(1).WaitAsync(TimeSpan.FromSeconds(30));

        _clock.Advance(JobScheduler.HeartbeatInterval);
        await WaitForAsync(async () =>
            (await RowAsync(GatedJob.JobName, tenant)).LeaseUntil == Start + JobScheduler.HeartbeatInterval + JobScheduler.LeaseLength);
        _clock.Advance(JobScheduler.LeaseLength - JobScheduler.HeartbeatInterval + TimeSpan.FromSeconds(10));

        (await RunDueAsync(Scheduler(job))).Should().Be(0, "the heartbeat extended the lease past its first expiry");
        job.Release();
        await running.DrainAsync();
        job.Runs.Should().ContainSingle();
    }

    [Fact]
    public async Task A_run_whose_lease_was_taken_is_cancelled_and_leaves_the_row_to_the_new_holder()
    {
        var tenant = await SeedTenantAsync();
        var job = new GatedJob();
        var running = Scheduler(job);
        await running.EnrolAsync(default);
        await running.PollAsync(default);
        await job.AllStarted(1).WaitAsync(TimeSpan.FromSeconds(30));
        await using (var conn = new NpgsqlConnection(_migrator))
        {
            await conn.OpenAsync();
            await using var steal = new NpgsqlCommand("UPDATE job_ledger SET lease_holder = 'other:claim'", conn);
            await steal.ExecuteNonQueryAsync();
        }

        _clock.Advance(JobScheduler.HeartbeatInterval);
        await running.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30));

        job.Cancelled.Should().Be(1);
        job.Runs.Should().BeEmpty();
        var row = await RowAsync(GatedJob.JobName, tenant);
        row.LeaseHolder.Should().Be("other:claim");
        row.Attempts.Should().Be(0);
        row.LastError.Should().BeNull();
        row.NextDueAt.Should().Be(Start);
    }

    [Fact]
    public async Task A_request_reopens_a_finished_row_and_one_made_mid_run_survives_the_runs_outcome()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(GatedJob.JobName, tenant, default);
        var job = new GatedJob();
        var scheduler = Scheduler(job);

        await scheduler.PollAsync(default);
        await job.AllStarted(1).WaitAsync(TimeSpan.FromSeconds(30));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await ledger.RequestAsync(GatedJob.JobName, tenant, default);
        job.Release();
        await scheduler.DrainAsync();

        (await RowAsync(GatedJob.JobName, tenant)).NextDueAt.Should().Be(Start + TimeSpan.FromMinutes(1));
        (await RunDueAsync(scheduler)).Should().Be(1);
        (await RowAsync(GatedJob.JobName, tenant)).NextDueAt.Should().BeNull();

        await ledger.RequestAsync(GatedJob.JobName, tenant, default);
        (await RunDueAsync(scheduler)).Should().Be(1);
        job.Runs.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_request_made_during_a_run_that_fails_is_not_held_back_by_the_backoff()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(GatedJob.JobName, tenant, default);
        var job = new GatedJob { Fails = true };
        var scheduler = Scheduler(job);

        await scheduler.PollAsync(default);
        await job.AllStarted(1).WaitAsync(TimeSpan.FromSeconds(30));
        _clock.Advance(TimeSpan.FromSeconds(30));
        await ledger.RequestAsync(GatedJob.JobName, tenant, default);
        job.Release();
        await scheduler.DrainAsync();

        var row = await RowAsync(GatedJob.JobName, tenant);
        row.Attempts.Should().Be(1);
        row.NextDueAt.Should().Be(Start + TimeSpan.FromSeconds(30), "the request is due before the one-minute backoff");
    }

    [Fact]
    public async Task A_request_on_a_row_whose_holder_died_leaves_it_due()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        await ledger.ClaimDueAsync("dead", Only(PagingJob.JobName), NoGroups, 1, Start, Start + JobScheduler.LeaseLength, default);

        _clock.Advance(JobScheduler.LeaseLength + TimeSpan.FromMinutes(1));
        var requested = _clock.GetUtcNow();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);

        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(
            requested, "the row still names a holder, which may yet be finishing, so the request marks it");
        (await RunDueAsync(Scheduler(new PagingJob()))).Should().Be(1, "the dead holder's lease has run out");
    }

    [Fact]
    public async Task A_request_landing_after_the_lease_ran_out_but_before_the_holder_finished_is_kept()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        var slow = (await ledger.ClaimDueAsync("slow", Only(PagingJob.JobName), NoGroups, 1, Start, Start + JobScheduler.LeaseLength, default)).Single();

        _clock.Advance(JobScheduler.LeaseLength + TimeSpan.FromMinutes(1));
        var requested = _clock.GetUtcNow();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        await ledger.CompleteAsync(slow, requested, null, requested + TimeSpan.FromHours(1), default);

        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(
            requested, "the request came after the run started, so the run's later due time does not replace it");
    }

    [Fact]
    public async Task A_delayed_request_reopens_a_finished_row_and_defers_a_due_one_and_an_immediate_request_pulls_it_back()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        var lease = (await ledger.ClaimDueAsync("p", Only(PagingJob.JobName), NoGroups, 1, Start, Start + JobScheduler.LeaseLength, default)).Single();
        await ledger.CompleteAsync(lease, Start, null, null, default);

        await ledger.RequestAsync(PagingJob.JobName, tenant, TimeSpan.FromMinutes(1), default);
        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(Start + TimeSpan.FromMinutes(1), "a finished row reopens after the delay");

        _clock.Advance(TimeSpan.FromSeconds(10));
        await ledger.RequestAsync(PagingJob.JobName, tenant, TimeSpan.FromMinutes(1), default);
        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(
            Start + TimeSpan.FromSeconds(70), "a delayed request defers the row to the delay after the last one");

        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(Start + TimeSpan.FromSeconds(10), "an immediate request pulls it back to now");
    }

    [Fact]
    public async Task On_a_running_row_a_delayed_request_overrides_an_immediate_one_made_before_it()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        var lease = (await ledger.ClaimDueAsync("p", Only(PagingJob.JobName), NoGroups, 1, Start, Start + JobScheduler.LeaseLength, default)).Single();

        _clock.Advance(TimeSpan.FromSeconds(2));
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        _clock.Advance(TimeSpan.FromSeconds(3));
        await ledger.RequestAsync(PagingJob.JobName, tenant, TimeSpan.FromMinutes(1), default);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ledger.CompleteAsync(lease, _clock.GetUtcNow(), null, Start + TimeSpan.FromMinutes(5), default);

        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(
            Start + TimeSpan.FromSeconds(65), "a configuration write settles everything pending");
    }

    [Fact]
    public async Task On_a_running_row_an_immediate_request_brings_a_delayed_one_forward()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        var lease = (await ledger.ClaimDueAsync("p", Only(PagingJob.JobName), NoGroups, 1, Start, Start + JobScheduler.LeaseLength, default)).Single();

        _clock.Advance(TimeSpan.FromSeconds(5));
        await ledger.RequestAsync(PagingJob.JobName, tenant, TimeSpan.FromMinutes(1), default);
        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(Start + TimeSpan.FromSeconds(65));
        _clock.Advance(TimeSpan.FromSeconds(2));
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        _clock.Advance(TimeSpan.FromSeconds(3));
        await ledger.CompleteAsync(lease, _clock.GetUtcNow(), null, Start + TimeSpan.FromMinutes(5), default);

        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(Start + TimeSpan.FromSeconds(7));
    }

    /// <summary>
    /// A long run outlives a delayed request's delay; a second configuration write still defers the
    /// row a minute past itself, not back to the first write's matured time.
    /// </summary>
    [Fact]
    public async Task On_a_long_run_a_second_delayed_request_defers_past_the_first_ones_matured_time()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        var lease = (await ledger.ClaimDueAsync("p", Only(PagingJob.JobName), NoGroups, 1, Start, Start + JobScheduler.LeaseLength, default)).Single();

        await ledger.RequestAsync(PagingJob.JobName, tenant, TimeSpan.FromMinutes(1), default);
        _clock.Advance(TimeSpan.FromSeconds(70));
        await ledger.RequestAsync(PagingJob.JobName, tenant, TimeSpan.FromMinutes(1), default);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ledger.CompleteAsync(lease, _clock.GetUtcNow(), null, Start + TimeSpan.FromMinutes(5), default);

        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(Start + TimeSpan.FromSeconds(130));
    }

    /// <summary>
    /// A configuration write during a run defers the row past a sooner outcome (an aligned time a few
    /// seconds out), so no sync starts between that write and the next one.
    /// </summary>
    [Fact]
    public async Task A_deferral_made_during_a_run_outlasts_a_sooner_outcome()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        var lease = (await ledger.ClaimDueAsync("p", Only(PagingJob.JobName), NoGroups, 1, Start, Start + JobScheduler.LeaseLength, default)).Single();

        _clock.Advance(TimeSpan.FromSeconds(5));
        await ledger.RequestAsync(PagingJob.JobName, tenant, TimeSpan.FromMinutes(1), default);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ledger.CompleteAsync(lease, _clock.GetUtcNow(), null, _clock.GetUtcNow() + TimeSpan.FromSeconds(30), default);

        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(Start + TimeSpan.FromSeconds(65));
    }

    [Fact]
    public async Task An_immediate_request_made_during_a_run_outlasts_a_later_outcome()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        var lease = (await ledger.ClaimDueAsync("p", Only(PagingJob.JobName), NoGroups, 1, Start, Start + JobScheduler.LeaseLength, default)).Single();

        _clock.Advance(TimeSpan.FromSeconds(5));
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ledger.CompleteAsync(lease, _clock.GetUtcNow(), null, _clock.GetUtcNow() + TimeSpan.FromMinutes(5), default);

        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(Start + TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// A deferral made during a run that fails is kept even when it is later than the retry: the retry
    /// would otherwise start the sync before the configuration has settled.
    /// </summary>
    [Fact]
    public async Task A_deferral_made_during_a_failed_run_is_kept_over_a_sooner_retry()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        var lease = (await ledger.ClaimDueAsync("p", Only(PagingJob.JobName), NoGroups, 1, Start, Start + JobScheduler.LeaseLength, default)).Single();

        _clock.Advance(TimeSpan.FromSeconds(5));
        await ledger.RequestAsync(PagingJob.JobName, tenant, TimeSpan.FromMinutes(3), default);
        _clock.Advance(TimeSpan.FromSeconds(5));
        var retry = _clock.GetUtcNow() + JobScheduler.Backoff(1, ConnectorBackgroundService<TestConnectorConfiguration>.ConnectorMaxBackoff);
        await ledger.FailAsync(lease, "boom", retry, default);

        retry.Should().BeBefore(Start + TimeSpan.FromSeconds(185));
        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(Start + TimeSpan.FromSeconds(185));
    }

    [Fact]
    public async Task An_immediate_request_made_during_a_failed_run_is_kept_over_a_later_retry()
    {
        var tenant = await SeedTenantAsync();
        var ledger = Ledger();
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        var lease = (await ledger.ClaimDueAsync("p", Only(PagingJob.JobName), NoGroups, 1, Start, Start + JobScheduler.LeaseLength, default)).Single();

        _clock.Advance(TimeSpan.FromSeconds(5));
        await ledger.RequestAsync(PagingJob.JobName, tenant, default);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ledger.FailAsync(
            lease, "boom",
            _clock.GetUtcNow() + JobScheduler.Backoff(1, ConnectorBackgroundService<TestConnectorConfiguration>.ConnectorMaxBackoff),
            default);

        (await RowAsync(PagingJob.JobName, tenant)).NextDueAt.Should().Be(Start + TimeSpan.FromSeconds(5));
    }

    [ConnectorRegistration("JobLedgerTest", "job-ledger-test", "JOBLEDGERTEST", "JobLedgerTest")]
    private sealed class TestConnectorConfiguration : BaseConnectorConfiguration;

    [Fact]
    public async Task A_platform_job_has_one_row_however_often_it_is_enrolled()
    {
        var job = new PlatformJob();
        var scheduler = Scheduler(job);
        await scheduler.EnrolAsync(default);
        await Scheduler(job).EnrolAsync(default);
        await Ledger().RequestAsync(PlatformJob.JobName, null, default);

        (await Ledger().ListAsync(1000, default)).Rows.Where(r => r.Job == PlatformJob.JobName)
            .Should().ContainSingle().Which.TenantId.Should().BeNull();
        (await RunDueAsync(scheduler)).Should().Be(1);
        job.Tenants.Should().Equal([(TenantContext?)null]);
    }

    [Fact]
    public async Task A_job_whose_runs_are_stuck_leaves_the_other_slots_to_other_jobs()
    {
        for (var i = 0; i < 6; i++)
            await SeedTenantAsync();
        var stuck = new GatedJob { MaxConcurrency = 2 };
        var daily = new DailyJob();
        var scheduler = Scheduler(new JobSchedulerOptions { Parallelism = 4 }, stuck, daily);
        await scheduler.EnrolAsync(default);

        await WaitForAsync(async () =>
        {
            await scheduler.PollAsync(default);
            await Task.Delay(20);
            return daily.Runs == 6;
        });
        await stuck.AllStarted(2).WaitAsync(TimeSpan.FromSeconds(30));

        stuck.Started.Should().Be(2, "the stuck job never holds more than its cap");
        stuck.Release();
        await scheduler.DrainAsync();
    }

    private static IReadOnlyDictionary<string, int> Only(string job) => new Dictionary<string, int> { [job] = int.MaxValue };

    private static readonly IReadOnlyDictionary<string, string> NoGroups = new Dictionary<string, string>();

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not hold within 30 seconds.");
            await Task.Delay(20);
        }
    }

    private async Task<int> RunDueAsync(JobScheduler scheduler)
    {
        var started = await scheduler.PollAsync(default);
        await scheduler.DrainAsync();
        return started;
    }

    private JobScheduler Scheduler(params IScheduledJob[] jobs) => Scheduler(new JobSchedulerOptions(), jobs);

    private JobScheduler Scheduler(JobSchedulerOptions schedulerOptions, params IScheduledJob[] jobs)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        var options = new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(_app).Options;
        services.AddSingleton<IDbContextFactory<NocturneDbContext>>(new Contexts(options));
        services.AddSingleton(sp => sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>().AsNocturneFactory());
        services.AddSingleton<TimeProvider>(_clock);
        services.AddTransient<IJobLedger, JobLedgerRepository>();
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return new JobScheduler(
            jobs,
            provider.GetRequiredService<IJobLedger>(),
            provider,
            _clock,
            new JobMetrics(provider.GetRequiredService<IMeterFactory>()),
            Options.Create(schedulerOptions),
            NullLogger<JobScheduler>.Instance);
    }

    private IJobLedger Ledger() => new JobLedgerRepository(
        new Contexts(new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(_app).Options).AsNocturneFactory(),
        _clock);

    private async Task<JobLedgerEntry> RowAsync(string job, Guid tenant) =>
        (await Ledger().ListAsync(100_000, default)).Rows.Single(r => r.Job == job && r.TenantId == tenant);

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var insert = new NpgsqlCommand(
            "INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES (@id, @slug, 'T', true, now(), now())", conn);
        insert.Parameters.AddWithValue("id", tenant);
        insert.Parameters.AddWithValue("slug", tenant.ToString("N"));
        await insert.ExecuteNonQueryAsync();
        return tenant;
    }

    private sealed class Contexts(DbContextOptions<NocturneDbContext> options) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    /// <summary>Walks pages 1 to <see cref="DoneAfter"/>, one a run.</summary>
    private sealed class PagingJob : IScheduledJob
    {
        public const string JobName = "test-paging";

        public int DoneAfter { get; init; } = 4;

        public ConcurrentQueue<(Guid Tenant, int? Page)> Pages { get; } = new();

        public string Name => JobName;

        public JobEnrolment Enrolment => JobEnrolment.PerTenant;

        public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            Pages.Enqueue((run.Tenant!.TenantId, run.Cursor?.Page));
            var page = run.Cursor?.Page ?? 1;
            return Task.FromResult(page >= DoneAfter
                ? JobOutcome.Done
                : JobOutcome.Resume(new JobCursor(Page: page + 1), run.StartedAt));
        }
    }

    /// <summary>Holds every run until <see cref="Release"/>, and notes whether two runs of one row overlapped.</summary>
    private sealed class GatedJob : IScheduledJob
    {
        public const string JobName = "test-gated";

        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<Guid, int> _running = new();
        private int _started;
        private int _cancelled;

        public int Cancelled => Volatile.Read(ref _cancelled);

        public int Started => Volatile.Read(ref _started);

        public int MaxConcurrency { get; init; } = int.MaxValue;

        public ConcurrentQueue<Guid> Runs { get; } = new();

        public bool Overlapped { get; private set; }

        public bool Fails { get; init; }

        public string Name => JobName;

        public JobEnrolment Enrolment => JobEnrolment.PerTenant;

        public void Release() => _gate.TrySetResult();

        public async Task AllStarted(int count)
        {
            while (Volatile.Read(ref _started) < count)
                await Task.Delay(10);
        }

        public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            var tenant = run.Tenant!.TenantId;
            if (_running.AddOrUpdate(tenant, 1, (_, n) => n + 1) > 1)
                Overlapped = true;
            Interlocked.Increment(ref _started);
            try
            {
                await _gate.Task.WaitAsync(ct);
                if (Fails)
                    throw new InvalidOperationException("gated run failed");
                Runs.Enqueue(tenant);
                return JobOutcome.Done;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
            finally
            {
                _running.AddOrUpdate(tenant, 0, (_, n) => n - 1);
            }
        }
    }

    /// <summary>Succeeds once leaving <see cref="Cursor"/>, fails <c>failures</c> times, then succeeds.</summary>
    private sealed class FailingJob(int failures) : IScheduledJob
    {
        public const string JobName = "test-failing";

        public static readonly JobCursor Cursor = new(Page: 7);

        private int _runs;

        public ConcurrentQueue<JobCursor?> Cursors { get; } = new();

        public string Name => JobName;

        public JobEnrolment Enrolment => JobEnrolment.PerTenant;

        public TimeSpan MaxBackoff { get; init; } = IScheduledJob.DefaultMaxBackoff;

        public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            Cursors.Enqueue(run.Cursor);
            var runs = Interlocked.Increment(ref _runs);
            if (runs > 1 && runs <= failures + 1)
                throw new InvalidOperationException($"failure {runs - 1}");
            return Task.FromResult(JobOutcome.Resume(Cursor, run.StartedAt));
        }
    }

    private sealed class DailyJob : IScheduledJob
    {
        public const string JobName = "test-daily";

        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public string Name => JobName;

        public JobEnrolment Enrolment => JobEnrolment.PerTenant;

        public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult(JobOutcome.Resume(null, run.StartedAt + TimeSpan.FromDays(1)));
        }
    }

    private sealed class PlatformJob : IScheduledJob
    {
        public const string JobName = "test-platform";

        public ConcurrentQueue<TenantContext?> Tenants { get; } = new();

        public string Name => JobName;

        public JobEnrolment Enrolment => JobEnrolment.Platform;

        public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            Tenants.Enqueue(run.Tenant);
            return Task.FromResult(JobOutcome.Done);
        }
    }
}
