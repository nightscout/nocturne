using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// A tenant whose rewrite fails is logged and skipped: every tenant is still attempted, and the
/// run reports how many failed. The InMemory provider has no connection to rewrite through, so
/// every tenant's rewrite fails here.
/// </summary>
[Trait("Category", "Unit")]
public class ApsSnapshotCurvesBackfillServiceTests
{
    private static readonly Guid[] Tenants =
    [
        Guid.Parse("a1111111-1111-7111-8111-111111111111"),
        Guid.Parse("b2222222-2222-7222-8222-222222222222"),
    ];

    [Fact]
    public async Task A_tenant_that_fails_is_logged_and_the_rest_are_still_attempted()
    {
        var database = $"aps_curves_backfill_{Guid.NewGuid()}";
        await using (var seed = TestDbContextFactory.CreateInMemoryContext(database))
        {
            seed.Tenants.AddRange(Tenants.Select(id => new TenantEntity { Id = id, Slug = id.ToString("N"), DisplayName = "T" }));
            await seed.SaveChangesAsync();
        }

        var attempted = new List<Guid>();
        var factory = new Mock<INocturneDbContextFactory>();
        factory
            .Setup(f => f.CreatePlatformAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(TestDbContextFactory.CreateInMemoryContext(database)));
        factory
            .Setup(f => f.CreateForTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid tenantId, CancellationToken _) =>
            {
                attempted.Add(tenantId);
                NocturneDbContext context = TestDbContextFactory.CreateInMemoryContext(database);
                context.TenantId = tenantId;
                return ValueTask.FromResult(context);
            });

        var provider = new ServiceCollection().AddSingleton(factory.Object).BuildServiceProvider();
        var logger = new ListLogger<ApsSnapshotCurvesBackfillService>();
        var service = new ApsSnapshotCurvesBackfillService(provider, logger);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        attempted.Should().BeEquivalentTo(Tenants);
        logger.Entries.Where(e => e.Level == LogLevel.Error).Should().HaveCount(Tenants.Length);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information)
            .Which.Message.Should().EndWith($"{Tenants.Length} failed");
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
