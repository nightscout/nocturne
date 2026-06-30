using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Verifies the single enforcement point for system timestamps and tenant ownership:
/// <c>NocturneDbContext.UpdateTimestamps</c>, driven by the timestamp marker interfaces
/// (<see cref="ISystemTimestamped"/>, <see cref="ISystemCreated"/>,
/// <see cref="IEntityTimestamped"/>, <see cref="IEntityCreated"/>) plus a small set of
/// entity-specific columns. EF InMemory runs the SaveChanges override, so timestamp
/// stamping is exercised end to end.
/// </summary>
public class UpdateTimestampsTests
{
    // A clearly-stale sentinel so a freshly-stamped utcNow value is unambiguously newer.
    private static readonly DateTime Stale = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SystemTimestamped_StampsBothOnInsert_AndBumpsUpdatedOnModify()
    {
        var options = NewStore();
        var tenantId = Guid.NewGuid();
        var id = Guid.CreateVersion7();
        var before = DateTime.UtcNow;

        await using (var ctx = new NocturneDbContext(options) { TenantId = tenantId })
        {
            ctx.Foods.Add(new FoodEntity { Id = id, SysCreatedAt = Stale, SysUpdatedAt = Stale });
            await ctx.SaveChangesAsync();
        }

        DateTime createdAfterInsert;
        await using (var ctx = new NocturneDbContext(options) { TenantId = tenantId })
        {
            var food = await ctx.Foods.SingleAsync();
            food.SysCreatedAt.Should().BeOnOrAfter(before, "sys_created_at is stamped on insert");
            food.SysUpdatedAt.Should().BeOnOrAfter(before, "sys_updated_at is stamped on insert");
            createdAfterInsert = food.SysCreatedAt;

            food.Name = "changed";
            await Task.Delay(5);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = new NocturneDbContext(options) { TenantId = tenantId })
        {
            var food = await ctx.Foods.SingleAsync();
            food.SysCreatedAt.Should().Be(createdAfterInsert, "sys_created_at is preserved across updates");
            food.SysUpdatedAt.Should().BeAfter(createdAfterInsert, "sys_updated_at is bumped on every save");
        }
    }

    [Fact]
    public async Task SystemCreated_StampsCreatedOnInsertOnly()
    {
        var options = NewStore();
        var tenantId = Guid.NewGuid();
        var before = DateTime.UtcNow;

        await using (var ctx = new NocturneDbContext(options) { TenantId = tenantId })
        {
            ctx.UserFoodFavorites.Add(new UserFoodFavoriteEntity { Id = Guid.CreateVersion7(), SysCreatedAt = Stale });
            await ctx.SaveChangesAsync();
        }

        await using (var verify = new NocturneDbContext(options) { TenantId = tenantId })
        {
            var favorite = await verify.UserFoodFavorites.SingleAsync();
            favorite.SysCreatedAt.Should().BeOnOrAfter(before, "create-only entities are stamped on insert");
        }
    }

    [Fact]
    public async Task EntityTimestamped_StampsCreatedAndUpdated()
    {
        var options = NewStore();
        var before = DateTime.UtcNow;

        await using (var ctx = new NocturneDbContext(options))
        {
            ctx.Subjects.Add(new SubjectEntity { Id = Guid.CreateVersion7(), CreatedAt = Stale, UpdatedAt = Stale });
            await ctx.SaveChangesAsync();
        }

        await using (var verify = new NocturneDbContext(options))
        {
            var subject = await verify.Subjects.SingleAsync();
            subject.CreatedAt.Should().BeOnOrAfter(before, "created_at is stamped on insert");
            subject.UpdatedAt.Should().BeOnOrAfter(before, "updated_at is stamped on insert");
        }
    }

