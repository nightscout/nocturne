using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Models.Compatibility;
using Nocturne.API.Services.Compatibility;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Compatibility;

public class DiscrepancyPersistenceServiceTests
{
    private static readonly ResponseComparisonResult Comparison = new() { TraceId = "trace-1" };
    private static readonly CompatibilityProxyResponse ProxyResponse = new();

    private static Mock<IDiscrepancyAnalysisRepository> RepositoryReturning(Guid id)
    {
        var repository = new Mock<IDiscrepancyAnalysisRepository>();
        repository
            .Setup(r => r.StoreAnalysisAsync(
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<long?>(),
                It.IsAny<long?>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<List<DiscrepancyDetailData>>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(id);
        return repository;
    }

    private static DiscrepancyPersistenceService Create(
        IDiscrepancyAnalysisRepository repository,
        IDiscrepancyForwardingService forwarder) =>
        new(repository, forwarder, TimeProvider.System, NullLogger<DiscrepancyPersistenceService>.Instance);

    [Fact]
    public async Task CleanupOldDataAsync_DeletesAnalysesOlderThanTheRetentionWindowBeforeNow()
    {
        var now = new DateTimeOffset(2026, 10, 29, 3, 4, 5, TimeSpan.Zero);
        var repository = new Mock<IDiscrepancyAnalysisRepository>();
        repository
            .Setup(r => r.DeleteOldAnalysesAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        var service = new DiscrepancyPersistenceService(
            repository.Object,
            Mock.Of<IDiscrepancyForwardingService>(),
            new FakeTimeProvider(now),
            NullLogger<DiscrepancyPersistenceService>.Instance);

        var deleted = await service.CleanupOldDataAsync(retentionDays: 7);

        deleted.Should().Be(3);
        repository.Verify(r => r.DeleteOldAnalysesAsync(now.AddDays(-7), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StoreAnalysisAsync_HasForwardedByTheTimeItReturns()
    {
        var id = Guid.CreateVersion7();
        var forwarder = new Mock<IDiscrepancyForwardingService>();
        forwarder
            .Setup(f => f.ForwardDiscrepancyAsync(
                Comparison, "GET", "/api/v1/entries", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = Create(RepositoryReturning(id).Object, forwarder.Object);

        var result = await service.StoreAnalysisAsync(Comparison, ProxyResponse, "GET", "/api/v1/entries");

        result.Should().Be(id);
        forwarder.Verify(
            f => f.ForwardDiscrepancyAsync(
                Comparison, "GET", "/api/v1/entries", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StoreAnalysisAsync_PassesTheCallersCancellationTokenToTheForward()
    {
        using var cts = new CancellationTokenSource();
        var forwarder = new Mock<IDiscrepancyForwardingService>();
        forwarder
            .Setup(f => f.ForwardDiscrepancyAsync(
                It.IsAny<ResponseComparisonResult>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                cts.Token))
            .ReturnsAsync(true);
        var service = Create(RepositoryReturning(Guid.CreateVersion7()).Object, forwarder.Object);

        await service.StoreAnalysisAsync(Comparison, ProxyResponse, "GET", "/p", cts.Token);

        forwarder.VerifyAll();
    }

    [Fact]
    public async Task StoreAnalysisAsync_NeverUsesTheScopedForwarderAfterItsScopeIsDisposed()
    {
        var id = Guid.CreateVersion7();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposedWhenUsed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton(RepositoryReturning(id).Object);
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IDiscrepancyForwardingService>(_ =>
            new ScopeBoundForwarder(entered, gate.Task, disposedWhenUsed));
        services.AddScoped<DiscrepancyPersistenceService>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DiscrepancyPersistenceService>();
        var store = service.StoreAnalysisAsync(Comparison, ProxyResponse, "GET", "/p");
        await entered.Task;

        store.IsCompleted.Should().BeFalse();

        gate.SetResult();
        var result = await store;
        await scope.DisposeAsync();

        result.Should().Be(id);
        (await disposedWhenUsed.Task).Should().BeFalse();
    }

    private sealed class ScopeBoundForwarder(
        TaskCompletionSource entered,
        Task gate,
        TaskCompletionSource<bool> disposedWhenUsed)
        : IDiscrepancyForwardingService, IAsyncDisposable
    {
        private volatile bool _disposed;

        public async Task<bool> ForwardDiscrepancyAsync(
            ResponseComparisonResult analysis,
            string requestMethod,
            string requestPath,
            CancellationToken cancellationToken = default)
        {
            entered.SetResult();
            await gate;
            disposedWhenUsed.TrySetResult(_disposed);
            return true;
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
