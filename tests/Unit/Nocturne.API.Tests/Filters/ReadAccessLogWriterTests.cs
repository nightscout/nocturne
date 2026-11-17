using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nocturne.API.Filters;
using Nocturne.API.Services.Audit;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Filters;

[Trait("Category", "Unit")]
public class ReadAccessLogWriterTests
{
    private static readonly Guid TenantA = Guid.CreateVersion7();
    private static readonly Guid TenantB = Guid.CreateVersion7();

    [Fact]
    public async Task DrainAsync_AfterTheRequestScopeIsDisposed_LandsAndReadsNoRequestState()
    {
        await using var harness = new Harness();

        ReadAccessLogWriter writer;
        await using (var request = harness.Provider.CreateAsyncScope())
        {
            var reader = request.ServiceProvider.GetRequiredService<ICategoryReadContext>();
            reader.MarkShare();
            reader.SetVisibleCategories("glucose.read");
            reader.SetFullHistory(false);
            writer = request.ServiceProvider.GetRequiredService<ReadAccessLogWriter>();
        }

        writer.Enqueue(Entry(TenantA));
        await harness.DrainAsync(writer);

        var saved = harness.Saves.Contexts.Should().ContainSingle().Subject;
        saved.TenantId.Should().Be(TenantA);
        saved.IsShareContext.Should().BeFalse();
        saved.VisibleCategories.Should().BeNull();
        saved.HistoryClamped.Should().BeFalse();
        saved.AuditContext.Should().BeNull();
        (await harness.CountAsync(TenantA)).Should().Be(1);
    }

