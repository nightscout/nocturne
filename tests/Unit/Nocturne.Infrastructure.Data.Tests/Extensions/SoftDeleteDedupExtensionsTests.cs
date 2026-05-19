using FluentAssertions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

[Trait("Category", "Unit")]
public class SoftDeleteDedupExtensionsTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly NocturneDbContext _ctx;

    public SoftDeleteDedupExtensionsTests()
    {
        _ctx = TestDbContextFactory.CreateInMemoryContext();
        _ctx.TenantId = TenantA;
    }

    public void Dispose() { _ctx.Dispose(); GC.SuppressFinalize(this); }

    private TempBasalEntity SeedTempBasal(string legacyId, bool softDeleted)
    {
        var entity = new TempBasalEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantA,
            LegacyId = legacyId,
            StartTimestamp = DateTime.UtcNow,
            Origin = "Manual",
            DeletedAt = softDeleted ? DateTime.UtcNow.AddHours(-1) : null,
        };
        _ctx.TempBasals.Add(entity);
        _ctx.SaveChanges();
        return entity;
    }

    private void SeedDeleteAudit(Guid entityId, string? authType, DateTime? createdAt = null)
    {
        _ctx.MutationAuditLog.Add(new MutationAuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantA,
            EntityType = "TempBasal",
            EntityId = entityId,
            Action = "delete",
            AuthType = authType,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        });
        _ctx.SaveChanges();
    }

    [Fact]
    public async Task ActiveRow_AlwaysBlocks()
    {
        SeedTempBasal("legacy-1", softDeleted: false);
        var blocked = await _ctx.GetBlockingLegacyIdsAsync<TempBasalEntity>(new HashSet<string> { "legacy-1" });
        blocked.Should().Contain("legacy-1");
    }

    [Fact]
    public async Task SoftDeleted_NoAuditRow_DoesNotBlock()
    {
        SeedTempBasal("legacy-1", softDeleted: true);
        var blocked = await _ctx.GetBlockingLegacyIdsAsync<TempBasalEntity>(new HashSet<string> { "legacy-1" });
        blocked.Should().BeEmpty();
    }

    [Fact]
    public async Task SoftDeleted_SystemDelete_NullAuthType_DoesNotBlock()
    {
        var entity = SeedTempBasal("legacy-1", softDeleted: true);
        SeedDeleteAudit(entity.Id, authType: null);
        var blocked = await _ctx.GetBlockingLegacyIdsAsync<TempBasalEntity>(new HashSet<string> { "legacy-1" });
        blocked.Should().BeEmpty();
    }

    [Fact]
    public async Task SoftDeleted_UserDelete_Bearer_Blocks()
    {
        var entity = SeedTempBasal("legacy-1", softDeleted: true);
        SeedDeleteAudit(entity.Id, authType: "OAuthAccessToken");
        var blocked = await _ctx.GetBlockingLegacyIdsAsync<TempBasalEntity>(new HashSet<string> { "legacy-1" });
        blocked.Should().Contain("legacy-1");
    }

    [Fact]
    public async Task SoftDeleted_GuestDelete_Blocks()
    {
        var entity = SeedTempBasal("legacy-1", softDeleted: true);
        SeedDeleteAudit(entity.Id, authType: "Guest");
        var blocked = await _ctx.GetBlockingLegacyIdsAsync<TempBasalEntity>(new HashSet<string> { "legacy-1" });
        blocked.Should().Contain("legacy-1");
    }

    [Fact]
    public async Task SoftDeleted_UserThenSystemLatestWins_DoesNotBlock()
    {
        var entity = SeedTempBasal("legacy-1", softDeleted: true);
        SeedDeleteAudit(entity.Id, authType: "Bearer", createdAt: DateTime.UtcNow.AddDays(-2));
        SeedDeleteAudit(entity.Id, authType: null,     createdAt: DateTime.UtcNow.AddDays(-1));
        var blocked = await _ctx.GetBlockingLegacyIdsAsync<TempBasalEntity>(new HashSet<string> { "legacy-1" });
        blocked.Should().BeEmpty();
    }

    [Fact]
    public async Task SoftDeleted_SystemThenUserLatestWins_Blocks()
    {
        var entity = SeedTempBasal("legacy-1", softDeleted: true);
        SeedDeleteAudit(entity.Id, authType: null,     createdAt: DateTime.UtcNow.AddDays(-2));
        SeedDeleteAudit(entity.Id, authType: "Bearer", createdAt: DateTime.UtcNow.AddDays(-1));
        var blocked = await _ctx.GetBlockingLegacyIdsAsync<TempBasalEntity>(new HashSet<string> { "legacy-1" });
        blocked.Should().Contain("legacy-1");
    }

    [Fact]
    public async Task EmptyInput_ReturnsEmpty()
    {
        var blocked = await _ctx.GetBlockingLegacyIdsAsync<TempBasalEntity>(new HashSet<string>());
        blocked.Should().BeEmpty();
    }

    [Fact]
    public async Task MixedBatch_OnlyBlocksBlockingOnes()
    {
        SeedTempBasal("legacy-active", softDeleted: false);
        var sysSoft = SeedTempBasal("legacy-system", softDeleted: true);
        SeedDeleteAudit(sysSoft.Id, authType: null);
        var userSoft = SeedTempBasal("legacy-user", softDeleted: true);
        SeedDeleteAudit(userSoft.Id, authType: "Bearer");

        var blocked = await _ctx.GetBlockingLegacyIdsAsync<TempBasalEntity>(
            new HashSet<string> { "legacy-active", "legacy-system", "legacy-user", "legacy-unknown" });

        blocked.Should().BeEquivalentTo(new[] { "legacy-active", "legacy-user" });
    }

    private DeviceStatusExtrasEntity SeedDeviceStatusExtras(Guid correlationId, bool softDeleted)
    {
        var entity = new DeviceStatusExtrasEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantA,
            CorrelationId = correlationId,
            Timestamp = DateTime.UtcNow,
            ExtrasJson = "{}",
            DeletedAt = softDeleted ? DateTime.UtcNow.AddHours(-1) : null,
        };
        _ctx.DeviceStatusExtras.Add(entity);
        _ctx.SaveChanges();
        return entity;
    }

    private void SeedDeviceStatusExtrasDeleteAudit(Guid entityId, string? authType, DateTime? createdAt = null)
    {
        _ctx.MutationAuditLog.Add(new MutationAuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantA,
            EntityType = "DeviceStatusExtras",
            EntityId = entityId,
            Action = "delete",
            AuthType = authType,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        });
        _ctx.SaveChanges();
    }

    [Fact]
    public async Task CorrelationId_ActiveRow_AlwaysBlocks()
    {
        var corrId = Guid.NewGuid();
        SeedDeviceStatusExtras(corrId, softDeleted: false);

        var blocked = await _ctx.GetBlockingCorrelationIdsAsync(new HashSet<Guid> { corrId });

        blocked.Should().Contain(corrId);
    }

    [Fact]
    public async Task CorrelationId_SystemSoftDeleted_DoesNotBlock()
    {
        var corrId = Guid.NewGuid();
        var entity = SeedDeviceStatusExtras(corrId, softDeleted: true);
        SeedDeviceStatusExtrasDeleteAudit(entity.Id, authType: null);

        var blocked = await _ctx.GetBlockingCorrelationIdsAsync(new HashSet<Guid> { corrId });

        blocked.Should().BeEmpty();
    }

    [Fact]
    public async Task CorrelationId_UserSoftDeleted_Blocks()
    {
        var corrId = Guid.NewGuid();
        var entity = SeedDeviceStatusExtras(corrId, softDeleted: true);
        SeedDeviceStatusExtrasDeleteAudit(entity.Id, authType: "Bearer");

        var blocked = await _ctx.GetBlockingCorrelationIdsAsync(new HashSet<Guid> { corrId });

        blocked.Should().Contain(corrId);
    }
}
