using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Pins which subject the startup bootstrap promotes, and which platform admins it replaces. The owner lookup walks tenants oldest-first
/// and asks each under its own tenant pin, so these tests fix the ordering and the skip-a-tenant-
/// without-an-owner behaviour that the per-tenant scan has to preserve.
/// </summary>
/// <remarks>
/// SQLite has no Row Level Security, so every row is visible regardless of the pin. These tests
/// prove the resolution logic, not that a policy would hide anything.
/// </remarks>
public class PlatformAdminBootstrapServiceTests : IDisposable
{
    private readonly SqliteTestDatabase _sqlite;
    private readonly NocturneDbContext _db;

    public PlatformAdminBootstrapServiceTests()
    {
        _sqlite = TestDbContextFactory.CreateSqlite();

        _db = _sqlite.CreateContext();
    }

    public void Dispose()
    {
        _db.Dispose();
        _sqlite.Dispose();
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
        var existing = await AddSubjectAsync("existing", isPlatformAdmin: true, signsIn: true);
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

    [Fact]
    public async Task ATenantWhoseOnlyOwnerIsDeactivatedIsSkippedForTheNextOldest()
    {
        var deactivated = await AddTenantAsync(
            "deactivated-oldest", new DateTime(2019, 1, 1), RoleSeeds.Owner, subjectIsActive: false);
        var owner = await AddTenantWithOwnerAsync("owned", new DateTime(2020, 1, 1));

        await BootstrapAsync();

        (await IsPlatformAdminAsync(deactivated)).Should().BeFalse();
        (await IsPlatformAdminAsync(owner)).Should().BeTrue();
    }

    [Fact]
    public async Task TheFirstOwnerOfTheSoleTenantIsGrantedOnSetupCompletion()
    {
        var owner = await AddTenantWithOwnerAsync("only", new DateTime(2020, 1, 1));

        (await EnsureFirstOwnerAsync(owner)).Should().BeTrue();
        (await IsPlatformAdminAsync(owner)).Should().BeTrue();
    }

    [Fact]
    public async Task ADeactivatedOwnerOfTheSoleTenantIsNotGrantedOnSetupCompletion()
    {
        var deactivated = await AddTenantAsync(
            "only", new DateTime(2020, 1, 1), RoleSeeds.Owner, subjectIsActive: false);

        (await EnsureFirstOwnerAsync(deactivated)).Should().BeFalse();
        (await IsPlatformAdminAsync(deactivated)).Should().BeFalse();
    }

    [Fact]
    public async Task AnOwnerWhoCannotSignInIsNeverGrantedAndTheNextOldestOwnerWhoCanIs()
    {
        var stranded = await AddTenantWithOwnerAsync("oldest", new DateTime(2019, 1, 1), signsIn: false);
        var capable = await AddTenantWithOwnerAsync("newer", new DateTime(2020, 1, 1));

        await BootstrapAsync();

        (await IsPlatformAdminAsync(stranded)).Should().BeFalse();
        (await IsPlatformAdminAsync(capable)).Should().BeTrue();
    }

    [Fact]
    public async Task AnOwnerWhoCannotSignInIsSkippedForAnotherOwnerOfTheSameTenant()
    {
        var tenantId = await AddTenantRowAsync("shared", new DateTime(2019, 1, 1));
        var stranded = await AddMemberAsync(
            tenantId, RoleSeeds.Owner, "stranded", signsIn: false, joinedAt: new DateTime(2019, 1, 2));
        var capable = await AddMemberAsync(
            tenantId, RoleSeeds.Owner, "capable", joinedAt: new DateTime(2019, 1, 3));

        await BootstrapAsync();

        (await IsPlatformAdminAsync(stranded)).Should().BeFalse();
        (await IsPlatformAdminAsync(capable)).Should().BeTrue();
    }

    [Fact]
    public async Task WhenNoOwnerCanSignIn_NobodyIsGranted()
    {
        var stranded = await AddTenantWithOwnerAsync("only", new DateTime(2020, 1, 1), signsIn: false);

        await BootstrapAsync();

        (await IsPlatformAdminAsync(stranded)).Should().BeFalse();
    }

    [Fact]
    public async Task AConfirmedAddressCountsAsSigningInOnlyWhileALinkRouteIsConfigured()
    {
        var owner = await AddTenantWithOwnerAsync("only", new DateTime(2020, 1, 1), signsIn: false);
        await ConfirmEmailAsync(owner);

        await BootstrapAsync(linkRouteConfigured: false);
        (await IsPlatformAdminAsync(owner)).Should().BeFalse();

        await BootstrapAsync(linkRouteConfigured: true);
        (await IsPlatformAdminAsync(owner)).Should().BeTrue();
    }

    [Fact]
    public async Task WhenEveryAdminCannotSignIn_TheFlagMovesToAnOwnerWhoCan()
    {
        var strandedAdmin = await AddSubjectAsync("stranded-admin", isPlatformAdmin: true);
        var deactivatedAdmin = await AddSubjectAsync(
            "deactivated-admin", isPlatformAdmin: true, signsIn: true, isActive: false);
        var strandedOwner = await AddTenantWithOwnerAsync("oldest", new DateTime(2019, 1, 1), signsIn: false);
        var capable = await AddTenantWithOwnerAsync("newer", new DateTime(2020, 1, 1));

        await BootstrapAsync();

        (await IsPlatformAdminAsync(strandedAdmin)).Should().BeFalse();
        (await IsPlatformAdminAsync(deactivatedAdmin)).Should().BeFalse();
        (await IsPlatformAdminAsync(strandedOwner)).Should().BeFalse();
        (await IsPlatformAdminAsync(capable)).Should().BeTrue();
    }

    [Fact]
    public async Task WhenEveryAdminCannotSignInAndNoOwnerCan_TheAdminIsKept()
    {
        var strandedAdmin = await AddSubjectAsync("stranded-admin", isPlatformAdmin: true);
        var strandedOwner = await AddTenantWithOwnerAsync("only", new DateTime(2020, 1, 1), signsIn: false);

        await BootstrapAsync();

        (await IsPlatformAdminAsync(strandedAdmin)).Should().BeTrue();
        (await IsPlatformAdminAsync(strandedOwner)).Should().BeFalse();
    }

    [Fact]
    public async Task AnAdminWhoCanSignInIsNeverCleared_EvenBesideOneWhoCannot()
    {
        var capableAdmin = await AddSubjectAsync("capable-admin", isPlatformAdmin: true, signsIn: true);
        var strandedAdmin = await AddSubjectAsync("stranded-admin", isPlatformAdmin: true);
        var capableOwner = await AddTenantWithOwnerAsync("only", new DateTime(2020, 1, 1));

        await BootstrapAsync();

        (await IsPlatformAdminAsync(capableAdmin)).Should().BeTrue();
        (await IsPlatformAdminAsync(strandedAdmin)).Should().BeTrue();
        (await IsPlatformAdminAsync(capableOwner)).Should().BeFalse();
    }

    [Fact]
    public async Task ConfiguredAdminSubjectIdsMeanNoHeal()
    {
        var strandedAdmin = await AddSubjectAsync("stranded-admin", isPlatformAdmin: true);
        var configured = await AddSubjectAsync("configured");
        var capableOwner = await AddTenantWithOwnerAsync("only", new DateTime(2020, 1, 1));

        await BootstrapAsync(adminSubjectIds: [configured]);

        (await IsPlatformAdminAsync(strandedAdmin)).Should().BeTrue();
        (await IsPlatformAdminAsync(configured)).Should().BeTrue();
        (await IsPlatformAdminAsync(capableOwner)).Should().BeFalse();
    }

    [Fact]
    public async Task AfterAnAbandonedOwnerStepAndARestart_AnotherSubjectCompletingSetupBecomesPlatformAdmin()
    {
        var tenantId = await AddTenantRowAsync("only", new DateTime(2020, 1, 1));
        var leftover = await AddMemberAsync(tenantId, RoleSeeds.Owner, "leftover", signsIn: false);

        await BootstrapAsync();
        (await IsPlatformAdminAsync(leftover)).Should().BeFalse();

        var completing = await AddMemberAsync(tenantId, RoleSeeds.Owner, "completing");

        (await EnsureFirstOwnerAsync(completing)).Should().BeTrue();
        (await IsPlatformAdminAsync(completing)).Should().BeTrue();
        (await IsPlatformAdminAsync(leftover)).Should().BeFalse();
    }

    private Task<bool> EnsureFirstOwnerAsync(Guid subjectId) =>
        ServiceWith([], AccountLinkRoutes.Off).EnsureFirstOwnerIsPlatformAdminAsync(subjectId, CancellationToken.None);

    private Task BootstrapAsync(List<Guid>? adminSubjectIds = null, bool linkRouteConfigured = false) =>
        ServiceWith(adminSubjectIds ?? [], AccountLinkRoutes.Of(linkRouteConfigured)).BootstrapAsync(CancellationToken.None);

    private PlatformAdminBootstrapService ServiceWith(List<Guid> adminSubjectIds, IAccountLinkRoute linkRoute) =>
        new(
            new TenantRepository(_sqlite.ContextFactory.AsNocturneFactory()),
            new TenantMembershipRepository(_sqlite.ContextFactory.AsNocturneFactory(), linkRoute),
            new SubjectRepository(_sqlite.ContextFactory.AsNocturneFactory(), linkRoute),
            Options.Create(new PlatformOptions { AdminSubjectIds = adminSubjectIds }),
            NullLogger<PlatformAdminBootstrapService>.Instance);

    private async Task<bool> IsPlatformAdminAsync(Guid subjectId) =>
        await _db.Subjects.AsNoTracking()
            .Where(s => s.Id == subjectId)
            .Select(s => s.IsPlatformAdmin)
            .SingleAsync();

    private async Task<Guid> AddSubjectAsync(
        string name, bool isPlatformAdmin = false, bool signsIn = false, bool isActive = true)
    {
        var subject = new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Username = name,
            IsActive = isActive,
            IsPlatformAdmin = isPlatformAdmin,
        };
        _db.Subjects.Add(subject);
        if (signsIn)
            AddPasskey(subject.Id);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return subject.Id;
    }