    [Fact]
    public async Task Enqueue_WhenTheQueueIsFull_DropsNewEntriesWithoutBlocking()
    {
        await using var harness = new Harness();
        var writer = harness.Writer;
        const int overflow = 5;

        var enqueue = Task.Run(() =>
        {
            for (var i = 0; i < ReadAccessLogWriter.Capacity + overflow; i++)
                writer.Enqueue(Entry(TenantA, $"GET /api/v4/entries/{i}"));
        });

        (await Task.WhenAny(enqueue, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(
            enqueue, "a full queue must not make a request wait");
        await enqueue;

        await harness.DrainAsync(writer);
        (await harness.CountAsync(TenantA)).Should().Be(ReadAccessLogWriter.Capacity);
        await using var check = await harness.Contexts.CreateForTenantAsync(TenantA);
        (await check.ReadAccessLog.CountAsync(e => e.Endpoint == $"GET /api/v4/entries/{ReadAccessLogWriter.Capacity}"))
            .Should().Be(0, "the entry that found the queue full is the one dropped");
    }

    [Fact]
    public async Task Enqueue_WhenTheQueueStaysFull_LogsTheDropCountOncePerInterval()
    {
        await using var harness = new Harness();
        var writer = harness.Writer;
        for (var i = 0; i < ReadAccessLogWriter.Capacity; i++)
            writer.Enqueue(Entry(TenantA));

        for (var i = 0; i < 10; i++)
            writer.Enqueue(Entry(TenantA));
        harness.Logger.Warnings.Should().ContainSingle("only the first drop of an interval is logged")
            .Which.Should().Contain("Dropped 1 ");

        harness.Clock.Advance(ReadAccessLogWriter.DropLogInterval);
        writer.Enqueue(Entry(TenantA));

        harness.Logger.Warnings.Should().HaveCount(2);
        harness.Logger.Warnings.Last().Should().Contain("Dropped 10 ", "it counts the nine suppressed drops and this one");
    }

    [Fact]
    public async Task DrainAsync_WithManyTenants_WritesEachTenantsRowsOnItsOwnPinnedContext()
    {
        await using var harness = new Harness();
        var writer = harness.Writer;
        for (var i = 0; i < 5; i++)
        {
            writer.Enqueue(Entry(TenantA));
            writer.Enqueue(Entry(TenantB));
        }

        await harness.DrainAsync(writer);

        harness.Saves.Batches.Should().HaveCount(2, "one save per tenant group");
        harness.Saves.Batches.Should().OnlyContain(b => b.Rows.All(r => r.TenantId == b.PinnedTenant));
        harness.Saves.Batches.Select(b => b.PinnedTenant).Should().BeEquivalentTo([TenantA, TenantB]);
        (await harness.CountAsync(TenantA)).Should().Be(5);
        (await harness.CountAsync(TenantB)).Should().Be(5);
    }

    [Fact]
    public async Task DrainAsync_WithMoreThanABatch_SavesNoMoreThanABatchAtATime()
    {
        await using var harness = new Harness();
        var writer = harness.Writer;
        const int total = ReadAccessLogWriter.BatchSize * 2 + 1;
        for (var i = 0; i < total; i++)
            writer.Enqueue(Entry(TenantA));

        await harness.DrainAsync(writer);

        harness.Saves.Batches.Should().HaveCount(3);
        harness.Saves.Batches.Should().OnlyContain(b => b.Rows.Count <= ReadAccessLogWriter.BatchSize);
        (await harness.CountAsync(TenantA)).Should().Be(total);
    }

    [Fact]
    public async Task DrainAsync_WhenOneTenantsWriteFails_StillWritesTheOtherTenantsRows()
    {
        await using var harness = new Harness { FailingTenant = TenantA };
        var writer = harness.Writer;
        writer.Enqueue(Entry(TenantA));
        writer.Enqueue(Entry(TenantB));
        writer.Enqueue(Entry(TenantB));

        await harness.DrainAsync(writer);

        (await harness.CountAsync(TenantB)).Should().Be(2);
        (await harness.CountAsync(TenantA)).Should().Be(0);
        harness.Logger.Warnings.Should().ContainSingle().Which.Should().Contain(TenantA.ToString());
    }

    [Fact]
    public async Task DrainAsync_AfterAFailedBatch_KeepsDrainingLaterEntries()
    {
        await using var harness = new Harness { FailingTenant = TenantA };
        var writer = harness.Writer;
        var drain = writer.DrainAsync();

        writer.Enqueue(Entry(TenantA));
        await WaitForAsync(() => harness.Logger.Warnings.Any());
        writer.Enqueue(Entry(TenantB));
        await WaitForAsync(() => harness.Saves.Batches.Any(b => b.PinnedTenant == TenantB));

        writer.Complete();
        await drain;
        (await harness.CountAsync(TenantB)).Should().Be(1);
    }

    [Fact]
    public async Task StopAsync_WritesWhatIsStillQueued()
    {
        await using var harness = new Harness();
        var writer = harness.Writer;
        var service = new ReadAccessLogDrain(writer);
        await service.StartAsync(CancellationToken.None);

        const int perTenant = ReadAccessLogWriter.BatchSize * 3 / 2;
        for (var i = 0; i < perTenant * 2; i++)
            writer.Enqueue(Entry(i % 2 == 0 ? TenantA : TenantB));
        await service.StopAsync(CancellationToken.None);

        (await harness.CountAsync(TenantA)).Should().Be(perTenant);
        (await harness.CountAsync(TenantB)).Should().Be(perTenant);
    }

    [Fact]
    public async Task DrainAsync_WhenOneRowOfAGroupIsRefused_WritesTheRestOfTheGroup()
    {
        await using var harness = new Harness();
        harness.Saves.Fault = (_, rows) => rows.Any(r => r.Endpoint == "bad") ? Refusal("22001") : null;
        var writer = harness.Writer;
        writer.Enqueue(Entry(TenantA));
        writer.Enqueue(Entry(TenantA, "bad"));
        writer.Enqueue(Entry(TenantA));
        writer.Enqueue(Entry(TenantB));

        await harness.DrainAsync(writer);

        (await harness.CountAsync(TenantA)).Should().Be(2);
        (await harness.CountAsync(TenantB)).Should().Be(1);
        harness.Logger.Warnings.Should().ContainSingle().Which.Should().Contain("Failed to write 1 of 3 ");
    }

    [Theory]
    [InlineData("22001")]
    [InlineData("23502")]
    public async Task DrainAsync_WhenTheDatabaseRefusesTheData_RetriesEachEntry(string sqlState)
    {
        await using var harness = new Harness();
        harness.Saves.Fault = (_, rows) => rows.Any(r => r.Endpoint == "bad") ? Refusal(sqlState) : null;
        var writer = harness.Writer;
        writer.Enqueue(Entry(TenantA));
        writer.Enqueue(Entry(TenantA, "bad"));

        await harness.DrainAsync(writer);

        harness.Saves.Attempts.Should().Be(3);
        (await harness.CountAsync(TenantA)).Should().Be(1);
    }

    [Theory]
    [InlineData("08006")]
    [InlineData("53300")]
    [InlineData("57014")]
    public async Task DrainAsync_WhenTheFailureIsNotTheData_DoesNotRetryEachEntry(string sqlState)
    {
        await using var harness = new Harness();
        harness.Saves.Fault = (_, _) => Refusal(sqlState);
        var writer = harness.Writer;
        for (var i = 0; i < 5; i++)
            writer.Enqueue(Entry(TenantA));

        await harness.DrainAsync(writer);

        harness.Saves.Attempts.Should().Be(1);
        harness.Logger.Warnings.Should().ContainSingle().Which.Should().Contain("Failed to write 5 of 5 ");
    }

    [Fact]
    public async Task DrainAsync_WhenASaveThrowsCancellation_KeepsDrainingLaterEntries()
    {
        await using var harness = new Harness();
        harness.Saves.Fault = (_, rows) => rows.Any(r => r.Endpoint == "bad") ? new OperationCanceledException() : null;
        var writer = harness.Writer;
        var drain = writer.DrainAsync();

        writer.Enqueue(Entry(TenantA, "bad"));
        await WaitForAsync(() => harness.Logger.Warnings.Any());
        writer.Enqueue(Entry(TenantB));
        await WaitForAsync(() => harness.Saves.Batches.Any(b => b.PinnedTenant == TenantB));

        writer.Complete();
        await drain;
        (await harness.CountAsync(TenantB)).Should().Be(1);
    }

    [Fact]
    public async Task DrainAsync_CutsEachFreeTextColumnToItsLength()
    {
        await using var harness = new Harness();
        var writer = harness.Writer;
        var entry = Entry(TenantA, "GET /api/v4/" + new string('x', 400));
        entry.ActorName = new string('a', 400);
        entry.UserAgent = new string('u', 400);
        var splitPair = Entry(TenantA, new string('y', 199) + "😀");

        writer.Enqueue(entry);
        writer.Enqueue(splitPair);
        await harness.DrainAsync(writer);

        var saved = await harness.ReadAsync(TenantA);
        saved.Should().HaveCount(2);
        saved.Should().Contain(r => r.Id == entry.Id && r.Endpoint.Length == 200 && r.ActorName!.Length == 128);
        saved.Single(r => r.Id == entry.Id).UserAgent.Should().HaveLength(400, "a text column has no length to cut to");
        saved.Single(r => r.Id == splitPair.Id).Endpoint.Should().HaveLength(199, "a cut must not leave half a surrogate pair");
    }

    [Fact]
    public async Task StopAsync_WhenTheHostGivesUp_CountsAndLogsWhatIsStillQueued()
    {
        await using var harness = new Harness();
        var writer = harness.Writer;
        var gate = harness.Saves.BlockSaves();
        const int queued = 250;
        for (var i = 0; i < ReadAccessLogWriter.BatchSize + queued; i++)
            writer.Enqueue(Entry(TenantA));
        var service = new ReadAccessLogDrain(writer);
        await service.StartAsync(CancellationToken.None);
        await harness.Saves.SaveEntered;

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stop = () => service.StopAsync(timeout.Token);

        await stop.Should().NotThrowAsync();
        harness.Logger.Warnings.Should().ContainSingle().Which.Should().Contain($"Discarded {queued} ");
        gate.SetResult();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the drain should have reached this state");
            await Task.Delay(10);
        }
    }

    private static DbUpdateException Refusal(string sqlState) =>
        new("The database refused the write.", new PostgresException("refused", "ERROR", "ERROR", sqlState));

    private static ReadAccessLogEntity Entry(Guid tenantId, string endpoint = "GET /api/v4/entries") => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Endpoint = endpoint,
        CreatedAt = DateTime.UtcNow,
    };

