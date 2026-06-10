using FluentAssertions;
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

    // Blocking is decided from the deleted_by_user flag carried on the row (the audit
    // interceptor / bulk-delete helpers set it at delete time). deletedByUser only
    // applies to soft-deleted rows.
    private TempBasalEntity SeedTempBasal(string legacyId, bool softDeleted, bool deletedByUser = false)
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
        _ctx.Entry(entity).Property("DeletedByUser").CurrentValue = deletedByUser;
        _ctx.SaveChanges();
        return entity;
    }

    [Fact]
    public async Task ActiveRow_AlwaysBlocks()
    {
        SeedTempBasal("legacy-1", softDeleted: false);
        var blocked = await _ctx.GetBlockingLegacyIdsAsync<TempBasalEntity>(new HashSet<string> { "legacy-1" });
        blocked.Should().Contain("legacy-1");
    }

    [Fact]
    public async Task SoftDeleted_SystemDelete_DoesNotBlock()
    {
        SeedTempBasal("legacy-1", softDeleted: true, deletedByUser: false);
        var blocked = await _ctx.GetBlockingLegacyIdsAsync<TempBasalEntity>(new HashSet<string> { "legacy-1" });
        blocked.Should().BeEmpty();
    }

    [Fact]
    public async Task SoftDeleted_UserDelete_Blocks()
    {
        SeedTempBasal("legacy-1", softDeleted: true, deletedByUser: true);
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
    public async Task UnknownLegacyId_DoesNotBlock()
    {
        var blocked = await _ctx.GetBlockingLegacyIdsAsync<TempBasalEntity>(new HashSet<string> { "legacy-unknown" });
        blocked.Should().BeEmpty();
    }

    [Fact]
    public async Task MixedBatch_OnlyBlocksBlockingOnes()
    {
        SeedTempBasal("legacy-active", softDeleted: false);
        SeedTempBasal("legacy-system", softDeleted: true, deletedByUser: false);
        SeedTempBasal("legacy-user", softDeleted: true, deletedByUser: true);

        var blocked = await _ctx.GetBlockingLegacyIdsAsync<TempBasalEntity>(
            new HashSet<string> { "legacy-active", "legacy-system", "legacy-user", "legacy-unknown" });

        blocked.Should().BeEquivalentTo(new[] { "legacy-active", "legacy-user" });
    }

    private DeviceStatusExtrasEntity SeedDeviceStatusExtras(Guid correlationId, bool softDeleted, bool deletedByUser = false)
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
        _ctx.Entry(entity).Property("DeletedByUser").CurrentValue = deletedByUser;
        _ctx.SaveChanges();
        return entity;
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
        SeedDeviceStatusExtras(corrId, softDeleted: true, deletedByUser: false);

        var blocked = await _ctx.GetBlockingCorrelationIdsAsync(new HashSet<Guid> { corrId });

        blocked.Should().BeEmpty();
    }

    [Fact]
    public async Task CorrelationId_UserSoftDeleted_Blocks()
    {
        var corrId = Guid.NewGuid();
        SeedDeviceStatusExtras(corrId, softDeleted: true, deletedByUser: true);

        var blocked = await _ctx.GetBlockingCorrelationIdsAsync(new HashSet<Guid> { corrId });

        blocked.Should().Contain(corrId);
    }
}
