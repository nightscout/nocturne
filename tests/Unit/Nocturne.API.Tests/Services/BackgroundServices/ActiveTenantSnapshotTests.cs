using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public sealed class ActiveTenantSnapshotTests : IDisposable
{
    private const int PollerCount = 13;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly QueryGate _gate = new();
    private readonly SqliteTestDatabase _db;
    private readonly FakeTimeProvider _time = new();
    private readonly ListLogger<ActiveTenantSnapshot> _logger = new();
    private readonly ActiveTenantSnapshot _sut;

    public ActiveTenantSnapshotTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(Guid.NewGuid(), "active", _gate)
            .SeedTenant(Guid.NewGuid(), "inactive", isActive: false);

        _sut = new ActiveTenantSnapshot(_db.ContextFactory, _time, _logger);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task EveryPollerReadingAtOnce_SharesOneQuery()
    {
        _gate.Hold();

        var reads = Enumerable.Range(0, PollerCount)
            .Select(_ => _sut.GetAsync(CancellationToken.None))
            .ToList();

        await _gate.QueryStarted(1).WaitAsync(Patience);
        _gate.Release();
        var results = await Task.WhenAll(reads);

        _gate.Queries.Should().Be(1);
        results.Should().AllSatisfy(tenants =>
            tenants.Select(t => t.Slug).Should().Equal("active"));
    }

    [Fact]
    public async Task ReadsWithinTheTtl_DoNotQueryAgain()
    {
        await _sut.GetAsync(CancellationToken.None);
        _db.SeedTenant(Guid.NewGuid(), "created-later");

        _time.Advance(ActiveTenantSnapshot.Ttl - TimeSpan.FromSeconds(1));
        var tenants = await _sut.GetAsync(CancellationToken.None);

        _gate.Queries.Should().Be(1);
        tenants.Select(t => t.Slug).Should().Equal("active");
    }

    [Fact]
    public async Task AReadPastTheTtl_ReturnsTheLastListAtOnce_WhileOneRefreshRunsBehindIt()
    {
        await _sut.GetAsync(CancellationToken.None);
        _db.SeedTenant(Guid.NewGuid(), "created-later");
        _gate.Hold();
        _time.Advance(ActiveTenantSnapshot.Ttl);

        var first = _sut.GetAsync(CancellationToken.None);
        await _gate.QueryStarted(2).WaitAsync(Patience);
        var duringRefresh = _sut.GetAsync(CancellationToken.None);

        first.IsCompletedSuccessfully.Should().BeTrue();
        duringRefresh.IsCompletedSuccessfully.Should().BeTrue();
        (await first).Select(t => t.Slug).Should().Equal("active");
        (await duringRefresh).Select(t => t.Slug).Should().Equal("active");

        _gate.Release();
        var refreshed = await ReadUntilAsync(tenants => tenants.Count == 2);

        refreshed.Select(t => t.Slug).Should().BeEquivalentTo("active", "created-later");
        _gate.Queries.Should().Be(2);
    }

    [Fact]
    public async Task AFailedRefresh_KeepsTheLastGoodListForAnotherTtl()
    {
        await _sut.GetAsync(CancellationToken.None);
        _gate.FailWith = new InvalidOperationException("database unavailable");

        _time.Advance(ActiveTenantSnapshot.Ttl);
        var served = await _sut.GetAsync(CancellationToken.None);
        await WaitUntilAsync(() => _logger.Entries.Count > 0);

        served.Select(t => t.Slug).Should().Equal("active");
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(_gate.FailWith);

        _time.Advance(ActiveTenantSnapshot.Ttl - TimeSpan.FromSeconds(1));
        (await _sut.GetAsync(CancellationToken.None)).Select(t => t.Slug).Should().Equal("active");
        _gate.Queries.Should().Be(2);

        _gate.FailWith = null;
        _db.SeedTenant(Guid.NewGuid(), "created-later");
        _time.Advance(TimeSpan.FromSeconds(1));
        var recovered = await ReadUntilAsync(tenants => tenants.Count == 2);

        recovered.Select(t => t.Slug).Should().BeEquivalentTo("active", "created-later");
        _gate.Queries.Should().Be(3);
    }

    [Fact]
    public async Task AFailedFirstRead_Throws_AndTheNextReadQueriesAgain()
    {
        _gate.FailWith = new InvalidOperationException("database unavailable");

        var first = () => _sut.GetAsync(CancellationToken.None);
        await first.Should().ThrowAsync<InvalidOperationException>();

        _gate.FailWith = null;
        var tenants = await _sut.GetAsync(CancellationToken.None);

        _gate.Queries.Should().Be(2);
        tenants.Select(t => t.Slug).Should().Equal("active");
    }

    [Fact]
    public async Task OneCallerCancelling_LeavesTheSharedRefreshAndTheOtherCallersIntact()
    {
        _gate.Hold();
        using var cancelled = new CancellationTokenSource();

        var abandoned = _sut.GetAsync(cancelled.Token);
        var waiting = _sut.GetAsync(CancellationToken.None);
        await _gate.QueryStarted(1).WaitAsync(Patience);

        await cancelled.CancelAsync();
        var abandon = () => abandoned;
        await abandon.Should().ThrowAsync<OperationCanceledException>();
        waiting.IsCompleted.Should().BeFalse();

        _gate.Release();
        (await waiting.WaitAsync(Patience)).Select(t => t.Slug).Should().Equal("active");
        (await _sut.GetAsync(CancellationToken.None)).Select(t => t.Slug).Should().Equal("active");
        _gate.Queries.Should().Be(1);
    }

    private async Task<IReadOnlyList<ActiveTenant>> ReadUntilAsync(Func<IReadOnlyList<ActiveTenant>, bool> done)
    {
        IReadOnlyList<ActiveTenant> tenants = [];
        await WaitUntilAsync(async () => done(tenants = await _sut.GetAsync(CancellationToken.None)));
        return tenants;
    }

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not hold in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>Counts the queries the snapshot sends, and can hold them in flight or fail them.</summary>
    private sealed class QueryGate : DbCommandInterceptor
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _started = new();
        private TaskCompletionSource _open = Opened();
        private int _queries;

        public int Queries => Volatile.Read(ref _queries);

        public Exception? FailWith { get; set; }

        public Task QueryStarted(int ordinal) => Started(ordinal).Task;

        public void Hold() => _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _open.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Started(Interlocked.Increment(ref _queries)).TrySetResult();
            await _open.Task;

            if (FailWith is { } failure)
                throw failure;

            return result;
        }

        private TaskCompletionSource Started(int ordinal) =>
            _started.GetOrAdd(ordinal, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private static TaskCompletionSource Opened()
        {
            var source = new TaskCompletionSource();
            source.SetResult();
            return source;
        }
    }
}
