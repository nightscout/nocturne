using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Migration;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Migration;

public class MigrationSourceHmacTests
{
    private const string KeyOne = "instance-key-one";
    private const string KeyTwo = "instance-key-two";
    private const string SiteUrl = "https://example.nightscout.org/secret-path";
    private const string ApiSecret = "my-super-secret";

    private static IConfiguration BuildConfig(string? instanceKey) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ServiceNames.ConfigKeys.InstanceKey] = instanceKey,
            })
            .Build();

    [Fact]
    public void Identifier_and_secret_hash_cannot_be_recomputed_without_instance_key()
    {
        var unsaltedUrlDigest = HashUtils.Sha256Hex("https://example.nightscout.org/secret-path");
        var unsaltedSecretDigest = HashUtils.Sha256Hex(ApiSecret);

        var idWithKey1 = MigrationJob.ApiSourceIdentifier(SiteUrl, KeyOne);
        var idWithKey2 = MigrationJob.ApiSourceIdentifier(SiteUrl, KeyTwo);
        var secretWithKey1 = MigrationJob.HashNightscoutApiSecret(ApiSecret, KeyOne);
        var secretWithKey2 = MigrationJob.HashNightscoutApiSecret(ApiSecret, KeyTwo);

        HashUtils.IsHmacSha256(idWithKey1).Should().BeTrue();
        HashUtils.IsHmacSha256(secretWithKey1).Should().BeTrue();

        idWithKey1.Should().NotBe(unsaltedUrlDigest);
        idWithKey1.Should().NotBe(idWithKey2);
        secretWithKey1.Should().NotBe(unsaltedSecretDigest);
        secretWithKey1.Should().NotBe(secretWithKey2);
    }

    [Fact]
    public async Task Reimporting_after_rewrite_attaches_to_existing_row_and_does_not_retrigger_notice()
    {
        var dbName = $"hmac-rewrite-{Guid.NewGuid():N}";
        var tenantId = Guid.CreateVersion7();
        var config = BuildConfig(KeyOne);

        var provider = new ServiceCollection()
            .AddDbContextFactory<NocturneDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddDataServices()
            .AddSingleton(AccountLinkRoutes.Off)
            .AddActiveTenantSnapshot()
            .AddSingleton(config)
            .BuildServiceProvider();

        var legacyIdentifier = HashUtils.Sha256Hex("https://example.nightscout.org/secret-path");
        var legacySecretHash = HashUtils.Sha256Hex(ApiSecret);
        var legacySourceId = Guid.CreateVersion7();

        await using (var db = await provider.GetRequiredService<INocturneDbContextFactory>().CreateAsync())
        {
            db.MigrationSources.Add(new MigrationSourceEntity
            {
                Id = legacySourceId,
                TenantId = tenantId,
                Mode = "Api",
                SourceIdentifier = legacyIdentifier,
                NightscoutUrl = "example.nightscout.org/secr…",
                NightscoutApiSecretHash = legacySecretHash,
                CreatedAt = DateTime.UtcNow,
            });
            db.MigrationRuns.Add(new MigrationRunEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                SourceId = legacySourceId,
                Mode = "Api",
                State = "Completed",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var startupService = new MigrationStartupService(
            provider,
            provider.GetRequiredService<ActiveTenantSnapshot>(),
            config,
            NullLogger<MigrationStartupService>.Instance);

        // Run rewrite twice to verify idempotency (never double-wraps)
        await startupService.RewriteLegacySourceHashesAsync(CancellationToken.None);
        await startupService.RewriteLegacySourceHashesAsync(CancellationToken.None);

        var expectedIdentifier = MigrationJob.ApiSourceIdentifier(SiteUrl, KeyOne);
        var expectedSecretHash = MigrationJob.HashNightscoutApiSecret(ApiSecret, KeyOne);

        await using (var db = await provider.GetRequiredService<INocturneDbContextFactory>().CreateAsync())
        {
            var rewritten = await db.MigrationSources.SingleAsync(s => s.Id == legacySourceId);
            rewritten.SourceIdentifier.Should().Be(expectedIdentifier);
            rewritten.NightscoutApiSecretHash.Should().Be(expectedSecretHash);
        }

        // Re-importing attaches to the existing source row rather than creating a duplicate
        var jobService = MigrationJobHarness.CreateService(provider, config);
        await jobService.StartMigrationAsync(
            new StartMigrationRequest
            {
                Mode = MigrationMode.Api,
                NightscoutUrl = SiteUrl,
                NightscoutApiSecret = ApiSecret,
            },
            new TenantContext(tenantId, "test", "Test", true, IsDemo: false));

        await using (var db = await provider.GetRequiredService<INocturneDbContextFactory>().CreateAsync())
        {
            var sources = await db.MigrationSources.Where(s => s.TenantId == tenantId).ToListAsync();
            sources.Should().ContainSingle(s => s.Id == legacySourceId);

            var hasCompleted = await db.MigrationRuns
                .AnyAsync(r => r.TenantId == tenantId
                            && r.Source.SourceIdentifier == expectedIdentifier
                            && r.State == "Completed");
            hasCompleted.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Rotating_instance_key_leaves_old_rows_orphaned_without_data_loss()
    {
        var dbName = $"hmac-rotate-{Guid.NewGuid():N}";
        var tenantId = Guid.CreateVersion7();
        var oldConfig = BuildConfig(KeyOne);
        var newConfig = BuildConfig(KeyTwo);

        var oldProvider = new ServiceCollection()
            .AddDbContextFactory<NocturneDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddDataServices()
            .AddSingleton(AccountLinkRoutes.Off)
            .AddActiveTenantSnapshot()
            .AddSingleton(oldConfig)
            .BuildServiceProvider();

        var oldJobService = MigrationJobHarness.CreateService(oldProvider, oldConfig);
        var oldJob = await oldJobService.StartMigrationAsync(
            new StartMigrationRequest { Mode = MigrationMode.Api, NightscoutUrl = SiteUrl },
            new TenantContext(tenantId, "test", "Test", true, IsDemo: false));
        await oldJobService.CancelAsync(tenantId, oldJob.Id);

        // Rotate key
        var newProvider = new ServiceCollection()
            .AddDbContextFactory<NocturneDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddDataServices()
            .AddSingleton(AccountLinkRoutes.Off)
            .AddActiveTenantSnapshot()
            .AddSingleton(newConfig)
            .BuildServiceProvider();

        var startupWithNewKey = new MigrationStartupService(
            newProvider,
            newProvider.GetRequiredService<ActiveTenantSnapshot>(),
            newConfig,
            NullLogger<MigrationStartupService>.Instance);

        await startupWithNewKey.RewriteLegacySourceHashesAsync(CancellationToken.None);

        var newJobService = MigrationJobHarness.CreateService(newProvider, newConfig);
        await newJobService.StartMigrationAsync(
            new StartMigrationRequest { Mode = MigrationMode.Api, NightscoutUrl = SiteUrl },
            new TenantContext(tenantId, "test", "Test", true, IsDemo: false));

        await using var db = await newProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
        var sources = await db.MigrationSources.Where(s => s.TenantId == tenantId).ToListAsync();
        sources.Should().HaveCount(2);
        sources.Select(s => s.SourceIdentifier).Should().Contain(
        [
            MigrationJob.ApiSourceIdentifier(SiteUrl, KeyOne),
            MigrationJob.ApiSourceIdentifier(SiteUrl, KeyTwo),
        ]);
    }
}
