using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// A tenant whose rewrite fails is logged and skipped: every tenant is still attempted, and the
/// run reports how many failed. A tenant whose pass completes is recorded and not walked again. The
/// InMemory provider has no connection to rewrite through, so the default rewrite fails for every
/// tenant here; the completion tests substitute the rewrite.
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
        var database = await SeedTenantsAsync();
        var attempted = new List<Guid>();
        var service = CreateService(database, attempted, out var logger);

        await RunAsync(service);

        attempted.Should().BeEquivalentTo(Tenants);
        logger.Entries.Where(e => e.Level == LogLevel.Error).Should().HaveCount(Tenants.Length);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information)
            .Which.Message.Should().EndWith($"{Tenants.Length} failed");
        (await CompletedAsync(database)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_completed_pass_is_recorded_once_and_the_tenant_is_not_walked_again()
    {
        var database = await SeedTenantsAsync();
        var walked = new List<Guid>();
        var rewrite = Succeeding(walked);

        await RunAsync(CreateService(database, [], out _, rewrite));
        var second = CreateService(database, [], out var logger, rewrite);
        await RunAsync(second);

        walked.Should().BeEquivalentTo(Tenants);
        var rows = await CompletedAsync(database);
        rows.Should().BeEquivalentTo(Tenants.Select(id => (ApsSnapshotCurvesBackfillService.BackfillName, id)));
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information)
            .Which.Message.Should().Contain($"{Tenants.Length} already complete");
    }

    [Fact]
    public async Task A_tenant_whose_pass_threw_is_walked_again_on_the_next_run()
    {
        var database = await SeedTenantsAsync();
        var walked = new List<Guid>();
        var failing = Tenants[0];
        var rewrite = (NocturneDbContext _, Guid tenantId, CancellationToken _) =>
        {
            walked.Add(tenantId);
            return tenantId == failing
                ? Task.FromException<int>(new InvalidOperationException("page failed"))
                : Task.FromResult(0);
        };

        await RunAsync(CreateService(database, [], out _, rewrite));
        walked.Clear();
        await RunAsync(CreateService(database, [], out _, Succeeding(walked)));

        walked.Should().BeEquivalentTo([failing]);
        (await CompletedAsync(database)).Select(r => r.TenantId).Should().BeEquivalentTo(Tenants);
    }

    private static Func<NocturneDbContext, Guid, CancellationToken, Task<int>> Succeeding(List<Guid> walked) =>
        (_, tenantId, _) =>
        {
            walked.Add(tenantId);
            return Task.FromResult(0);
        };

    private static async Task<string> SeedTenantsAsync()
    {
        var database = $"aps_curves_backfill_{Guid.NewGuid()}";
        await using var seed = TestDbContextFactory.CreateInMemoryContext(database);
        seed.Tenants.AddRange(Tenants.Select(id => new TenantEntity { Id = id, Slug = id.ToString("N"), DisplayName = "T" }));
        await seed.SaveChangesAsync();
        return database;
    }

    private static async Task<List<(string Backfill, Guid TenantId)>> CompletedAsync(string database)
    {
        await using var db = TestDbContextFactory.CreateInMemoryContext(database);
        var rows = await db.BackfillCompletions.AsNoTracking().ToListAsync();
        return rows.Select(r => (r.Backfill, r.TenantId)).ToList();
    }

    private static ApsSnapshotCurvesBackfillService CreateService(
        string database,
        List<Guid> attempted,
        out ListLogger<ApsSnapshotCurvesBackfillService> logger,
        Func<NocturneDbContext, Guid, CancellationToken, Task<int>>? rewrite = null)
    {
        var factory = new Mock<INocturneDbContextFactory>();
        factory
            .Setup(f => f.CreatePlatformAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(TestDbContextFactory.CreateInMemoryContext(database)));
        factory
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
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

        var provider = new ServiceCollection().AddSingleton(factory.Object).AddTransient<ITenantRepository, TenantRepository>().BuildServiceProvider();
        logger = new ListLogger<ApsSnapshotCurvesBackfillService>();
        return new ApsSnapshotCurvesBackfillService(provider, logger, TimeProvider.System, rewrite);
    }

    private static async Task RunAsync(ApsSnapshotCurvesBackfillService service)
    {
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
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
