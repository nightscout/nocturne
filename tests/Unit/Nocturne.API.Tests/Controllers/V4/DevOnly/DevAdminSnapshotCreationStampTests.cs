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
/// Neither restore branch adopts the snapshot's creation stamp: the insert does not carry one,
/// and the branch that updates a tenant the instance already has leaves the column alone.
/// That an unset stamp is then server-assigned is NocturneDbContext's own contract, pinned by
/// UpdateTimestampsTests in the Infrastructure.Data suite; nothing here re-states it.
/// </summary>
public class DevAdminSnapshotCreationStampTests : IDisposable
{
    private static readonly DateTime Stale = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _database = TestDbContextFactory.CreateSqlite();

    [Fact]
    public async Task ImportSnapshot_DoesNotAdoptTheSnapshotsCreationStamp()
    {
        var existingId = Guid.CreateVersion7();
        var restoredId = Guid.CreateVersion7();
        var before = DateTime.UtcNow;

        await using (var seed = _database.CreateContext())
        {
            seed.Tenants.Add(new TenantEntity
            {
                Id = existingId,
                Slug = "existing",
                DisplayName = "Existing",
                IsActive = true,
            });
            await seed.SaveChangesAsync();
        }

        DateTime stampedOnInsert;
        await using (var read = _database.CreateContext())
        {
            stampedOnInsert = (await read.Tenants.SingleAsync(t => t.Id == existingId)).SysCreatedAt;
        }

        await using (var context = _database.CreateContext())
        {
            var result = await NewController(context).ImportSnapshot(
                new DevSnapshotDto
                {
                    Tenants =
                    [
                        SnapshotOf(existingId, "existing"),
                        SnapshotOf(restoredId, "restored"),
                    ],
                },
                CancellationToken.None);

            result.Should().BeOfType<OkObjectResult>();
        }

        await using var verify = _database.CreateContext();

        var existing = await verify.Tenants.SingleAsync(t => t.Id == existingId);
        existing.SysCreatedAt.Should().Be(stampedOnInsert,
            "a restore over an existing tenant does not adopt the snapshot's creation stamp");

        var restored = await verify.Tenants.SingleAsync(t => t.Id == restoredId);
        restored.SysCreatedAt.Should().BeOnOrAfter(before,
            "the insert carries no creation stamp from the snapshot, so the row gets a current one");
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

    private static TenantSnapshotDto SnapshotOf(Guid id, string slug) =>
        new()
        {
            Tenant = new TenantEntityDto
            {
                Id = id,
                Slug = slug,
                DisplayName = slug,
                IsActive = true,
                SysCreatedAt = Stale,
                SysUpdatedAt = Stale,
            },
        };

    public void Dispose() => _database.Dispose();
}
