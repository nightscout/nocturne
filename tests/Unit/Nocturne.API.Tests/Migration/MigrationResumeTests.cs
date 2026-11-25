using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A migration job is run a slice at a time from the checkpoint on its run record, so a job whose
/// process stopped, at a slice's end or in the middle of one, goes on from where it was and imports
/// what one uninterrupted run does.
/// </summary>
public class MigrationResumeTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>Device statuses half an hour apart: one page in the recent part, two in the older.</summary>
    private static readonly DateTimeOffset[] DeviceStatuses =
        [.. Enumerable.Range(1, 2500).Select(i => Start.AddMinutes(-30 * i))];

    private static readonly DateTimeOffset[] Entries = [Start.AddDays(-1), Start.AddDays(-30)];

    private static string DeviceStatusId(DateTimeOffset at) => $"d{at.ToUnixTimeMilliseconds()}";

    private static string EntryId(DateTimeOffset at) => $"e{at.ToUnixTimeMilliseconds()}";

    /// <summary>A Nightscout that honours the date filter and the count, and can stop a request it is asked for.</summary>
    private sealed class PagedNightscout : HttpMessageHandler
    {
        /// <summary>Called with each device status request, counted from 1, before it is answered.</summary>
        public Action<int>? OnDeviceStatusRequest { get; set; }

        public int DeviceStatusRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = Uri.UnescapeDataString(uri.Query);

            if (uri.AbsolutePath == "/api/v1/devicestatus.json")
            {
                OnDeviceStatusRequest?.Invoke(++DeviceStatusRequests);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(uri.AbsolutePath switch
            {
                "/api/v1/entries.json" => Json(Within(Entries, query, "date").Select(at =>
                    $$"""{"_id":"{{EntryId(at)}}","date":{{at.ToUnixTimeMilliseconds()}}}""")),
                "/api/v1/devicestatus.json" => Json(Within(DeviceStatuses, query, "created_at").Select(at =>
                    $$"""{"_id":"{{DeviceStatusId(at)}}","created_at":"{{at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}}"}""")),
                "/api/v1/profile.json" => Json(["""{"_id":"p1","defaultProfile":"Default"}"""]),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("[]") },
            });
        }

        private static IEnumerable<DateTimeOffset> Within(IReadOnlyList<DateTimeOffset> records, string query, string field)
        {
            var upper = Bound(query, $"find[{field}][$lte]=");
            var lower = Bound(query, $"find[{field}][$gte]=");
            var count = int.Parse(Value(query, "count=")!, CultureInfo.InvariantCulture);
            return records
                .Where(at => (upper is null || at <= upper) && (lower is null || at >= lower))
                .OrderByDescending(at => at)
                .Take(count);
        }

        private static DateTimeOffset? Bound(string query, string marker) => Value(query, marker) switch
        {
            null => null,
            var value when long.TryParse(value, out var mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills),
            var value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
        };

        private static string? Value(string query, string marker)
        {
            var at = query.IndexOf(marker, StringComparison.Ordinal);
            return at < 0 ? null : query[(at + marker.Length)..].Split('&')[0];
        }

        private static HttpResponseMessage Json(IEnumerable<string> documents) => new(HttpStatusCode.OK)
        {
            Content = new StringContent("[" + string.Join(",", documents) + "]", Encoding.UTF8, "application/json"),
        };
    }

    private sealed class Import
    {
        public List<string> Stored { get; } = [];

        public PagedNightscout Source { get; } = new();

        public IInterceptor? Interceptor { get; init; }

        public ServiceProvider BuildProvider() => MigrationJobHarness.BuildProvider(
            Source,
            interceptor: Interceptor,
            entryOutcome: page =>
            {
                Stored.AddRange(page.Select(e => e.Id!));
                return new();
            },
            profileOutcome: _ =>
            {
                Stored.Add("profile");
                return new();
            },
            deviceStatusOutcome: page =>
            {
                Stored.AddRange(page.Select(d => d.Id!));
                return new();
            });
    }

    private static readonly string[] Expected =
        [.. Entries.Select(EntryId), .. DeviceStatuses.Select(DeviceStatusId), "profile"];

    private static StartMigrationRequest Request() => new()
    {
        Mode = MigrationMode.Api,
        NightscoutUrl = "https://example-nightscout.invalid",
        NightscoutApiSecret = "synthetic-secret",
        Collections = ["entries", "devicestatus", "profile"],
    };

    private static async Task<MigrationRunEntity> RunRecordAsync(IServiceProvider provider, Guid jobId)
    {
        await using var db = await provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync();
        return await db.MigrationRuns.AsNoTracking().SingleAsync(r => r.Id == jobId);
    }

    private static MigrationJob Resume(IServiceProvider provider, MigrationRunEntity run, TenantContext tenant, TimeProvider time) =>
        MigrationJob.Resume(run, Request(), tenant, NullLogger.Instance, provider, time);

    [Fact]
    public async Task A_job_run_a_step_per_slice_imports_what_one_slice_does()
    {
        var import = new Import();
        await using var provider = import.BuildProvider();
        var time = new FakeTimeProvider(Start);
        var service = MigrationJobHarness.CreateService(provider, time: time);
        var sliced = new MigrationJobService(
            NullLogger<MigrationJobService>.Instance,
            provider,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            provider.GetRequiredService<Nocturne.Infrastructure.Data.Services.INocturneDbContextFactory>(),
            new MigrationJobHarness.MarkingSecrets(),
            time)
        {
            SliceLength = TimeSpan.Zero,
        };
        var tenant = MigrationJobHarness.NewTenant();

        var job = await service.StartMigrationAsync(Request(), tenant, starter: null);
        var slices = 0;
        while (await sliced.RunSliceAsync(tenant, CancellationToken.None))
        {
            slices++;
            var live = await sliced.GetStatusAsync(tenant.TenantId, job.Id);
            live.State.Should().Be(MigrationJobState.Running);
            live.Phase.Should().Be(MigrationPhase.Importing);
        }

        slices.Should().BeGreaterThanOrEqualTo(5, "every page and every collection ends a slice of no length");
        import.Stored.Distinct().Should().BeEquivalentTo(Expected);
        import.Stored.Where(id => id == "profile").Should().ContainSingle();

        var status = await service.GetStatusAsync(tenant.TenantId, job.Id);
        status.State.Should().Be(MigrationJobState.Completed);
        status.ErrorMessage.Should().BeNull();
        status.CollectionProgress["entries"].DocumentsMigrated.Should().Be(Entries.Length);
        status.CollectionProgress["devicestatus"].IsComplete.Should().BeTrue();
        status.CollectionProgress["devicestatus"].DocumentsMigrated.Should().BeInRange(
            DeviceStatuses.Length, DeviceStatuses.Length + slices,
            "a resumed pass counts the record at its bound again");
    }

    [Fact]
    public async Task A_live_job_reports_its_progress_from_its_record()
    {
        var import = new Import();
        await using var provider = import.BuildProvider();
        var time = new FakeTimeProvider(Start);
        var tenant = MigrationJobHarness.NewTenant();
        var service = MigrationJobHarness.CreateService(provider, time: time);
        var job = await service.StartMigrationAsync(Request(), tenant, starter: null);

        using var stop = new CancellationTokenSource();
        import.Source.OnDeviceStatusRequest = request =>
        {
            if (request == 3)
                stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunSliceAsync(tenant, stop.Token));

        var status = await MigrationJobHarness.CreateService(provider, time: time).GetStatusAsync(tenant.TenantId, job.Id);
        status.State.Should().Be(MigrationJobState.Running);
        status.CurrentCollection.Should().Be("devicestatus");
        status.RecentDataReady.Should().BeTrue("the recent part of every collection was in before the host stopped");
        status.CollectionProgress["devicestatus"].DocumentsMigrated.Should().BeGreaterThan(1000);
        status.ProgressPercentage.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_slice_the_host_stops_leaves_the_job_live_and_the_next_one_goes_on_from_its_page()
    {
        var import = new Import();
        await using var provider = import.BuildProvider();
        var time = new FakeTimeProvider(Start);
        var tenant = MigrationJobHarness.NewTenant();
        var service = MigrationJobHarness.CreateService(provider, time: time);
        var job = await service.StartMigrationAsync(Request(), tenant, starter: null);

        using var stop = new CancellationTokenSource();
        import.Source.OnDeviceStatusRequest = request =>
        {
            if (request == 3)
                stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunSliceAsync(tenant, stop.Token));

        var stopped = await RunRecordAsync(provider, job.Id);
        stopped.State.Should().Be(nameof(MigrationJobState.Running));
        stopped.RequestEncrypted.Should().NotBeNull();
        var before = import.Source.DeviceStatusRequests;

        import.Source.OnDeviceStatusRequest = null;
        await MigrationJobHarness.RunToEndAsync(MigrationJobHarness.CreateService(provider, time: time), tenant);

        import.Stored.Distinct().Should().BeEquivalentTo(Expected);
        (import.Source.DeviceStatusRequests - before).Should().BeLessThan(
            3, "the pages stored before the stop are not asked for again");
        var ended = await RunRecordAsync(provider, job.Id);
        ended.State.Should().Be(nameof(MigrationJobState.Completed));
        ended.RequestEncrypted.Should().BeNull("the source's credentials are held only while the run is live");
        ended.Checkpoint.Should().BeNull();
    }

    /// <summary>
    /// A killed process stores nothing on its way out, so the record is put back to what the job
    /// wrote before the page it was reading: that write alone has to be enough to go on from.
    /// </summary>
    [Fact]
    public async Task A_job_whose_process_died_goes_on_from_the_checkpoint_it_stored_before_its_page()
    {
        var import = new Import();
        await using var provider = import.BuildProvider();
        var time = new FakeTimeProvider(Start);
        var tenant = MigrationJobHarness.NewTenant();
        var service = MigrationJobHarness.CreateService(provider, time: time);
        var job = await service.StartMigrationAsync(Request(), tenant, starter: null);

        using var stop = new CancellationTokenSource();
        MigrationRunEntity? beforePage = null;
        import.Source.OnDeviceStatusRequest = request =>
        {
            if (request != 3)
                return;

            beforePage = RunRecordAsync(provider, job.Id).GetAwaiter().GetResult();
            stop.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunSliceAsync(tenant, stop.Token));

        await using (var db = await provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync())
        {
            var run = await db.MigrationRuns.SingleAsync(r => r.Id == job.Id);
            (run.Checkpoint, run.Revision, run.CollectionOutcomes) =
                (beforePage!.Checkpoint, beforePage.Revision, beforePage.CollectionOutcomes);
            await db.SaveChangesAsync();
        }

        import.Source.OnDeviceStatusRequest = null;
        await MigrationJobHarness.RunToEndAsync(MigrationJobHarness.CreateService(provider, time: time), tenant);

        import.Stored.Distinct().Should().BeEquivalentTo(Expected);
        (await RunRecordAsync(provider, job.Id)).State.Should().Be(nameof(MigrationJobState.Completed));
    }

    /// <summary>Fails the first save that would end a run.</summary>
    private sealed class FailingFinalWrite : SaveChangesInterceptor
    {
        private int _armed = 1;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var ending = eventData.Context!.ChangeTracker.Entries<MigrationRunEntity>()
                .Any(e => e.Entity.State is nameof(MigrationJobState.Completed));

            return ending && Interlocked.Exchange(ref _armed, 0) == 1
                ? throw new DbUpdateException("the store did not answer")
                : ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task A_job_whose_end_could_not_be_stored_is_run_again_until_it_is()
    {
        var import = new Import { Interceptor = new FailingFinalWrite() };
        await using var provider = import.BuildProvider();
        var time = new FakeTimeProvider(Start);
        var tenant = MigrationJobHarness.NewTenant();
        var service = MigrationJobHarness.CreateService(provider, time: time);
        var job = await service.StartMigrationAsync(Request(), tenant, starter: null);

        (await service.RunSliceAsync(tenant, CancellationToken.None)).Should().BeTrue(
            "the record is still live, so the run is not over");
        (await RunRecordAsync(provider, job.Id)).State.Should().Be(nameof(MigrationJobState.Running));

        (await service.RunSliceAsync(tenant, CancellationToken.None)).Should().BeFalse();

        var ended = await RunRecordAsync(provider, job.Id);
        ended.State.Should().Be(nameof(MigrationJobState.Completed));
        ended.RequestEncrypted.Should().BeNull();
        import.Stored.Distinct().Should().BeEquivalentTo(Expected);
    }

    [Fact]
    public async Task A_checkpoint_of_another_version_starts_the_run_over()
    {
        var import = new Import();
        await using var provider = import.BuildProvider();
        var time = new FakeTimeProvider(Start);
        var tenant = MigrationJobHarness.NewTenant();
        var service = MigrationJobHarness.CreateService(provider, time: time);
        var job = await service.StartMigrationAsync(Request(), tenant, starter: null);
        await using (var db = await provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync())
        {
            var run = await db.MigrationRuns.SingleAsync(r => r.Id == job.Id);
            run.Checkpoint = $$"""{"version":{{MigrationJob.CheckpointVersion + 1}},"counted":true,"pass":7,"next":"entries"}""";
            await db.SaveChangesAsync();
        }

        await MigrationJobHarness.RunToEndAsync(service, tenant);

        import.Stored.Distinct().Should().BeEquivalentTo(Expected);
        (await RunRecordAsync(provider, job.Id)).State.Should().Be(nameof(MigrationJobState.Completed));
    }

    [Fact]
    public async Task The_stored_request_is_encrypted()
    {
        await using var provider = new Import().BuildProvider();
        var tenant = MigrationJobHarness.NewTenant();
        var job = await MigrationJobHarness.CreateService(provider).StartMigrationAsync(Request(), tenant, starter: null);

        var run = await RunRecordAsync(provider, job.Id);

        run.RequestEncrypted.Should().StartWith("encrypted:");
        run.Checkpoint.Should().NotContain("synthetic-secret");
        run.SourceDescription.Should().NotContain("synthetic-secret");
    }

    [Fact]
    public async Task A_job_cancelled_between_slices_does_not_run_again()
    {
        var import = new Import();
        await using var provider = import.BuildProvider();
        var time = new FakeTimeProvider(Start);
        var tenant = MigrationJobHarness.NewTenant();
        var service = new MigrationJobService(
            NullLogger<MigrationJobService>.Instance,
            provider,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            provider.GetRequiredService<Nocturne.Infrastructure.Data.Services.INocturneDbContextFactory>(),
            new MigrationJobHarness.MarkingSecrets(),
            time)
        {
            SliceLength = TimeSpan.Zero,
        };
        var job = await service.StartMigrationAsync(Request(), tenant, starter: null);
        (await service.RunSliceAsync(tenant, CancellationToken.None)).Should().BeTrue();
        var stored = import.Stored.Count;

        await service.CancelAsync(tenant.TenantId, job.Id);

        (await service.RunSliceAsync(tenant, CancellationToken.None)).Should().BeFalse();
        import.Stored.Should().HaveCount(stored);
        var run = await RunRecordAsync(provider, job.Id);
        run.State.Should().Be(nameof(MigrationJobState.Cancelled));
        run.RequestEncrypted.Should().BeNull();
    }

    [Fact]
    public async Task A_job_cancelled_while_another_process_runs_it_stops_before_its_next_page()
    {
        var import = new Import();
        await using var provider = import.BuildProvider();
        var time = new FakeTimeProvider(Start);
        var tenant = MigrationJobHarness.NewTenant();
        var running = MigrationJobHarness.CreateService(provider, time: time);
        var other = MigrationJobHarness.CreateService(provider, time: time);
        var job = await running.StartMigrationAsync(Request(), tenant, starter: null);

        import.Source.OnDeviceStatusRequest = request =>
        {
            if (request == 2)
                other.CancelAsync(tenant.TenantId, job.Id).GetAwaiter().GetResult();
        };

        (await running.RunSliceAsync(tenant, CancellationToken.None)).Should().BeFalse();

        import.Source.DeviceStatusRequests.Should().Be(2);
        (await RunRecordAsync(provider, job.Id)).State.Should().Be(nameof(MigrationJobState.Cancelled));
    }

    [Fact]
    public async Task A_holder_that_lost_the_job_to_another_process_writes_nothing_more()
    {
        var import = new Import();
        await using var provider = import.BuildProvider();
        var time = new FakeTimeProvider(Start);
        var tenant = MigrationJobHarness.NewTenant();
        var job = await MigrationJobHarness.CreateService(provider, time: time).StartMigrationAsync(Request(), tenant, starter: null);

        var stale = Resume(provider, await RunRecordAsync(provider, job.Id), tenant, time);
        var holder = Resume(provider, await RunRecordAsync(provider, job.Id), tenant, time);
        (await holder.ExecuteAsync(TimeSpan.Zero, CancellationToken.None)).Should().BeTrue();
        var held = await RunRecordAsync(provider, job.Id);
        var stored = import.Stored.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => stale.ExecuteAsync(TimeSpan.MaxValue, CancellationToken.None));

        import.Stored.Should().HaveCount(stored);
        var after = await RunRecordAsync(provider, job.Id);
        after.State.Should().Be(nameof(MigrationJobState.Running));
        after.Revision.Should().Be(held.Revision);
        after.Checkpoint.Should().Be(held.Checkpoint);
    }

    [Fact]
    public async Task A_live_job_whose_request_cannot_be_read_fails_with_what_to_do()
    {
        await using var provider = new Import().BuildProvider();
        var tenant = MigrationJobHarness.NewTenant();
        var service = MigrationJobHarness.CreateService(provider);
        var job = await service.StartMigrationAsync(Request(), tenant, starter: null);
        await using (var db = await provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync())
        {
            (await db.MigrationRuns.SingleAsync(r => r.Id == job.Id)).RequestEncrypted = "written under another key";
            await db.SaveChangesAsync();
        }

        (await service.RunSliceAsync(tenant, CancellationToken.None)).Should().BeFalse();

        var status = await service.GetStatusAsync(tenant.TenantId, job.Id);
        status.State.Should().Be(MigrationJobState.Failed);
        status.ErrorMessage.Should().Contain("Start the import again");
    }

    [Fact]
    public async Task A_tenant_with_no_live_job_has_nothing_to_run()
    {
        await using var provider = new Import().BuildProvider();

        var more = await MigrationJobHarness.CreateService(provider)
            .RunSliceAsync(MigrationJobHarness.NewTenant(), CancellationToken.None);

        more.Should().BeFalse();
    }
}
