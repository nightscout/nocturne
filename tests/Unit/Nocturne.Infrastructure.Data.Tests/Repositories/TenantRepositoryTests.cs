using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="TenantRepository"/> over SQLite: the directory reads a request resolves a tenant
/// from, the settings writes, and the writes staged on a unit of work until its owner commits.
/// </summary>
public class TenantRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public TenantRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "first");
    }

    public void Dispose() => _db.Dispose();

    private TenantRepository Tenants() => new(new TestNocturneDbContextFactory(_db.CreateContext(Guid.Empty)));

    private Task<Guid> CreateAsync(NewTenant tenant) => Tenants().CreateAsync(tenant, CancellationToken.None);

    [Fact]
    public async Task ACreatedTenantReadsBackByIdAndSlugWithTheIdItAsked()
    {
        var wanted = Guid.CreateVersion7();
        var id = await CreateAsync(new NewTenant("acme", "Acme", Id: wanted, AllowPublicDocs: true, OnboardingCompletedAt: Now));

        id.Should().Be(wanted);
        var tenant = await Tenants().GetAsync(id, CancellationToken.None);
        tenant.Should().BeEquivalentTo(new
        {
            Id = wanted, Slug = "acme", DisplayName = "Acme", IsActive = true, IsDemo = false,
            AllowAccessRequests = true, AllowPublicDocs = true, OnboardingCompletedAt = Now, DemoConfig = (DemoTenantConfig?)null,
        });
        (await Tenants().FindBySlugAsync("acme", CancellationToken.None))!.Id.Should().Be(wanted);
        (await Tenants().FindBySlugAsync("nobody", CancellationToken.None)).Should().BeNull();
        (await Tenants().SlugExistsAsync("acme", CancellationToken.None)).Should().BeTrue();
        var dormant = Guid.CreateVersion7();
        _db.SeedTenant(dormant, "dormant", isActive: false);
        (await Tenants().IsActiveAsync(wanted, CancellationToken.None)).Should().BeTrue();
        (await Tenants().IsActiveAsync(dormant, CancellationToken.None)).Should().BeFalse();
        (await Tenants().IsActiveAsync(Guid.CreateVersion7(), CancellationToken.None)).Should().BeFalse();
        (await Tenants().GetAsync(Guid.CreateVersion7(), CancellationToken.None)).Should().BeNull();
        (await Tenants().GetManyAsync([wanted, _tenant, Guid.CreateVersion7()], CancellationToken.None))
            .Select(t => t.Slug).Should().BeEquivalentTo("acme", "first");
        (await Tenants().GetManyAsync([], CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheSoleTenantIsTheOneActiveNonDemoTenantOrNothing()
    {
        (await Tenants().FindSoleTenantAsync(CancellationToken.None))!.Id.Should().Be(_tenant);

        var demo = await CreateAsync(new NewTenant("demo", "Demo", IsDemo: true));
        var inactive = await CreateAsync(new NewTenant("old", "Old", IsActive: false));
        (await Tenants().FindSoleTenantAsync(CancellationToken.None))!.Id.Should().Be(_tenant, "a demo and an inactive tenant are not counted");
        (await Tenants().FindDemoTenantAsync(CancellationToken.None))!.Id.Should().Be(demo);
        (await Tenants().AnyNonDemoAsync(CancellationToken.None)).Should().BeTrue();

        await CreateAsync(new NewTenant("second", "Second"));
        (await Tenants().FindSoleTenantAsync(CancellationToken.None)).Should().BeNull("two active tenants are ambiguous");
        (await Tenants().ListNonDemoAsync(2, CancellationToken.None)).Select(t => t.Slug).Should().Equal("first", "old");
        (await Tenants().ListAsync(activeOnly: true, CancellationToken.None)).Select(t => t.Slug).Should().Equal("first", "demo", "second");
        (await Tenants().ListIdsAsync(activeOnly: false, CancellationToken.None)).Should().Equal(_tenant, demo, inactive, (await Tenants().FindBySlugAsync("second", CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task TheDemoConfigurationComesWithTheTenant()
    {
        var demo = await CreateAsync(new NewTenant("demo", "Demo", IsDemo: true));
        await using (var db = _db.CreateContext())
        {
            db.Set<TenantDemoConfigEntity>().Add(new TenantDemoConfigEntity { TenantId = demo, NextResetAt = Now, BackfillDays = 3 });
            await db.SaveChangesAsync();
        }

        var tenant = await Tenants().FindDemoTenantAsync(CancellationToken.None);
        tenant!.DemoConfig.Should().BeEquivalentTo(new { NextResetAt = Now, BackfillDays = 3, AccessMode = "open", IntervalMinutes = 5 });
        (await Tenants().GetAsync(demo, CancellationToken.None))!.DemoConfig.Should().NotBeNull();
    }

    [Fact]
    public async Task TheDemoConfigurationIsStoredWithANewTenantAndCreatedOrEditedByAnEdit()
    {
        var reset = new DemoTenantConfig(Now, Now, "readonly", 7, 9, 30);
        var created = await CreateAsync(new NewTenant("demo", "Demo", IsDemo: true, DemoConfig: reset));
        (await Tenants().GetAsync(created, CancellationToken.None))!.DemoConfig.Should().BeEquivalentTo(reset);

        (await Tenants().UpdateAsync(created, new TenantEdit(DemoConfig: new DemoConfigEdit(NextResetAt: Now.AddHours(1))), CancellationToken.None))
            .Should().BeTrue();
        (await Tenants().GetAsync(created, CancellationToken.None))!.DemoConfig.Should()
            .BeEquivalentTo(reset with { NextResetAt = Now.AddHours(1) }, "a null member leaves its column as it is");

        (await Tenants().GetAsync(_tenant, CancellationToken.None))!.DemoConfig.Should().BeNull();
        (await Tenants().UpdateAsync(_tenant, new TenantEdit(DemoConfig: new DemoConfigEdit()), CancellationToken.None)).Should().BeTrue();
        (await Tenants().GetAsync(_tenant, CancellationToken.None))!.DemoConfig.Should()
            .BeEquivalentTo(new { NextResetAt = (DateTime?)null, AccessMode = "open", BackfillDays = 90, IntervalMinutes = 5, ResetIntervalMinutes = 0 });
    }

    [Fact]
    public async Task AnEditWritesOnlyTheMembersItNames()
    {
        (await Tenants().UpdateAsync(_tenant, new TenantEdit(DisplayName: "Renamed", PatientRelationship: PatientRelationship.Helper, DefaultGlucoseUnits: "mmol"), CancellationToken.None))
            .Should().BeTrue();
        (await Tenants().UpdateAsync(_tenant, new TenantEdit(IsActive: false, HandedOverAt: Now, SetupStripDismissedRevision: "rev"), CancellationToken.None))
            .Should().BeTrue();

        var tenant = await Tenants().GetAsync(_tenant, CancellationToken.None);
        tenant.Should().BeEquivalentTo(new
        {
            DisplayName = "Renamed", IsActive = false, PatientRelationship = PatientRelationship.Helper,
            DefaultGlucoseUnits = "mmol", HandedOverAt = Now, SetupStripDismissedRevision = "rev", AllowAccessRequests = true,
        });
        (await Tenants().UpdateAsync(Guid.CreateVersion7(), new TenantEdit(DisplayName: "x"), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task TheShareTokenColumnsAreWrittenTogetherAndResolveTheTenant()
    {
        (await Tenants().SetShareTokenAsync(_tenant, new ShareTokenState("hash", "cipher", Now), CancellationToken.None)).Should().BeTrue();

        var tenant = await Tenants().FindByShareTokenHashAsync("hash", CancellationToken.None);
        tenant!.Should().BeEquivalentTo(new { Id = _tenant, ShareTokenHash = "hash", ShareTokenEncrypted = "cipher", ShareTokenSetAt = Now });
        (await Tenants().ShareTokenHashExistsAsync("hash", CancellationToken.None)).Should().BeTrue();
        (await Tenants().ShareTokenHashExistsAsync("other", CancellationToken.None)).Should().BeFalse();

        (await Tenants().RecordShareAccessAsync(_tenant, Now, CancellationToken.None)).Should().BeTrue();
        (await Tenants().GetAsync(_tenant, CancellationToken.None))!.ShareLastAccessedAt.Should().Be(Now);

        (await Tenants().SetShareTokenAsync(_tenant, ShareTokenState.Off, CancellationToken.None)).Should().BeTrue();
        (await Tenants().FindByShareTokenHashAsync("hash", CancellationToken.None)).Should().BeNull();
        (await Tenants().GetAsync(_tenant, CancellationToken.None)).Should().BeEquivalentTo(new
        {
            ShareTokenHash = (string?)null, ShareTokenEncrypted = (string?)null, ShareTokenSetAt = (DateTime?)null,
        });
        (await Tenants().RecordShareAccessAsync(Guid.CreateVersion7(), Now, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ADeletedTenantIsGone()
    {
        var id = await CreateAsync(new NewTenant("gone", "Gone"));
        (await Tenants().DeleteAsync(id, CancellationToken.None)).Should().BeTrue();
        (await Tenants().GetAsync(id, CancellationToken.None)).Should().BeNull();
        (await Tenants().DeleteAsync(id, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task AWriteOnAUnitIsStagedUntilTheOwnerSaves()
    {
        await using var owned = _db.CreateContext(Guid.Empty);
        await using var unit = DbUnitOfWork.Own(owned);
        var tenants = new TenantRepository(unit);

        var id = await tenants.CreateAsync(new NewTenant("staged", "Staged"), CancellationToken.None);
        await tenants.UpdateAsync(_tenant, new TenantEdit(DisplayName: "Also staged"), CancellationToken.None);

        (await Tenants().FindBySlugAsync("staged", CancellationToken.None)).Should().BeNull("the unit has not been saved");
        (await Tenants().GetAsync(_tenant, CancellationToken.None))!.DisplayName.Should().BeEmpty("the unit has not been saved");

        await owned.SaveChangesAsync();
        (await Tenants().GetAsync(id, CancellationToken.None))!.Slug.Should().Be("staged");
        (await Tenants().GetAsync(_tenant, CancellationToken.None))!.DisplayName.Should().Be("Also staged");
        (await tenants.GetAsync(id, CancellationToken.None))!.Slug.Should().Be("staged", "a read on the unit sees its own rows");
    }

    [Fact]
    public async Task TheListsAreOrderedByCreation()
    {
        var later = await CreateAsync(new NewTenant("later", "Later"));
        await using (var db = _db.CreateContext())
        {
            (await db.Tenants.SingleAsync(t => t.Id == later)).SysCreatedAt = Now.AddYears(1);
            (await db.Tenants.SingleAsync(t => t.Id == _tenant)).SysCreatedAt = Now;
            await db.SaveChangesAsync();
        }

        (await Tenants().ListAsync(activeOnly: false, CancellationToken.None)).Select(t => t.Id).Should().Equal(_tenant, later);
    }
}