    [Fact]
    public async Task ClockFace_StampsAllFourConventions()
    {
        var options = NewStore();
        var tenantId = Guid.NewGuid();
        var before = DateTime.UtcNow;

        await using (var ctx = new NocturneDbContext(options) { TenantId = tenantId })
        {
            ctx.ClockFaces.Add(new ClockFaceEntity
            {
                Id = Guid.CreateVersion7(),
                CreatedAt = Stale,
                UpdatedAt = null,
                SysCreatedAt = Stale,
                SysUpdatedAt = Stale,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var verify = new NocturneDbContext(options) { TenantId = tenantId })
        {
            var clockFace = await verify.ClockFaces.SingleAsync();
            clockFace.SysCreatedAt.Should().BeOnOrAfter(before);
            clockFace.SysUpdatedAt.Should().BeOnOrAfter(before);
            clockFace.CreatedAt.Should().BeOnOrAfter(before);
            clockFace.UpdatedAt.Should().NotBeNull("the nullable updated_at is stamped on every save");
            clockFace.UpdatedAt!.Value.Should().BeOnOrAfter(before);
        }
    }

    [Fact]
    public async Task ConnectorConfiguration_StampsLastModifiedOnInsert()
    {
        var options = NewStore();
        var tenantId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;

        await using (var ctx = new NocturneDbContext(options) { TenantId = tenantId })
        {
            ctx.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
            {
                Id = Guid.CreateVersion7(),
                LastModified = new DateTimeOffset(Stale),
            });
            await ctx.SaveChangesAsync();
        }

        DateTimeOffset stampedOnInsert;
        await using (var verify = new NocturneDbContext(options) { TenantId = tenantId })
        {
            var config = await verify.ConnectorConfigurations.SingleAsync();
            config.LastModified.Should().BeOnOrAfter(before, "last_modified is stamped on insert");
            stampedOnInsert = config.LastModified;

            // last_modified is insert-only: a later save must not bump it.
            await Task.Delay(5);
            verify.Entry(config).State = EntityState.Modified;
            await verify.SaveChangesAsync();
        }

        await using (var verify = new NocturneDbContext(options) { TenantId = tenantId })
        {
            var config = await verify.ConnectorConfigurations.SingleAsync();
            config.LastModified.Should().Be(stampedOnInsert, "last_modified is not re-stamped on update");
        }
    }

    [Fact]
    public async Task OAuthRefreshToken_StampsIssuedAtOnInsert()
    {
        var options = NewStore();
        var tenantId = Guid.NewGuid();
        var before = DateTime.UtcNow;

        await using (var ctx = new NocturneDbContext(options) { TenantId = tenantId })
        {
            ctx.OAuthRefreshTokens.Add(new OAuthRefreshTokenEntity { Id = Guid.CreateVersion7(), IssuedAt = Stale });
            await ctx.SaveChangesAsync();
        }

        DateTime stampedOnInsert;
        await using (var verify = new NocturneDbContext(options) { TenantId = tenantId })
        {
            var token = await verify.OAuthRefreshTokens.SingleAsync();
            token.IssuedAt.Should().BeOnOrAfter(before, "issued_at is stamped on insert");
            stampedOnInsert = token.IssuedAt;

            // issued_at is insert-only: a later save must not bump it.
            await Task.Delay(5);
            verify.Entry(token).State = EntityState.Modified;
            await verify.SaveChangesAsync();
        }

        await using (var verify = new NocturneDbContext(options) { TenantId = tenantId })
        {
            var token = await verify.OAuthRefreshTokens.SingleAsync();
            token.IssuedAt.Should().Be(stampedOnInsert, "issued_at is not re-stamped on update");
        }
    }

    [Fact]
    public async Task TenantScoped_InheritsTenantFromContextOnInsert()
    {
        var options = NewStore();
        var tenantId = Guid.NewGuid();

        await using (var ctx = new NocturneDbContext(options) { TenantId = tenantId })
        {
            ctx.Foods.Add(new FoodEntity { Id = Guid.CreateVersion7() });
            await ctx.SaveChangesAsync();
        }

        await using (var verify = new NocturneDbContext(options) { TenantId = tenantId })
        {
            var food = await verify.Foods.SingleAsync();
            food.TenantId.Should().Be(tenantId, "a new tenant-scoped row inherits the resolved tenant");
        }
    }

    [Fact]
    public async Task TenantScoped_WithoutResolvableTenant_Throws()
    {
        var options = NewStore();
        await using var ctx = new NocturneDbContext(options); // TenantId left as Guid.Empty

        ctx.Foods.Add(new FoodEntity { Id = Guid.CreateVersion7() });

        var act = () => ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>(
            "saving a tenant-scoped entity without a resolvable tenant must fail closed");
    }

    [Fact]
    public async Task TenantScoped_CrossTenantModify_Throws()
    {
        var options = NewStore();
        var ownerTenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var id = Guid.CreateVersion7();

        await using (var ctx = new NocturneDbContext(options) { TenantId = ownerTenant })
        {
            ctx.Foods.Add(new FoodEntity { Id = id });
            await ctx.SaveChangesAsync();
        }

        await using var attacker = new NocturneDbContext(options) { TenantId = otherTenant };
        // IgnoreQueryFilters reaches the other tenant's row; the modify guard must still reject it.
        var food = await attacker.Foods.IgnoreQueryFilters().SingleAsync();
        food.Name = "tampered";

        var act = () => attacker.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>(
            "modifying a row owned by another tenant must fail closed");
    }

    private static DbContextOptions<NocturneDbContext> NewStore() =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"update_timestamps_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
}
