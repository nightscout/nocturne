using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Pins which subject the startup bootstrap promotes. The owner lookup walks tenants oldest-first
/// and asks each under its own tenant pin, so these tests fix the ordering and the skip-a-tenant-
/// without-an-owner behaviour that the per-tenant scan has to preserve.
/// </summary>
/// <remarks>
/// SQLite has no Row Level Security, so every row is visible regardless of the pin. These tests
/// prove the resolution logic, not that a policy would hide anything.
/// </remarks>
public class PlatformAdminBootstrapServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly NocturneDbContext _db;

    public PlatformAdminBootstrapServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _db = new NocturneDbContext(_dbOptions);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task WhenAdminSubjectIdsAreConfigured_ThoseSubjectsAreGranted()
    {
        var configured = await AddSubjectAsync("configured");
        var oldestOwner = await AddTenantWithOwnerAsync("oldest", new DateTime(2020, 1, 1));

        await BootstrapAsync(adminSubjectIds: [configured]);

        (await IsPlatformAdminAsync(configured)).Should().BeTrue();
        (await IsPlatformAdminAsync(oldestOwner)).Should().BeFalse(
            "explicit configuration takes precedence over the implicit owner bootstrap");
    }

    [Fact]
    public async Task WhenAPlatformAdminAlreadyExists_NobodyElseIsGranted()
    {
        var existing = await AddSubjectAsync("existing", isPlatformAdmin: true);
        var oldestOwner = await AddTenantWithOwnerAsync("oldest", new DateTime(2020, 1, 1));

        await BootstrapAsync();

        (await IsPlatformAdminAsync(existing)).Should().BeTrue();
        (await IsPlatformAdminAsync(oldestOwner)).Should().BeFalse();
    }

    [Fact]
    public async Task TheOwnerOfTheOldestTenantIsGranted()
    {
        var olderOwner = await AddTenantWithOwnerAsync("older", new DateTime(2020, 1, 1));
        var newerOwner = await AddTenantWithOwnerAsync("newer", new DateTime(2024, 1, 1));

        await BootstrapAsync();

        (await IsPlatformAdminAsync(olderOwner)).Should().BeTrue();
        (await IsPlatformAdminAsync(newerOwner)).Should().BeFalse();
    }

    [Fact]
    public async Task ATenantWithNoOwnerIsSkippedForTheNextOldest()
    {
        await AddTenantWithoutOwnerAsync("ownerless-oldest", new DateTime(2019, 1, 1));
        var owner = await AddTenantWithOwnerAsync("owned", new DateTime(2020, 1, 1));

        await BootstrapAsync();

        (await IsPlatformAdminAsync(owner)).Should().BeTrue();
    }

    [Fact]
    public async Task WhenNoTenantHasAnOwner_NobodyIsGranted()
    {
        var memberOnly = await AddTenantWithoutOwnerAsync("ownerless", new DateTime(2020, 1, 1));

        await BootstrapAsync();

        (await IsPlatformAdminAsync(memberOnly)).Should().BeFalse();
        (await _db.Subjects.AsNoTracking().AnyAsync(s => s.IsPlatformAdmin)).Should().BeFalse();
    }

    private Task BootstrapAsync(List<Guid>? adminSubjectIds = null) =>
        new PlatformAdminBootstrapService(
            new TestDbContextFactory(_dbOptions),
            Options.Create(new PlatformOptions { AdminSubjectIds = adminSubjectIds ?? [] }),
            NullLogger<PlatformAdminBootstrapService>.Instance)
            .BootstrapAsync(CancellationToken.None);

    private sealed class TestDbContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    private async Task<bool> IsPlatformAdminAsync(Guid subjectId) =>
        await _db.Subjects.AsNoTracking()
            .Where(s => s.Id == subjectId)
            .Select(s => s.IsPlatformAdmin)
            .SingleAsync();

    private async Task<Guid> AddSubjectAsync(string name, bool isPlatformAdmin = false)
    {
        var subject = new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Username = name,
            IsActive = true,
            IsPlatformAdmin = isPlatformAdmin,
        };
        _db.Subjects.Add(subject);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return subject.Id;
    }

    /// <summary>Seeds a tenant whose sole member holds the owner role, and returns that subject's id.</summary>
    private async Task<Guid> AddTenantWithOwnerAsync(string slug, DateTime createdAt) =>
        await AddTenantAsync(slug, createdAt, TenantPermissions.SeedRoles.Owner);

    /// <summary>Seeds a tenant whose sole member holds a non-owner role, and returns that subject's id.</summary>
    private async Task<Guid> AddTenantWithoutOwnerAsync(string slug, DateTime createdAt) =>
        await AddTenantAsync(slug, createdAt, TenantPermissions.SeedRoles.Viewer);

    private async Task<Guid> AddTenantAsync(string slug, DateTime createdAt, string roleSlug)
    {
        var tenantId = Guid.CreateVersion7();
        var subjectId = await AddSubjectAsync($"{slug}-member");
        var roleId = Guid.CreateVersion7();
        var memberId = Guid.CreateVersion7();

        _db.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = slug,
            DisplayName = slug,
            IsActive = true,
            SysCreatedAt = createdAt,
        });
        _db.TenantRoles.Add(new TenantRoleEntity
        {
            Id = roleId,
            TenantId = tenantId,
            Name = roleSlug,
            Slug = roleSlug,
            Permissions = [],
            IsSystem = true,
        });
        _db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = memberId,
            TenantId = tenantId,
            SubjectId = subjectId,
        });
        _db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = memberId,
            TenantRoleId = roleId,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return subjectId;
    }
}
