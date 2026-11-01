using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Monitoring;

[Trait("Category", "Unit")]
public sealed class TrackerAlertRuleBackfillJobTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"TrackerBackfill_{Guid.NewGuid():N}.db");
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly DbContextOptions<NocturneDbContext> _options;

    public TrackerAlertRuleBackfillJobTests()
    {
        _options = new DbContextOptionsBuilder<NocturneDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
    }

    public void Dispose()
    {
        // SQLite pools the file handle past the last context's disposal.
        try { File.Delete(_dbPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task It_syncs_each_definition_with_an_unlinked_threshold_and_comes_due_a_day_later()
    {
        var unlinked = Seed();
        var sync = new Mock<ITrackerAlertRuleSyncService>();

        var outcome = await RunAsync(sync.Object);

        sync.Verify(s => s.SyncDefinitionAsync(unlinked, It.IsAny<CancellationToken>()), Times.Once);
        sync.VerifyNoOtherCalls();
        outcome.Should().Be(JobOutcome.Resume(null, Now.AddDays(1)));
    }

    [Fact]
    public async Task A_definition_that_fails_leaves_the_rest_synced_and_fails_the_run()
    {
        var first = Seed();
        var second = SeedDefinition();
        var sync = new Mock<ITrackerAlertRuleSyncService>();
        sync.Setup(s => s.SyncDefinitionAsync(first, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rule write failed"));

        var act = () => RunAsync(sync.Object);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("rule write failed");
        sync.Verify(s => s.SyncDefinitionAsync(second, It.IsAny<CancellationToken>()), Times.Once);
    }

    private async Task<JobOutcome> RunAsync(ITrackerAlertRuleSyncService sync)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<NocturneDbContext>>(new Contexts(_options));
        services.AddSingleton(sp => sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>().AsNocturneFactory(_tenantId));
        services.AddSingleton(sync);
        await using var provider = services.BuildServiceProvider();

        var job = new TrackerAlertRuleBackfillJob(NullLogger<TrackerAlertRuleBackfillJob>.Instance);
        return await job.RunAsync(
            new JobRun(new TenantContext(_tenantId, "t", "T", true, IsDemo: false), null, 0, provider, Now),
            CancellationToken.None);
    }

    private Guid Seed()
    {
        using (var seed = new NocturneDbContext(_options) { TenantId = _tenantId })
        {
            seed.Database.EnsureCreated();
            seed.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "test", DisplayName = "Test", IsActive = true });
            seed.SaveChanges();
        }

        return SeedDefinition();
    }

    private Guid SeedDefinition()
    {
        using var seed = new NocturneDbContext(_options) { TenantId = _tenantId };
        var definitionId = Guid.CreateVersion7();
        seed.TrackerDefinitions.Add(new TrackerDefinitionEntity
        {
            Id = definitionId, TenantId = _tenantId, CreatedBy = "dev", Name = "Cannula",
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
        return definitionId;
    }

    private sealed class Contexts(DbContextOptions<NocturneDbContext> options) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }
}
