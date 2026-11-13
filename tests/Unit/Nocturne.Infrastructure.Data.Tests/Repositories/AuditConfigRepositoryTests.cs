using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary><see cref="AuditConfigRepository"/> over SQLite: the scope's tenant and no other.</summary>
[Trait("Category", "Unit")]
public class AuditConfigRepositoryTests : IDisposable
{
    private readonly Guid _scopeTenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public AuditConfigRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_scopeTenant, "scope").SeedTenant(_otherTenant, "other");
    }

    public void Dispose() => _db.Dispose();

    private AuditConfigRepository Repository() =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(_scopeTenant)));

    private async Task<List<TenantAuditConfigEntity>> StoredAsync()
    {
        await using var db = _db.CreateContext(Guid.Empty);
        return await db.TenantAuditConfig.IgnoreQueryFilters().AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task SavingWritesTheScopesTenant()
    {
        var saved = await Repository().SaveAsync(new TenantAuditConfig(true, 30, 120));

        saved.Should().Be(new TenantAuditConfig(true, 30, 120));
        (await StoredAsync()).Should().ContainSingle().Which.TenantId.Should().Be(_scopeTenant);
    }

    [Fact]
    public async Task SavingTwiceReplacesTheRowInsteadOfAddingOne()
    {
        await Repository().SaveAsync(new TenantAuditConfig(true, 30, 120));
        var saved = await Repository().SaveAsync(new TenantAuditConfig(false, null, null));

        saved.Should().Be(new TenantAuditConfig(false, null, null));
        (await StoredAsync()).Should().ContainSingle().Which.ReadAuditEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task SavingLeavesAnotherTenantsConfigAlone()
    {
        await using (var db = _db.CreateContext(_otherTenant))
        {
            db.TenantAuditConfig.Add(new TenantAuditConfigEntity
            {
                Id = Guid.CreateVersion7(), TenantId = _otherTenant, ReadAuditEnabled = true,
            });
            await db.SaveChangesAsync();
        }

        await Repository().SaveAsync(new TenantAuditConfig(false, 7, 7));

        var stored = await StoredAsync();
        stored.Should().HaveCount(2);
        stored.Single(c => c.TenantId == _otherTenant).ReadAuditEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task TheSoftDeleteWindowIsTheScopesTenantsAndNoOthers()
    {
        await using (var db = _db.CreateContext(_otherTenant))
        {
            db.TenantDataRetentionConfig.Add(new TenantDataRetentionConfigEntity
            {
                Id = Guid.CreateVersion7(), TenantId = _otherTenant, SoftDeleteRetentionDays = 45,
            });
            await db.SaveChangesAsync();
        }

        (await Repository().GetSoftDeleteRetentionDaysAsync()).Should().BeNull();
        (await new AuditConfigRepository(new TestNocturneDbContextFactory(_db.CreateContext(_otherTenant)))
            .GetSoftDeleteRetentionDaysAsync()).Should().Be(45);
    }
}
