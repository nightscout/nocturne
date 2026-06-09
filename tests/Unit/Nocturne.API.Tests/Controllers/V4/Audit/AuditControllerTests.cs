using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Moq;
using Nocturne.API.Controllers.V4.Audit;
using Nocturne.API.Models.Responses;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Audit;

[Trait("Category", "Unit")]
public class AuditControllerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly Mock<ITenantAuditConfigCache> _configCache = new();

    public AuditControllerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        // Create schema
        using var db = new NocturneDbContext(_dbOptions) { TenantId = TenantId };
        db.Database.EnsureCreated();
        db.Tenants.Add(new TenantEntity { Id = TenantId, Slug = "test" });
        db.SaveChanges();

        _tenantAccessor.Setup(t => t.TenantId).Returns(TenantId);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private AuditController CreateController(IReadOnlySet<string>? scopes = null)
    {
        var factoryMock = new Mock<IDbContextFactory<NocturneDbContext>>();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NocturneDbContext(_dbOptions) { TenantId = TenantId });

        // No config keys set → SoftDeleteRetentionPolicy falls back to its 30-day default.
        var configuration = new ConfigurationBuilder().Build();

        var controller = new AuditController(
            factoryMock.Object,
            _tenantAccessor.Object,
            _configCache.Object,
            configuration);

        var httpContext = new DefaultHttpContext();
        if (scopes != null)
            httpContext.Items["GrantedScopes"] = scopes;

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static IReadOnlySet<string> Scopes(params string[] permissions)
        => new HashSet<string>(permissions);

    // ── Permission tests ────────────────────────────────────────────

    [Fact]
    public async Task GetMutations_WithoutAuditRead_Returns403()
    {
        var controller = CreateController(Scopes("glucose.read"));

        var result = await controller.GetMutationAuditLog(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetMutations_WithAuditRead_Returns200()
    {
        var controller = CreateController(Scopes(TenantPermissions.AuditRead));

        var result = await controller.GetMutationAuditLog(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetMutations_WithAuditManage_Returns200()
    {
        // audit.manage implies audit.read
        var controller = CreateController(Scopes(TenantPermissions.AuditManage));

        var result = await controller.GetMutationAuditLog(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetMutations_WithNoScopes_Returns403()
    {
        var controller = CreateController(); // no scopes at all

        var result = await controller.GetMutationAuditLog(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetReads_WithoutPermission_Returns403()
    {
        var controller = CreateController(Scopes("glucose.read"));

        var result = await controller.GetReadAccessAuditLog(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetReads_WithAuditRead_Returns200()
    {
        var controller = CreateController(Scopes(TenantPermissions.AuditRead));

        var result = await controller.GetReadAccessAuditLog(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetConfig_WithoutAuditRead_Returns403()
    {
        var controller = CreateController(Scopes("glucose.read"));

        var result = await controller.GetAuditConfig(CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetConfig_WithAuditRead_Returns200()
    {
        _configCache
            .Setup(c => c.GetConfigAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAuditConfig(true, 90, 365));

        var controller = CreateController(Scopes(TenantPermissions.AuditRead));

        var result = await controller.GetAuditConfig(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateConfig_WithoutAuditManage_Returns403()
    {
        var controller = CreateController(Scopes(TenantPermissions.AuditRead));

        var result = await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = true,
            ReadAuditRetentionDays = 90,
            MutationAuditRetentionDays = 365,
        }, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task UpdateConfig_WithAuditManage_Returns200()
    {
        var controller = CreateController(Scopes(TenantPermissions.AuditManage));

        var result = await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = true,
            ReadAuditRetentionDays = 90,
            MutationAuditRetentionDays = 365,
        }, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateConfig_WithSuperuser_Returns200()
    {
        var controller = CreateController(Scopes(TenantPermissions.Superuser));

        var result = await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = false,
        }, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    // ── Query tests ─────────────────────────────────────────────────

    [Fact]
    public async Task GetMutations_ReturnsPaginatedResponse()
    {
        // Seed some mutation log entries
        await using (var db = new NocturneDbContext(_dbOptions) { TenantId = TenantId })
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < 5; i++)
            {
                db.MutationAuditLog.Add(new MutationAuditLogEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = TenantId,
                    EntityType = "Entry",
                    EntityId = Guid.CreateVersion7(),
                    Action = "create",
                    CreatedAt = now.AddMinutes(-i),
                });
            }
            await db.SaveChangesAsync();
        }

        var controller = CreateController(Scopes(TenantPermissions.AuditRead));

        var result = await controller.GetMutationAuditLog(
            DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, limit: 3);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<PaginatedResponse<MutationAuditDto>>().Subject;
        response.Data.Should().HaveCount(3);
        response.Pagination.Total.Should().Be(5);
        response.Pagination.Limit.Should().Be(3);
    }

    [Fact]
    public async Task GetMutations_FiltersByDateRange()
    {
        var now = DateTime.UtcNow;
        await using (var db = new NocturneDbContext(_dbOptions) { TenantId = TenantId })
        {
            db.MutationAuditLog.Add(new MutationAuditLogEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                EntityType = "Entry",
                EntityId = Guid.CreateVersion7(),
                Action = "create",
                CreatedAt = now.AddDays(-10), // outside range
            });
            db.MutationAuditLog.Add(new MutationAuditLogEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                EntityType = "Treatment",
                EntityId = Guid.CreateVersion7(),
                Action = "update",
                CreatedAt = now.AddHours(-1), // inside range
            });
            await db.SaveChangesAsync();
        }

        var controller = CreateController(Scopes(TenantPermissions.AuditRead));

        var result = await controller.GetMutationAuditLog(
            now.AddDays(-1), now);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<PaginatedResponse<MutationAuditDto>>().Subject;
        response.Data.Should().HaveCount(1);
        response.Data.First().EntityType.Should().Be("Treatment");
    }

    [Fact]
    public async Task GetReads_ReturnsPaginatedResponse()
    {
        await using (var db = new NocturneDbContext(_dbOptions) { TenantId = TenantId })
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < 3; i++)
            {
                db.ReadAccessLog.Add(new ReadAccessLogEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = TenantId,
                    Endpoint = $"GET /api/v4/sensor-glucoses",
                    StatusCode = 200,
                    CreatedAt = now.AddMinutes(-i),
                });
            }
            await db.SaveChangesAsync();
        }

        var controller = CreateController(Scopes(TenantPermissions.AuditRead));

        var result = await controller.GetReadAccessAuditLog(
            DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<PaginatedResponse<ReadAccessAuditDto>>().Subject;
        response.Data.Should().HaveCount(3);
        response.Pagination.Total.Should().Be(3);
    }

    // ── Config tests ────────────────────────────────────────────────

    [Fact]
    public async Task GetConfig_ReturnsCurrentConfig()
    {
        _configCache
            .Setup(c => c.GetConfigAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAuditConfig(true, 90, 365));

        var controller = CreateController(Scopes(TenantPermissions.AuditRead));

        var result = await controller.GetAuditConfig(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<AuditConfigDto>().Subject;
        dto.ReadAuditEnabled.Should().BeTrue();
        dto.ReadAuditRetentionDays.Should().Be(90);
        dto.MutationAuditRetentionDays.Should().Be(365);
    }

    [Fact]
    public async Task UpdateConfig_CreatesConfigIfNoneExists()
    {
        var controller = CreateController(Scopes(TenantPermissions.AuditManage));

        var result = await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = true,
            ReadAuditRetentionDays = 30,
            MutationAuditRetentionDays = 180,
        }, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<AuditConfigDto>().Subject;
        dto.ReadAuditEnabled.Should().BeTrue();
        dto.ReadAuditRetentionDays.Should().Be(30);
        dto.MutationAuditRetentionDays.Should().Be(180);

        // Verify persisted
        await using var db = new NocturneDbContext(_dbOptions) { TenantId = TenantId };
        var entity = await db.TenantAuditConfig.SingleOrDefaultAsync(c => c.TenantId == TenantId);
        entity.Should().NotBeNull();
        entity!.ReadAuditEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateConfig_UpdatesExistingConfig()
    {
        // Pre-seed a config
        await using (var db = new NocturneDbContext(_dbOptions) { TenantId = TenantId })
        {
            db.TenantAuditConfig.Add(new TenantAuditConfigEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                ReadAuditEnabled = false,
                ReadAuditRetentionDays = 30,
                MutationAuditRetentionDays = 90,
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var controller = CreateController(Scopes(TenantPermissions.AuditManage));

        var result = await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = true,
            ReadAuditRetentionDays = 60,
            MutationAuditRetentionDays = 365,
        }, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<AuditConfigDto>().Subject;
        dto.ReadAuditEnabled.Should().BeTrue();
        dto.ReadAuditRetentionDays.Should().Be(60);

        // Verify only one row exists (updated, not duplicated)
        await using var db2 = new NocturneDbContext(_dbOptions) { TenantId = TenantId };
        var count = await db2.TenantAuditConfig.CountAsync(c => c.TenantId == TenantId);
        count.Should().Be(1);
    }

    [Fact]
    public async Task UpdateConfig_InvalidatesCache()
    {
        var controller = CreateController(Scopes(TenantPermissions.AuditManage));

        await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = true,
        }, CancellationToken.None);

        _configCache.Verify(c => c.Invalidate(TenantId), Times.Once);
    }

    // ── Retention-ordering validation ───────────────────────────────
    // Mutation audit retention must cover the effective soft-delete window; otherwise
    // audit rows age out while the soft-deleted entities they describe still live,
    // losing user-delete attribution. The effective window is the tenant's configured
    // value, or the instance default (30d here, floored at 7d) when unset or when no
    // retention row exists — matching SoftDeleteCleanupService. A null (infinite) audit
    // retention always covers it. The symmetric direction (rejecting a soft-delete
    // retention bump that exceeds audit retention) will land when the
    // TenantDataRetentionConfig update endpoint exists.

    private async Task SeedSoftDeleteRetentionAsync(int? days)
    {
        await using var db = new NocturneDbContext(_dbOptions) { TenantId = TenantId };
        db.TenantDataRetentionConfig.Add(new TenantDataRetentionConfigEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            SoftDeleteRetentionDays = days,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UpdateConfig_AuditShorterThanSoftDelete_Returns400()
    {
        await SeedSoftDeleteRetentionAsync(30);
        var controller = CreateController(Scopes(TenantPermissions.AuditManage));

        var result = await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = true,
            MutationAuditRetentionDays = 10,
        }, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().NotBeNull();
        var payload = bad.Value!.ToString();
        payload.Should().Contain("10");
        payload.Should().Contain("30");
    }

    [Fact]
    public async Task UpdateConfig_AuditEqualToSoftDelete_Returns200()
    {
        await SeedSoftDeleteRetentionAsync(30);
        var controller = CreateController(Scopes(TenantPermissions.AuditManage));

        var result = await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = true,
            MutationAuditRetentionDays = 30,
        }, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateConfig_AuditLongerThanSoftDelete_Returns200()
    {
        await SeedSoftDeleteRetentionAsync(30);
        var controller = CreateController(Scopes(TenantPermissions.AuditManage));

        var result = await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = true,
            MutationAuditRetentionDays = 90,
        }, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateConfig_AuditNullWithFiniteSoftDelete_Returns200()
    {
        await SeedSoftDeleteRetentionAsync(30);
        var controller = CreateController(Scopes(TenantPermissions.AuditManage));

        var result = await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = true,
            MutationAuditRetentionDays = null,
        }, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateConfig_FiniteAuditBelowDefault_NullSoftDelete_Returns400()
    {
        // Null soft-delete retention is not "infinite" — the cleanup service falls back to
        // the instance default (30d), so audit retention must still cover that window.
        await SeedSoftDeleteRetentionAsync(null);
        var controller = CreateController(Scopes(TenantPermissions.AuditManage));

        var result = await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = true,
            MutationAuditRetentionDays = 10,
        }, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().NotBeNull();
        var payload = bad.Value!.ToString();
        payload.Should().Contain("10");
        payload.Should().Contain("30");
    }

    [Fact]
    public async Task UpdateConfig_NullAuditWithNullSoftDelete_Returns200()
    {
        await SeedSoftDeleteRetentionAsync(null);
        var controller = CreateController(Scopes(TenantPermissions.AuditManage));

        var result = await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = true,
            MutationAuditRetentionDays = null,
        }, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task UpdateConfig_FiniteAuditBelowDefault_NoRetentionRow_Returns400()
    {
        // No TenantDataRetentionConfig row at all: the cleanup service still purges
        // soft-deleted rows at the instance default (30d), so the validator must reject a
        // shorter audit retention rather than skip the check. Otherwise a user-delete's
        // audit row ages out at 10d while the entity lives to 30d, and a connector resync
        // silently recreates it.
        var controller = CreateController(Scopes(TenantPermissions.AuditManage));

        var result = await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = true,
            MutationAuditRetentionDays = 10,
        }, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().NotBeNull();
        var payload = bad.Value!.ToString();
        payload.Should().Contain("10");
        payload.Should().Contain("30");
    }

    [Fact]
    public async Task UpdateConfig_FiniteAuditAtDefault_NoRetentionRow_Returns200()
    {
        var controller = CreateController(Scopes(TenantPermissions.AuditManage));

        var result = await controller.UpdateAuditConfig(new AuditConfigDto
        {
            ReadAuditEnabled = true,
            MutationAuditRetentionDays = 30,
        }, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
}
