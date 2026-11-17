using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Core.Models.Health;
using Xunit;

namespace Nocturne.API.Tests.Health.GoogleHealth;

public class GoogleHealthCoordinatorTests
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Operation_lock_makes_waiters_wait_and_zero_timeout_callers_refuse()
    {
        var coordinator = InMemoryGoogleHealthCoordinator.Create();
        var tenant = Guid.NewGuid();
        var held = await coordinator.AcquireAsync(tenant, default);
        Assert.NotNull(held);

        var waiter = coordinator.AcquireAsync(tenant, default);
        await Task.Delay(100);
        Assert.False(waiter.IsCompleted);
        Assert.Null(await coordinator.AcquireAsync(tenant, default, TimeSpan.Zero));
        Assert.False(await coordinator.QueueAsync(tenant, 1, default));
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.AcquireAsync(tenant, cancelled.Token));

        await held!.DisposeAsync();
        await using var next = await waiter.WaitAsync(Prompt);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task A_worker_claim_refuses_while_another_holds_it()
    {
        var coordinator = InMemoryGoogleHealthCoordinator.Create();
        var tenant = Guid.NewGuid();
        var first = await coordinator.ClaimWorkerAsync(tenant, default);
        Assert.NotNull(first);
        Assert.Null(await coordinator.ClaimWorkerAsync(tenant, default));
        await first!.DisposeAsync();
        await using var second = await coordinator.ClaimWorkerAsync(tenant, default);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task One_tenants_locks_never_block_another()
    {
        var coordinator = InMemoryGoogleHealthCoordinator.Create();
        var busy = Guid.NewGuid();
        var other = Guid.NewGuid();
        await using var operation = await coordinator.AcquireAsync(busy, default);
        await using var claim = await coordinator.ClaimWorkerAsync(busy, default);
        await using var reader = await coordinator.WatchReadsAsync(busy, default);
        await coordinator.RequestDisconnectAsync(busy, Guid.NewGuid(), default);

        await using (var unrelated = await coordinator.AcquireAsync(other, default, TimeSpan.Zero))
            Assert.NotNull(unrelated);
        await using (var unrelatedClaim = await coordinator.ClaimWorkerAsync(other, default))
            Assert.NotNull(unrelatedClaim);
        await using var unrelatedReader = await coordinator.WatchReadsAsync(other, default);
        Assert.False(unrelatedReader.Token.IsCancellationRequested);
        Assert.True(await coordinator.QueueAsync(other, 1, default));
    }

    [Fact]
    public async Task Disconnect_interrupts_running_reads_and_keeps_one_request_until_finished()
    {
        var coordinator = InMemoryGoogleHealthCoordinator.Create();
        var tenant = Guid.NewGuid();
        var subject = Guid.NewGuid();
        await using var running = await coordinator.WatchReadsAsync(tenant, default);
        Assert.False(running.Token.IsCancellationRequested);

        var request = await coordinator.RequestDisconnectAsync(tenant, subject, default);
        await Task.Delay(Timeout.Infinite, running.Token)
            .ContinueWith(task => Assert.True(task.IsCanceled)).WaitAsync(Prompt);
        Assert.Equal(request, await coordinator.RequestDisconnectAsync(tenant, Guid.NewGuid(), default));
        Assert.Equal(subject, (await coordinator.DisconnectRequestAsync(tenant, default))!.SubjectId);
        await using (var late = await coordinator.WatchReadsAsync(tenant, default))
            Assert.True(late.Token.IsCancellationRequested);
        Assert.False(await coordinator.QueueAsync(tenant, 1, default));

        await coordinator.FinishDisconnectAsync(tenant, Guid.NewGuid());
        Assert.Equal(request, (await coordinator.DisconnectRequestAsync(tenant, default))!.RequestId);
        await coordinator.FinishDisconnectAsync(tenant, request);
        Assert.Null(await coordinator.DisconnectRequestAsync(tenant, default));
        await using (var after = await coordinator.WatchReadsAsync(tenant, default))
            Assert.False(after.Token.IsCancellationRequested);
        Assert.NotEqual(request, await coordinator.RequestDisconnectAsync(tenant, subject, default));
    }

    [Fact]
    public async Task Queued_imports_and_disconnects_wake_the_worker()
    {
        var coordinator = InMemoryGoogleHealthCoordinator.Create();
        var queued = Guid.NewGuid();
        var disconnecting = Guid.NewGuid();
        using var timeout = new CancellationTokenSource(Prompt);
        await using var requests = coordinator.ReadRequestsAsync(timeout.Token).GetAsyncEnumerator();
        var next = requests.MoveNextAsync().AsTask();
        await Task.Delay(50);
        Assert.False(next.IsCompleted);

        Assert.True(await coordinator.QueueAsync(queued, 1, default));
        Assert.True(await next);
        Assert.Equal(queued, requests.Current);
        await coordinator.RequestDisconnectAsync(disconnecting, Guid.NewGuid(), default);
        Assert.True(await requests.MoveNextAsync());
        Assert.Equal(disconnecting, requests.Current);
    }

    [Fact]
    public async Task Scheduled_progress_is_active_only_while_its_run_holds_the_operation_lock()
    {
        var coordinator = InMemoryGoogleHealthCoordinator.Create();
        var tenant = Guid.NewGuid();
        await using (await coordinator.AcquireAsync(tenant, default))
        {
            await coordinator.ReportAsync(tenant, GoogleHealthSyncPhase.Reading, "steps", 1, 4, 3);
            Assert.Equal(3, (await coordinator.ProgressAsync(tenant, default))!.PagesRead);
        }

        Assert.Null(await coordinator.ProgressAsync(tenant, default));
        Assert.True(await coordinator.QueueAsync(tenant, 4, default));
        Assert.True((await coordinator.ProgressAsync(tenant, default))!.WorkerOwned);
    }
}