    private sealed class Harness : IAsyncDisposable
    {
        private ReadAccessLogWriter? writer;

        public Harness()
        {
            var services = new ServiceCollection();
            var database = Guid.NewGuid().ToString();
            services.AddDbContextFactory<NocturneDbContext>(o => o.UseInMemoryDatabase(database).AddInterceptors(Saves));
            services.AddScoped<IAuditContext, AuditContext>();
            services.AddLogging();
            services.AddDataServices();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<ILogger<ReadAccessLogWriter>>(Logger);
            services.AddSingleton<ReadAccessLogWriter>();
            Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

        public ListLogger<ReadAccessLogWriter> Logger { get; } = new();

        public ServiceProvider Provider { get; }

        public SaveCapture Saves { get; } = new();

        public Guid? FailingTenant
        {
            init => Saves.Fault = (tenant, _) =>
                tenant == value ? new InvalidOperationException("The database refused this tenant's write.") : null;
        }

        public INocturneDbContextFactory Contexts => Provider.GetRequiredService<INocturneDbContextFactory>();

        public ReadAccessLogWriter Writer => writer ??= Provider.GetRequiredService<ReadAccessLogWriter>();

        public async Task DrainAsync(ReadAccessLogWriter target)
        {
            target.Complete();
            await target.DrainAsync();
        }

        public async Task<int> CountAsync(Guid tenantId)
        {
            await using var check = await Contexts.CreateForTenantAsync(tenantId);
            return await check.ReadAccessLog.CountAsync();
        }

        public async Task<List<ReadAccessLogEntity>> ReadAsync(Guid tenantId)
        {
            await using var check = await Contexts.CreateForTenantAsync(tenantId);
            return await check.ReadAccessLog.AsNoTracking().ToListAsync();
        }

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private sealed class SaveCapture : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource saveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? gate;

        private int attempts;

        public Func<Guid, IReadOnlyList<ReadAccessLogEntity>, Exception?>? Fault { get; set; }

        public int Attempts => Volatile.Read(ref attempts);

        public Task SaveEntered => saveEntered.Task;

        public TaskCompletionSource BlockSaves() =>
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<NocturneDbContext> Contexts { get; } = [];

        public List<(Guid PinnedTenant, List<ReadAccessLogEntity> Rows)> Batches { get; } = [];

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context is NocturneDbContext context)
            {
                Interlocked.Increment(ref attempts);
                var rows = context.ChangeTracker.Entries<ReadAccessLogEntity>().Select(e => e.Entity).ToList();
                if (Fault?.Invoke(context.TenantId, rows) is { } fault)
                    throw fault;

                if (gate is not null)
                {
                    saveEntered.TrySetResult();
                    await gate.Task;
                }

                lock (Batches)
                {
                    Contexts.Add(context);
                    Batches.Add((context.TenantId, rows));
                }
            }

            return await base.SavingChangesAsync(eventData, result, ct);
        }
    }
}
