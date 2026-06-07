using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

public sealed class ShareLinkServiceTests : IDisposable
{
    private static readonly Guid TenantId = TestDatabaseSeeder.TenantId;

    private readonly NocturneDbContext _db;
    private readonly ShareLinkService _service;

    public ShareLinkServiceTests()
    {
        var dbName = $"sharelink_{Guid.NewGuid()}";
        _db = TestDbContextFactory.CreateInMemoryContext(dbName);
        TestDatabaseSeeder.Seed(_db);

        // The seeder grants the Public subject the Clinician role; keep a Viewer role available too
        // so the legacy role-based link scenario can be exercised.
        _db.TenantRoles.Add(new TenantRoleEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Name = "Viewer",
            Slug = TenantPermissions.SeedRoles.Viewer,
            Permissions = TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Viewer],
            IsSystem = true,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        var factory = new Mock<IDbContextFactory<NocturneDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TestDbContextFactory.CreateInMemoryContext(dbName));

        _service = new ShareLinkService(
            _db,
            new ShareTokenGenerator(),
            new ShareTokenCacheService(new MemoryCache(new MemoryCacheOptions()), factory.Object),
            new PublicAccessCacheService(
                new MemoryCache(new MemoryCacheOptions()), factory.Object, NullLogger<PublicAccessCacheService>.Instance),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.run" }));
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Loads the Public subject's membership with its role assignments.</summary>
    private Task<TenantMemberEntity> GetPublicMemberAsync() =>
        _db.TenantMembers.AsNoTracking().Include(m => m.MemberRoles)
            .FirstAsync(m => m.TenantId == TenantId && m.Subject!.Name == "Public");

    /// <summary>Strips the seeded role/scope grant so the Public subject mirrors a fresh tenant.</summary>
    private async Task ResetPublicMemberAccessAsync()
    {
        var member = await _db.TenantMembers.Include(m => m.MemberRoles)
            .FirstAsync(m => m.TenantId == TenantId && m.Subject!.Name == "Public");
        _db.TenantMemberRoles.RemoveRange(member.MemberRoles);
        member.MemberRoles.Clear();
        member.DirectPermissions = null;
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Rotate_enables_sharing_and_returns_a_url()
    {
        var dto = await _service.RotateAsync(TenantId);

        dto.Enabled.Should().BeTrue();
        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == TenantId);
        tenant.ShareToken.Should().NotBeNullOrEmpty();
        dto.Url.Should().Be($"https://{tenant.ShareToken}.share.nocturne.run");
    }

    [Fact]
    public async Task Rotate_seeds_default_scopes_as_direct_permissions_on_first_enable()
    {
        await ResetPublicMemberAccessAsync();

        var dto = await _service.RotateAsync(TenantId);

        dto.Scopes.Should().BeEquivalentTo(TenantPermissions.DefaultPublicShareScopes);

        var member = await GetPublicMemberAsync();
        member.DirectPermissions.Should().BeEquivalentTo(TenantPermissions.DefaultPublicShareScopes);
        member.MemberRoles.Should().BeEmpty("rotation seeds direct permissions rather than a role");
    }

    [Fact]
    public async Task Rotate_changes_the_token_each_time()
    {
        var first = (await _service.RotateAsync(TenantId)).Url;
        var second = (await _service.RotateAsync(TenantId)).Url;

        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Disable_clears_the_token_roles_and_scopes()
    {
        await _service.RotateAsync(TenantId);

        var dto = await _service.DisableAsync(TenantId);

        dto.Enabled.Should().BeFalse();
        dto.Url.Should().BeNull();
        dto.Scopes.Should().BeEmpty();

        var tenant = await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == TenantId);
        tenant.ShareToken.Should().BeNull();

        var member = await GetPublicMemberAsync();
        member.MemberRoles.Should().BeEmpty();
        (member.DirectPermissions ?? []).Should().BeEmpty();
    }

    [Fact]
    public async Task SetFullHistory_toggles_the_24_hour_limit()
    {
        await _service.RotateAsync(TenantId); // defaults to 24h

        (await _service.SetFullHistoryAsync(TenantId, fullHistory: true)).FullHistory.Should().BeTrue();
        (await _service.SetFullHistoryAsync(TenantId, fullHistory: false)).FullHistory.Should().BeFalse();
    }

    [Fact]
    public async Task Get_reflects_disabled_state_by_default()
    {
        var dto = await _service.GetAsync(TenantId);

        dto.Enabled.Should().BeFalse();
        dto.Url.Should().BeNull();
    }

    [Fact]
    public async Task Get_reports_role_derived_scopes_for_the_public_member()
    {
        // The seeder grants the Public subject the Clinician role; its read atoms must surface as
        // the current public scopes so legacy (role-based) links keep working.
        var dto = await _service.GetAsync(TenantId);

        dto.Scopes.Should().Contain([
            TenantPermissions.GlucoseRead,
            TenantPermissions.StatisticsRead,
            TenantPermissions.TreatmentsRead,
        ]);
        dto.Scopes.Should().OnlyContain(s => TenantPermissions.PublicShareScopes.Contains(s));
    }

    [Fact]
    public async Task Rerotate_preserves_the_full_history_choice()
    {
        await _service.RotateAsync(TenantId);
        await _service.SetFullHistoryAsync(TenantId, fullHistory: true);

        var dto = await _service.RotateAsync(TenantId);

        dto.FullHistory.Should().BeTrue("re-rotation must not reset the owner's full-history choice");
    }

    [Fact]
    public async Task Rerotate_preserves_the_chosen_scopes()
    {
        await ResetPublicMemberAccessAsync();
        await _service.RotateAsync(TenantId);
        await _service.SetScopesAsync(TenantId, [TenantPermissions.GlucoseRead]);

        var dto = await _service.RotateAsync(TenantId);

        dto.Scopes.Should().BeEquivalentTo([TenantPermissions.GlucoseRead]);
    }

    [Fact]
    public async Task SetScopes_replaces_direct_permissions_and_drops_role_grants()
    {
        // Start from the seeded Clinician-role link, then choose explicit scopes.
        await _service.RotateAsync(TenantId);

        var dto = await _service.SetScopesAsync(TenantId,
            [TenantPermissions.GlucoseRead, TenantPermissions.TreatmentsRead]);

        dto.Scopes.Should().BeEquivalentTo([TenantPermissions.GlucoseRead, TenantPermissions.TreatmentsRead]);

        var member = await GetPublicMemberAsync();
        member.MemberRoles.Should().BeEmpty("choosing scopes migrates the link onto direct permissions");
        member.DirectPermissions.Should().BeEquivalentTo([TenantPermissions.GlucoseRead, TenantPermissions.TreatmentsRead]);
    }

    [Fact]
    public async Task SetScopes_allows_an_empty_list_while_the_link_stays_live()
    {
        await _service.RotateAsync(TenantId);

        var dto = await _service.SetScopesAsync(TenantId, []);

        dto.Enabled.Should().BeTrue("the link is live via its token, independent of shared scopes");
        dto.Scopes.Should().BeEmpty();

        var member = await GetPublicMemberAsync();
        (member.DirectPermissions ?? []).Should().BeEmpty();
        member.MemberRoles.Should().BeEmpty();
    }

    [Fact]
    public async Task SetScopes_rejects_scopes_outside_the_public_allow_list()
    {
        await _service.RotateAsync(TenantId);

        var setReadWrite = async () => await _service.SetScopesAsync(TenantId, [TenantPermissions.GlucoseReadWrite]);
        var setAdmin = async () => await _service.SetScopesAsync(TenantId, [TenantPermissions.MembersManage]);

        await setReadWrite.Should().ThrowAsync<ArgumentException>();
        await setAdmin.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Disable_without_a_public_member_still_clears_the_token()
    {
        await _service.RotateAsync(TenantId);

        // Remove the Public membership entirely, then disable.
        var publicMember = await _db.TenantMembers.Include(m => m.MemberRoles)
            .FirstAsync(m => m.TenantId == TenantId && m.Subject!.Name == "Public");
        _db.TenantMemberRoles.RemoveRange(publicMember.MemberRoles);
        _db.TenantMembers.Remove(publicMember);
        await _db.SaveChangesAsync();

        var dto = await _service.DisableAsync(TenantId); // must not throw

        dto.Enabled.Should().BeFalse();
        (await _db.Tenants.AsNoTracking().FirstAsync(t => t.Id == TenantId)).ShareToken.Should().BeNull();
    }
}
