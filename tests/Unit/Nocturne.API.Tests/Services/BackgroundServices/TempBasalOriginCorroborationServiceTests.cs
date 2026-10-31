using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// A tenant whose corroboration fails is logged and skipped: every tenant is still attempted, and
/// the run reports how many failed. The InMemory provider has no connection to read through, so
/// every tenant fails here.
/// </summary>
[Trait("Category", "Unit")]
public class TempBasalOriginCorroborationServiceTests
{
    private static readonly Guid[] Tenants =
    [
        Guid.Parse("a1111111-1111-7111-8111-111111111111"),
        Guid.Parse("b2222222-2222-7222-8222-222222222222"),
    ];

    [Fact]
    public async Task A_tenant_that_fails_is_logged_and_the_rest_are_still_attempted()
    {
        var database = $"temp_basal_origin_{Guid.NewGuid()}";
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
        var logger = new ListLogger<TempBasalOriginCorroborationService>();
        var service = new TempBasalOriginCorroborationService(provider, TimeProvider.System, logger);

        await service.RunAsync(CancellationToken.None);

        attempted.Should().BeEquivalentTo(Tenants);
        logger.Entries.Where(e => e.Level == LogLevel.Error).Should().HaveCount(Tenants.Length);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information)
            .Which.Message.Should().EndWith($"{Tenants.Length} failed");
    }

    [Fact]
    public void A_tenant_with_no_run_yet_looks_back_the_restart_lookback()
    {
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

        new SnapshotWatermarks().Since(Tenants[0], now).Should().Be(now - SnapshotWatermarks.RestartLookback);
    }

    [Fact]
    public void A_run_that_read_snapshots_moves_only_its_own_tenants_watermark_to_a_commit_margin_before_its_last_write()
    {
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        var through = now.AddMinutes(-3);
        var watermarks = new SnapshotWatermarks();

        watermarks.Advance(Tenants[0], through);

        watermarks.Since(Tenants[0], now).Should().Be(through - TempBasalOriginCorroborator.CommitMargin);
        watermarks.Since(Tenants[1], now).Should().Be(now - SnapshotWatermarks.RestartLookback);
    }

    [Fact]
    public void A_run_that_read_no_snapshots_keeps_the_watermark()
    {
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        var through = now.AddMinutes(-3);
        var watermarks = new SnapshotWatermarks();
        watermarks.Advance(Tenants[0], through);

        watermarks.Advance(Tenants[0], null);

        watermarks.Since(Tenants[0], now).Should().Be(through - TempBasalOriginCorroborator.CommitMargin);
    }
}
