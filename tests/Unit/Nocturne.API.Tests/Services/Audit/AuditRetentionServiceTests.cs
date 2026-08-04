using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services.Audit;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Services.Audit;

public class AuditRetentionServiceTests
{
    private readonly Mock<IDbContextFactory<NocturneDbContext>> _contextFactory = new();
    private readonly Mock<ILogger<AuditRetentionService>> _logger = new();

    /// <summary>
    /// The tests that predate the platform default assert an exact factory call count, so they
    /// disable the default (0) and exercise only the per-tenant config path.
    /// </summary>
    private AuditRetentionService CreateService() => CreateService(new Dictionary<string, string?>
    {
        ["Audit:DefaultReadAuditRetentionDays"] = "0",
        ["Audit:DefaultMutationRetentionDays"] = "0"
    });

    private AuditRetentionService CreateService(Dictionary<string, string?> settings) =>
        new(_contextFactory.Object,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            _logger.Object);

    private static NocturneDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new NocturneDbContext(options);
    }

    [Fact]
    public async Task PurgeExpiredRecordsAsync_NoConfiguredRetention_DeletesNothing()
    {
        // Arrange: empty TenantAuditConfig table (no retention configured)
        var configContext = CreateInMemoryContext();
        _contextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(configContext);

        var service = CreateService();

        // Act
        await service.PurgeExpiredRecordsAsync(CancellationToken.None);

        // Assert: factory called once for the config query, no further calls for deletion
        _contextFactory.Verify(
            f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PurgeExpiredRecordsAsync_ReadAuditRetention_AttemptsDeleteOnCorrectTable()
    {
        // Arrange: seed config with read retention only
        var tenantId = Guid.CreateVersion7();
        var configContext = CreateInMemoryContext();
        configContext.TenantAuditConfig.Add(new TenantAuditConfigEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ReadAuditRetentionDays = 90,
            MutationAuditRetentionDays = null,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow
        });
        await configContext.SaveChangesAsync();

        // InMemory doesn't support raw SQL, so the deletion attempt will throw.
        // The service catches per-tenant exceptions and continues.
        var callCount = 0;
        _contextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1 ? configContext : CreateInMemoryContext();
            });

        var service = CreateService();

        // Act: should not throw (per-tenant exceptions are caught)
        await service.PurgeExpiredRecordsAsync(CancellationToken.None);

        // Assert: factory called more than once (config query + deletion attempt)
        _contextFactory.Verify(
            f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2)); // 1 config + 1 deletion context for read_access_log

        // Assert: per-tenant warning logged (InMemory doesn't support raw SQL)
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task PurgeExpiredRecordsAsync_MutationAuditRetention_AttemptsDeleteOnCorrectTable()
    {
        // Arrange: seed config with mutation retention only
        var tenantId = Guid.CreateVersion7();
        var configContext = CreateInMemoryContext();
        configContext.TenantAuditConfig.Add(new TenantAuditConfigEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ReadAuditRetentionDays = null,
            MutationAuditRetentionDays = 30,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow
        });
        await configContext.SaveChangesAsync();

        var callCount = 0;
        _contextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1 ? configContext : CreateInMemoryContext();
            });

        var service = CreateService();

        // Act
        await service.PurgeExpiredRecordsAsync(CancellationToken.None);

        // Assert: factory called exactly twice (config + mutation deletion attempt)
        _contextFactory.Verify(
            f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2)); // 1 config + 1 deletion context for mutation_audit_log

        // Assert: per-tenant warning logged
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task PurgeExpiredRecordsAsync_BothRetentionConfigured_AttemptsBothTables()
    {
        // Arrange: config with both read and mutation retention
        var tenantId = Guid.CreateVersion7();
        var configContext = CreateInMemoryContext();
        configContext.TenantAuditConfig.Add(new TenantAuditConfigEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ReadAuditRetentionDays = 90,
            MutationAuditRetentionDays = 30,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow
        });
        await configContext.SaveChangesAsync();

        var callCount = 0;
        _contextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1 ? configContext : CreateInMemoryContext();
            });

        var service = CreateService();

        // Act
        await service.PurgeExpiredRecordsAsync(CancellationToken.None);

        // Assert: factory called for config + read attempt (which fails, so mutation is not reached
        // within the same tenant try block -- the per-tenant catch fires on the first failure)
        _contextFactory.Verify(
            f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
    }

    [Fact]
    public async Task PurgeExpiredRecordsAsync_DbException_LogsWarningAndContinues()
    {
        // Arrange: seed config so purge is attempted
        var tenantId = Guid.CreateVersion7();
        var configContext = CreateInMemoryContext();
        configContext.TenantAuditConfig.Add(new TenantAuditConfigEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ReadAuditRetentionDays = 90,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow
        });
        await configContext.SaveChangesAsync();

        // Second context factory call throws immediately
        var callCount = 0;
        _contextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) return configContext;
                throw new InvalidOperationException("Database connection failed");
            });

        var service = CreateService();

        // Act: should not throw
        var act = () => service.PurgeExpiredRecordsAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Assert: warning was logged for the failed tenant
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Records the purges the service would issue instead of running raw SQL, so the effective
    /// retention window per tenant/table is observable.
    /// </summary>
    private sealed class RecordingAuditRetentionService(
        IDbContextFactory<NocturneDbContext> contextFactory,
        IConfiguration configuration,
        ILogger<AuditRetentionService> logger)
        : AuditRetentionService(contextFactory, configuration, logger)
    {
        public List<(Guid TenantId, string Table, DateTime Cutoff)> Purges { get; } = [];

        internal override Task<int> PurgeBatchedAsync(
            Guid tenantId, string table, DateTime cutoff, CancellationToken ct)
        {
            Purges.Add((tenantId, table, cutoff));
            return Task.FromResult(0);
        }
    }

    private RecordingAuditRetentionService CreateRecordingService(
        Dictionary<string, string?>? settings = null) =>
        new(_contextFactory.Object,
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build(),
            _logger.Object);

    [Fact]
    public async Task PurgeExpiredRecordsAsync_UnconfiguredTenant_PurgesAtPlatformDefault()
    {
        var tenantId = Guid.CreateVersion7();
        var configContext = CreateInMemoryContext();
        configContext.Tenants.Add(new TenantEntity { Id = tenantId, Slug = "unconfigured" });
        await configContext.SaveChangesAsync();

        _contextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(configContext);

        var service = CreateRecordingService();

        await service.PurgeExpiredRecordsAsync(CancellationToken.None);

        service.Purges.Should().HaveCount(2);
        service.Purges.Should().OnlyContain(p => p.TenantId == tenantId);
        service.Purges.Select(p => p.Table).Should()
            .BeEquivalentTo(["read_access_log", "mutation_audit_log"]);
        service.Purges.Should().OnlyContain(
            p => p.Cutoff < DateTime.UtcNow.AddDays(-89) && p.Cutoff > DateTime.UtcNow.AddDays(-91));
    }

    [Fact]
    public async Task PurgeExpiredRecordsAsync_TenantConfig_OverridesPlatformDefault()
    {
        var tenantId = Guid.CreateVersion7();
        var configContext = CreateInMemoryContext();
        configContext.Tenants.Add(new TenantEntity { Id = tenantId, Slug = "configured" });
        configContext.TenantAuditConfig.Add(new TenantAuditConfigEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            // A longer window than the platform default must win too, not just a shorter one.
            MutationAuditRetentionDays = 365,
            ReadAuditRetentionDays = null,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow
        });
        await configContext.SaveChangesAsync();

        _contextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(configContext);

        var service = CreateRecordingService();

        await service.PurgeExpiredRecordsAsync(CancellationToken.None);

        var mutation = service.Purges.Single(p => p.Table == "mutation_audit_log");
        mutation.Cutoff.Should().BeBefore(DateTime.UtcNow.AddDays(-364));

        // The unset read window still falls back to the platform default.
        var read = service.Purges.Single(p => p.Table == "read_access_log");
        read.Cutoff.Should().BeAfter(DateTime.UtcNow.AddDays(-91));
    }

    [Fact]
    public async Task PurgeExpiredRecordsAsync_DefaultDisabled_PurgesOnlyConfiguredTenants()
    {
        var configuredTenant = Guid.CreateVersion7();
        var configContext = CreateInMemoryContext();
        configContext.Tenants.AddRange(
            new TenantEntity { Id = configuredTenant, Slug = "configured" },
            new TenantEntity { Id = Guid.CreateVersion7(), Slug = "unconfigured" });
        configContext.TenantAuditConfig.Add(new TenantAuditConfigEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = configuredTenant,
            MutationAuditRetentionDays = 30,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow
        });
        await configContext.SaveChangesAsync();

        _contextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(configContext);

        var service = CreateRecordingService(new Dictionary<string, string?>
        {
            ["Audit:DefaultReadAuditRetentionDays"] = "0",
            ["Audit:DefaultMutationRetentionDays"] = "0"
        });

        await service.PurgeExpiredRecordsAsync(CancellationToken.None);

        service.Purges.Should().ContainSingle()
            .Which.Should().Match<(Guid TenantId, string Table, DateTime Cutoff)>(
                p => p.TenantId == configuredTenant && p.Table == "mutation_audit_log");
    }

    [Fact]
    public async Task PurgeExpiredRecordsAsync_MultipleTenants_ContinuesAfterOneFailure()
    {
        // Arrange: two tenants, both with retention configured
        var tenant1 = Guid.CreateVersion7();
        var tenant2 = Guid.CreateVersion7();
        var configContext = CreateInMemoryContext();
        configContext.TenantAuditConfig.AddRange(
            new TenantAuditConfigEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant1,
                ReadAuditRetentionDays = 90,
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow
            },
            new TenantAuditConfigEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant2,
                ReadAuditRetentionDays = 60,
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow
            });
        await configContext.SaveChangesAsync();

        var callCount = 0;
        _contextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                // First call = config query; subsequent = deletion attempts (which fail on InMemory)
                return callCount == 1 ? configContext : CreateInMemoryContext();
            });

        var service = CreateService();

        // Act
        await service.PurgeExpiredRecordsAsync(CancellationToken.None);

        // Assert: both tenants were attempted (config + 2 deletion contexts, one per tenant)
        _contextFactory.Verify(
            f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(3)); // 1 config + 1 per tenant

        // Assert: warning logged for each tenant (raw SQL fails on InMemory)
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }
}
