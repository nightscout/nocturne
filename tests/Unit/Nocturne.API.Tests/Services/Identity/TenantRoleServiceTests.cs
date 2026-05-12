using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Services.Identity;

public class TenantRoleServiceTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly TenantRoleService _service;
    private readonly Guid _tenantId = Guid.CreateVersion7();

    public TenantRoleServiceTests()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new NocturneDbContext(options);
        _context.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test Tenant",
        });
        _context.SaveChanges();
        _service = new TenantRoleService(_context);
    }

    [Fact]
    public async Task SeedRolesForTenantAsync_CreatesAllSixSeedRoles()
    {
        await _service.SeedRolesForTenantAsync(_tenantId);
        var roles = await _context.TenantRoles.Where(r => r.TenantId == _tenantId).ToListAsync();
        roles.Should().HaveCount(6);
        roles.Should().Contain(r => r.Slug == "owner" && r.IsSystem);
        roles.Should().Contain(r => r.Slug == "admin" && r.IsSystem);
        roles.Should().Contain(r => r.Slug == "caretaker" && r.IsSystem);
        roles.Should().Contain(r => r.Slug == "viewer" && r.IsSystem);
        roles.Should().Contain(r => r.Slug == "clinician" && r.IsSystem);
        roles.Should().Contain(r => r.Slug == "denied" && r.IsSystem);
    }

    [Fact]
    public async Task CreateRoleAsync_CreatesCustomRole()
    {
        var result = await _service.CreateRoleAsync(_tenantId, "School Nurse", "Read-only for school staff", ["glucose.read", "statistics.read"]);
        result.Name.Should().Be("School Nurse");
        result.Slug.Should().Be("school-nurse");
        result.Description.Should().Be("Read-only for school staff");
        result.Permissions.Should().BeEquivalentTo(["glucose.read", "statistics.read"]);
        result.IsSystem.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteRoleAsync_BlocksOwnerDeletion()
    {
        await _service.SeedRolesForTenantAsync(_tenantId);
        var ownerRole = await _context.TenantRoles.FirstAsync(r => r.Slug == "owner" && r.TenantId == _tenantId);
        var result = await _service.DeleteRoleAsync(ownerRole.Id);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("owner_role_protected");
    }

    [Fact]
    public async Task DeleteRoleAsync_RemovesRoleFromMembers()
    {
        await _service.SeedRolesForTenantAsync(_tenantId);
        var followerRole = await _context.TenantRoles.FirstAsync(r => r.Slug == "viewer" && r.TenantId == _tenantId);
        var caretakerRole = await _context.TenantRoles.FirstAsync(r => r.Slug == "caretaker" && r.TenantId == _tenantId);

        var member = new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = Guid.CreateVersion7() };
        _context.TenantMembers.Add(member);
        _context.TenantMemberRoles.AddRange(
            new TenantMemberRoleEntity { Id = Guid.CreateVersion7(), TenantMemberId = member.Id, TenantRoleId = followerRole.Id },
            new TenantMemberRoleEntity { Id = Guid.CreateVersion7(), TenantMemberId = member.Id, TenantRoleId = caretakerRole.Id }
        );
        await _context.SaveChangesAsync();

        var result = await _service.DeleteRoleAsync(followerRole.Id);
        result.Success.Should().BeTrue();

        var remainingRoles = await _context.TenantMemberRoles.Where(mr => mr.TenantMemberId == member.Id).ToListAsync();
        remainingRoles.Should().HaveCount(1);
        remainingRoles[0].TenantRoleId.Should().Be(caretakerRole.Id);
    }

    [Fact]
    public async Task DeleteRoleAsync_BlocksIfMemberWouldHaveZeroPermissions()
    {
        await _service.SeedRolesForTenantAsync(_tenantId);
        var followerRole = await _context.TenantRoles.FirstAsync(r => r.Slug == "viewer" && r.TenantId == _tenantId);

        var member = new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = Guid.CreateVersion7() };
        _context.TenantMembers.Add(member);
        _context.TenantMemberRoles.Add(new TenantMemberRoleEntity { Id = Guid.CreateVersion7(), TenantMemberId = member.Id, TenantRoleId = followerRole.Id });
        await _context.SaveChangesAsync();

        var result = await _service.DeleteRoleAsync(followerRole.Id);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("members_would_lose_all_permissions");
    }

    [Fact]
    public async Task GetEffectivePermissionsAsync_UnionsRolesAndDirectPermissions()
    {
        await _service.SeedRolesForTenantAsync(_tenantId);
        var followerRole = await _context.TenantRoles.FirstAsync(r => r.Slug == "viewer" && r.TenantId == _tenantId);

        var member = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = Guid.CreateVersion7(),
            DirectPermissions = ["treatments.read"],
        };
        _context.TenantMembers.Add(member);
        _context.TenantMemberRoles.Add(new TenantMemberRoleEntity { Id = Guid.CreateVersion7(), TenantMemberId = member.Id, TenantRoleId = followerRole.Id });
        await _context.SaveChangesAsync();

        var effective = await _service.GetEffectivePermissionsAsync(member.Id);
        effective.Should().BeEquivalentTo(["glucose.read", "statistics.read", "treatments.read"]);
    }

    public void Dispose() => _context.Dispose();
}
