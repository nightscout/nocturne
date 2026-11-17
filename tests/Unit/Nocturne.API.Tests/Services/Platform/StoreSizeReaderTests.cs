using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Platform;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform;

public class StoreSizeReaderTests
{
    [Theory]
    [InlineData(25, 100, 0.25)]
    [InlineData(0, 100, 0.0)]
    [InlineData(100, 100, 1.0)]
    [InlineData(150, 100, 1.0)]
    [InlineData(10, 0, 1.0)]
    [InlineData(0, 0, 1.0)]
    [InlineData(10, -1, 1.0)]
    public void ShareOf_AttributesTheWholeRelationWhenTheTotalCannotBeTrusted(
        long tenantRows, double reltuples, double expected)
    {
        StoreSizeReader.ShareOf(tenantRows, reltuples).Should().Be(expected);
    }

    private static ITenantAccessor Tenant(Guid? id = null) =>
        Mock.Of<ITenantAccessor>(t => t.TenantId == (id ?? TestTenantCache.DefaultTenantId));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReadAsync_GivesAShareOrClampedReaderZero_WithoutTouchingTheCacheOrTheDatabase(
        bool isShare, bool isClamped)
    {
        var cache = new Mock<ITenantCache>(MockBehavior.Strict);
        var contexts = new Mock<INocturneDbContextFactory>(MockBehavior.Strict);
        var readContext = new Mock<ICategoryReadContext>();
        readContext.SetupGet(x => x.IsShare).Returns(isShare);
        var partition = new Mock<IReaderCachePartition>();
        partition.SetupGet(x => x.Current).Returns(isClamped ? null : "full");
        var reader = new StoreSizeReader(
            contexts.Object, cache.Object, readContext.Object, Tenant(), partition.Object, NullLogger<StoreSizeReader>.Instance);

        var stats = await reader.ReadAsync();

        stats.Should().BeEquivalentTo(new DbStats());
        cache.VerifyNoOtherCalls();
        contexts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadAsync_ServesAnUnclampedReaderThroughTheTenantCache()
    {
        var cached = new DbStats { DataSize = 10, IndexSize = 5 };
        var cache = new Mock<ITenantCache>();
        cache
            .Setup(x => x.GetOrSetAsync(It.IsAny<string>(), It.IsAny<Func<Task<DbStats>>>(), TimeSpan.FromMinutes(10), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached);
        var partition = new Mock<IReaderCachePartition>();
        partition.SetupGet(x => x.Current).Returns("full");
        var reader = new StoreSizeReader(
            Mock.Of<INocturneDbContextFactory>(), cache.Object, Mock.Of<ICategoryReadContext>(), Tenant(), partition.Object, NullLogger<StoreSizeReader>.Instance);

        (await reader.ReadAsync()).Should().BeSameAs(cached);
    }

    private static (StoreSizeReader Reader, Mock<INocturneDbContextFactory> Contexts) FailingReader()
    {
        var contexts = new Mock<INocturneDbContextFactory>();
        contexts
            .Setup(x => x.CreateForTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));
        var partition = new Mock<IReaderCachePartition>();
        partition.SetupGet(x => x.Current).Returns("full");
        var reader = new StoreSizeReader(
            contexts.Object, TestTenantCache.Create(), Mock.Of<ICategoryReadContext>(), Tenant(), partition.Object, NullLogger<StoreSizeReader>.Instance);
        return (reader, contexts);
    }

    [Fact]
    public async Task ReadAsync_ReportsZeroAfterAFailureAndDoesNotRequeryWithinTheWindow()
    {
        var (reader, contexts) = FailingReader();

        (await reader.ReadAsync()).Should().BeEquivalentTo(new DbStats());
        (await reader.ReadAsync()).Should().BeEquivalentTo(new DbStats());

        contexts.Verify(x => x.CreateForTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReadAsync_CachesASuccessfulReadForTheFullTtl()
    {
        var cache = new Mock<ITenantCache>();
        var partition = new Mock<IReaderCachePartition>();
        partition.SetupGet(x => x.Current).Returns("full");
        var reader = new StoreSizeReader(
            Mock.Of<INocturneDbContextFactory>(), cache.Object, Mock.Of<ICategoryReadContext>(), Tenant(), partition.Object, NullLogger<StoreSizeReader>.Instance);

        await reader.ReadAsync();

        cache.Verify(x => x.GetOrSetAsync(It.IsAny<string>(), It.IsAny<Func<Task<DbStats>>>(), TimeSpan.FromMinutes(10), It.IsAny<CancellationToken>()), Times.Once);
        cache.Verify(x => x.SetLocal(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    private static StoreSizeReader ReaderOver(
        INocturneDbContextFactory contexts, ITenantAccessor tenant, ITenantCache? cache = null)
    {
        var partition = new Mock<IReaderCachePartition>();
        partition.SetupGet(x => x.Current).Returns("full");
        return new StoreSizeReader(
            contexts, cache ?? TestTenantCache.Create(tenantId: tenant.TenantId), Mock.Of<ICategoryReadContext>(), tenant, partition.Object,
            NullLogger<StoreSizeReader>.Instance);
    }

    private static Mock<INocturneDbContextFactory> BlockedContexts(
        TaskCompletionSource release, Action onOpen, ConcurrentQueue<CancellationToken>? tokens = null)
    {
        var contexts = new Mock<INocturneDbContextFactory>();
        contexts
            .Setup(x => x.CreateForTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, CancellationToken ct) =>
            {
                tokens?.Enqueue(ct);
                onOpen();
                await release.Task;
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("database down");
            });
        return contexts;
    }

    [Fact]
    public async Task ReadAsync_RunsTheCountOnceForConcurrentColdReadsOfOneTenant()
    {
        var opened = 0;
        var release = new TaskCompletionSource();
        var contexts = BlockedContexts(release, () => Interlocked.Increment(ref opened));
        var tenant = Tenant(Guid.NewGuid());
        var cache = TestTenantCache.Create(tenantId: tenant.TenantId);

        var reads = Enumerable.Range(0, 8).Select(_ => ReaderOver(contexts.Object, tenant, cache).ReadAsync()).ToList();
        release.SetResult();
        await Task.WhenAll(reads);

        opened.Should().Be(1);
    }

    [Fact]
    public async Task ReadAsync_RunsTheCountWithTheCallersOwnTokenAndAnotherCallerRetriesWhenItCancels()
    {
        var release = new TaskCompletionSource();
        var opened = 0;
        var tokens = new ConcurrentQueue<CancellationToken>();
        var contexts = BlockedContexts(release, () => Interlocked.Increment(ref opened), tokens);
        var tenant = Tenant(Guid.NewGuid());
        var cache = TestTenantCache.Create(tenantId: tenant.TenantId);
        using var cts = new CancellationTokenSource();

        var cancelled = ReaderOver(contexts.Object, tenant, cache).ReadAsync(cts.Token);
        var other = ReaderOver(contexts.Object, tenant, cache).ReadAsync();
        opened.Should().Be(1);
        cts.Cancel();
        release.SetResult();

        var waiting = () => cancelled;
        await waiting.Should().ThrowAsync<OperationCanceledException>();
        (await other).Should().BeEquivalentTo(new DbStats());
        tokens.Should().Equal(cts.Token, CancellationToken.None);
    }

    [Fact]
    public async Task ReadAsync_DoesNotCacheAFailureAsAFullTtlValue()
    {
        var cache = new Mock<ITenantCache>();
        cache
            .Setup(x => x.GetOrSetAsync(It.IsAny<string>(), It.IsAny<Func<Task<DbStats>>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((string _, Func<Task<DbStats>> factory, TimeSpan _, CancellationToken _) => factory());
        var contexts = new Mock<INocturneDbContextFactory>();
        contexts
            .Setup(x => x.CreateForTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));

        await ReaderOver(contexts.Object, Tenant(Guid.NewGuid()), cache.Object).ReadAsync();

        cache.Verify(x => x.SetLocal(It.IsAny<string>(), true, TimeSpan.FromMinutes(1)), Times.Once);
    }

    [Fact]
    public async Task ReadAsync_ServesAWarmHitWithoutOpeningAContext()
    {
        var tenant = Tenant(Guid.NewGuid());
        var cache = TestTenantCache.Create(tenantId: tenant.TenantId);
        await cache.GetOrSetAsync("dbstats:size", () => Task.FromResult(new DbStats { DataSize = 7 }), TimeSpan.FromMinutes(10));
        var contexts = new Mock<INocturneDbContextFactory>(MockBehavior.Strict);

        var stats = await ReaderOver(contexts.Object, tenant, cache).ReadAsync();

        stats.DataSize.Should().Be(7);
        contexts.VerifyNoOtherCalls();
    }
}