    private void AddPasskey(Guid subjectId) =>
        _db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = subjectId.ToByteArray(),
            PublicKey = [1],
        });

    /// <summary>Seeds a tenant whose sole member holds the owner role, and returns that subject's id.</summary>
    private async Task<Guid> AddTenantWithOwnerAsync(string slug, DateTime createdAt, bool signsIn = true) =>
        await AddTenantAsync(slug, createdAt, RoleSeeds.Owner, signsIn: signsIn);

    /// <summary>Seeds a tenant whose sole member holds a non-owner role, and returns that subject's id.</summary>
    private async Task<Guid> AddTenantWithoutOwnerAsync(string slug, DateTime createdAt) =>
        await AddTenantAsync(slug, createdAt, RoleSeeds.Viewer);

    private async Task<Guid> AddTenantAsync(
        string slug,
        DateTime createdAt,
        string roleSlug,
        bool subjectIsActive = true,
        bool signsIn = true)
    {
        var tenantId = await AddTenantRowAsync(slug, createdAt);
        return await AddMemberAsync(tenantId, roleSlug, $"{slug}-member", subjectIsActive, signsIn);
    }

    private async Task<Guid> AddTenantRowAsync(string slug, DateTime createdAt)
    {
        var tenantId = Guid.CreateVersion7();
        _db.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = slug,
            DisplayName = slug,
            IsActive = true,
            SysCreatedAt = createdAt,
        });
        await _db.SaveChangesAsync();
        return tenantId;
    }

    private async Task<Guid> AddMemberAsync(
        Guid tenantId, string roleSlug, string name, bool isActive = true, bool signsIn = true, DateTime? joinedAt = null)
    {
        var subjectId = await TestDatabaseSeeder.SeedMemberAsync(
            _db, tenantId, roleSlug, name: name, isActive: isActive, joinedAt: joinedAt);
        if (signsIn)
        {
            AddPasskey(subjectId);
            await _db.SaveChangesAsync();
        }

        _db.ChangeTracker.Clear();
        return subjectId;
    }

    private async Task ConfirmEmailAsync(Guid subjectId)
    {
        var subject = await _db.Subjects.SingleAsync(s => s.Id == subjectId);
        var address = $"{subject.Name}@example.test";
        subject.SetEmail(address);
        subject.ConfirmEmail(address, DateTime.UtcNow);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }
}
