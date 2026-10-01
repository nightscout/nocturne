using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.DevOnly;
using Nocturne.API.Models.DevOnly;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.DevOnly;

/// <summary>
/// Neither restore endpoint adopts the snapshot's update stamps or connector
/// <c>last_modified</c>: inserted rows carry none, and a tenant updated in place with nothing
/// else changed is not "touched" back to the snapshot's value. Server assignment of an unset
/// stamp is NocturneDbContext's contract, pinned by UpdateTimestampsTests.
/// </summary>
public class DevAdminSnapshotUpdateStampTests : IDisposable
{
    private static readonly DateTime Stale = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _database = TestDbContextFactory.CreateSqlite();

    [Fact]
    public async Task ImportSnapshot_DoesNotAdoptTheSnapshotsUpdateStamps()
    {
        var existingId = Guid.CreateVersion7();
        var restoredId = Guid.CreateVersion7();

        await using (var seed = _database.CreateContext())
        {
            seed.Tenants.Add(new TenantEntity { Id = existingId, Slug = "existing", DisplayName = "existing", IsActive = true });
            await seed.SaveChangesAsync();
        }

        DateTime existingStamp;
        await using (var read = _database.CreateContext())
        {
            existingStamp = (await read.Tenants.SingleAsync(t => t.Id == existingId)).SysUpdatedAt;
        }

        var before = DateTime.UtcNow;
        var restored = ScopedSnapshotOf(restoredId);
        restored.Tenant = TenantDto(restoredId, "restored");

        await using (var context = _database.CreateContext())
        {
            var result = await NewController(context).ImportSnapshot(
                new DevSnapshotDto { Tenants = [new TenantSnapshotDto { Tenant = TenantDto(existingId, "existing") }, restored] },
                CancellationToken.None);

            result.Should().BeOfType<OkObjectResult>();
        }

        await using (var verify = _database.CreateContext())
        {
            (await verify.Tenants.SingleAsync(t => t.Id == existingId)).SysUpdatedAt.Should().Be(existingStamp,
                "a restore that changes nothing on an existing tenant does not touch it to the snapshot's stamp");
            (await verify.Tenants.SingleAsync(t => t.Id == restoredId)).SysUpdatedAt.Should().BeOnOrAfter(before);
        }

        await AssertRestoredRowsStampedAfter(restoredId, before);
    }

    [Fact]
    public async Task ImportScopedSnapshot_DoesNotAdoptTheSnapshotsUpdateStamps()
    {
        var tenantId = Guid.CreateVersion7();
        _database.SeedTenant(tenantId, "scoped");
        var before = DateTime.UtcNow;

        await using (var context = _database.CreateContext())
        {
            var result = await NewController(context).ImportScopedSnapshot(
                tenantId, ScopedSnapshotOf(tenantId), CancellationToken.None);

            result.Should().BeOfType<OkObjectResult>();
        }

        await AssertRestoredRowsStampedAfter(tenantId, before);
    }

    private async Task AssertRestoredRowsStampedAfter(Guid tenantId, DateTime before)
    {
        await using var verify = _database.CreateContext(tenantId);

        (await verify.Subjects.SingleAsync()).UpdatedAt.Should().BeOnOrAfter(before);
        (await verify.TenantRoles.SingleAsync()).SysUpdatedAt.Should().BeOnOrAfter(before);
        (await verify.TenantMembers.SingleAsync()).SysUpdatedAt.Should().BeOnOrAfter(before);
        (await verify.OAuthClients.SingleAsync()).UpdatedAt.Should().BeOnOrAfter(before);

        var config = await verify.ConnectorConfigurations.SingleAsync();
        config.SysUpdatedAt.Should().BeOnOrAfter(before);
        config.LastModified.Should().BeOnOrAfter(new DateTimeOffset(before));
    }

    private DevAdminController NewController(NocturneDbContext context) =>
        new(
            context,
            Mock.Of<ISecretEncryptionService>(),
            Mock.Of<IConnectorSyncService>(),
            Mock.Of<ITenantAccessor>(),
            Mock.Of<ITenantService>(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DevAdminController>.Instance);

    private static TenantEntityDto TenantDto(Guid id, string slug) =>
        new() { Id = id, Slug = slug, DisplayName = slug, IsActive = true, AllowAccessRequests = true, SysCreatedAt = Stale, SysUpdatedAt = Stale };

    private static TenantSnapshotDto ScopedSnapshotOf(Guid tenantId)
    {
        var subjectId = Guid.CreateVersion7();
        return new TenantSnapshotDto
        {
            Subjects = [new SubjectEntityDto { Id = subjectId, Name = "subject", IsActive = true, UpdatedAt = Stale }],
            Roles = [new TenantRoleEntityDto { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "role", Slug = "role", SysUpdatedAt = Stale }],
            Members = [new TenantMemberEntityDto { Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = subjectId, SysUpdatedAt = Stale }],
            OAuthClients = [new OAuthClientEntityDto { Id = Guid.CreateVersion7(), TenantId = tenantId, ClientId = "client", UpdatedAt = Stale }],
            ConnectorConfigurations =
            [
                new ConnectorConfigSnapshotDto
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    ConnectorName = "connector",
                    LastModified = new DateTimeOffset(Stale),
                    SysUpdatedAt = Stale,
                },
            ],
        };
    }

    public void Dispose() => _database.Dispose();
}
