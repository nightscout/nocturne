using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Services.Monitoring;

[Trait("Category", "Unit")]
public class TrackerAlertRuleBackfillServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"TrackerBackfill_{Guid.NewGuid():N}.db");
    private readonly Guid _tenantId = Guid.NewGuid();

    public void Dispose()
    {
        // SQLite pools the file handle past the last context's disposal.
        try { File.Delete(_dbPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The backfill's rule writes go through the interceptor, which derives attribution from the
    /// scope's audit context — a bare scope resolves a blank user context and records every
    /// synthesised, updated and orphaned managed rule as an actorless user mutation.
    /// </summary>
    [Fact]
    public async Task Backfill_SyncsUnderSystemAttribution()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        using (var seed = new NocturneDbContext(options) { TenantId = _tenantId })
        {
            seed.Database.EnsureCreated();
            seed.Tenants.Add(new TenantEntity
            {
                Id = _tenantId, Slug = "test", DisplayName = "Test", IsActive = true
            });
            var definitionId = Guid.CreateVersion7();
            seed.TrackerDefinitions.Add(new TrackerDefinitionEntity
            {
                Id = definitionId, TenantId = _tenantId, UserId = "dev", Name = "Cannula"
            });
            seed.TrackerNotificationThresholds.Add(new TrackerNotificationThresholdEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenantId,
                TrackerDefinitionId = definitionId,
                Hours = 24,
                AlertRuleId = null,
            });
            seed.SaveChanges();
        }

        var synced = new TaskCompletionSource();
        bool? ambientIsSystem = null;
        string? contextEndpoint = null;

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<NocturneDbContext>>(
            new StubDbContextFactory(options));
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext());
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddScoped<ITrackerAlertRuleSyncService>(sp =>
        {
            var ambient = sp.GetRequiredService<IAuditContext>();
            var dbContext = sp.GetRequiredService<NocturneDbContext>();
            var stub = new Mock<ITrackerAlertRuleSyncService>();
            stub.Setup(x => x.SyncDefinitionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    ambientIsSystem = ambient.IsSystem;
                    contextEndpoint = dbContext.AuditContext?.Endpoint;
                    synced.TrySetResult();
                    return Task.CompletedTask;
                });
            return stub.Object;
        });

        var sut = new TrackerAlertRuleBackfillService(
            services.BuildServiceProvider(),
            NullLogger<TrackerAlertRuleBackfillService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        // The body is thread-pool scheduled, so wait for the sync before stopping the host.
        await synced.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sut.StopAsync(CancellationToken.None);

        ambientIsSystem.Should().BeTrue(
            "the sync service stamps its factory-created contexts from the ambient audit context");
        contextEndpoint.Should().Be(TrackerAlertRuleBackfillService.AuditEndpoint);
    }

    private sealed class StubDbContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }
}
