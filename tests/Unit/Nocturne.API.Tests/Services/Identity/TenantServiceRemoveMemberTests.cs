using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Chat;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.API.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The two refusals that stop a tenant locking itself out of a live site: the last owner, and the
/// Public system subject whose membership is the share's storage.
/// </summary>
/// <remarks>
/// These guards used to live in the platform-admin <c>TenantController</c>, where the class-level
/// <c>[Authorize(Roles = "platform_admin")]</c> made them effectively unreachable. They now sit in
/// the service, on the hot path for every tenant owner using the members page.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class TenantServiceRemoveMemberTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _ownerRoleId = Guid.CreateVersion7();

    public TenantServiceRemoveMemberTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = Context();
        db.Database.EnsureCreated();
        db.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "test", DisplayName = "Test" });
        db.TenantRoles.Add(new TenantRoleEntity
        {
            Id = _ownerRoleId,
            TenantId = _tenantId,
            Name = "Owner",
            Slug = RoleSeeds.Owner,
            Permissions = [Scope.FullAccess],
            IsSystem = true,
        });
        db.SaveChanges();
    }

    private NocturneDbContext Context() => new(_options) { TenantId = _tenantId };

    private sealed class Factory(DbContextOptions<NocturneDbContext> options, Guid tenantId)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options) { TenantId = tenantId };
    }

    private TenantService Service() => new(
        new Factory(_options, _tenantId),
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        Mock.Of<ILogger<TenantService>>());

    /// <summary>Adds a membership, optionally carrying the owner role or being a system subject.</summary>
    private Guid SeedMember(bool isOwner = false, bool isSystemSubject = false)
    {
        var subjectId = Guid.CreateVersion7();
        using var db = Context();

        db.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = isSystemSubject ? "Public" : "Member",
            IsSystemSubject = isSystemSubject,
        });

        var member = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = subjectId,
        };
        db.TenantMembers.Add(member);

        if (isOwner)
        {
            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantMemberId = member.Id,
                TenantRoleId = _ownerRoleId,
            });
        }

        db.SaveChanges();
        return subjectId;
    }

    [Fact]
    public async Task RemoveMemberAsync_removesAnOrdinaryMember()
    {
        var subjectId = SeedMember();

        var result = await Service().RemoveMemberAsync(_tenantId, subjectId);

        result.Ok.Should().BeTrue();
        await using var db = Context();
        (await db.TenantMembers.AnyAsync(m => m.SubjectId == subjectId)).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveMemberAsync_refusesTheLastOwner()
    {
        var ownerSubjectId = SeedMember(isOwner: true);

        var result = await Service().RemoveMemberAsync(_tenantId, ownerSubjectId);

        result.Ok.Should().BeFalse();
        result.ErrorDescription.Should().Be("Cannot remove the last owner of a tenant");
        await using var db = Context();
        (await db.TenantMembers.AnyAsync(m => m.SubjectId == ownerSubjectId)).Should().BeTrue();
    }

    /// <summary>With a second owner present the tenant keeps an administrator, so removal lands.</summary>
    [Fact]
    public async Task RemoveMemberAsync_removesAnOwnerWhenAnotherRemains()
    {
        var firstOwner = SeedMember(isOwner: true);
        SeedMember(isOwner: true);

        var result = await Service().RemoveMemberAsync(_tenantId, firstOwner);

        result.Ok.Should().BeTrue();
        await using var db = Context();
        (await db.TenantMembers.AnyAsync(m => m.SubjectId == firstOwner)).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveMemberAsync_refusesASystemSubject()
    {
        var publicSubjectId = SeedMember(isSystemSubject: true);

        var result = await Service().RemoveMemberAsync(_tenantId, publicSubjectId);

        result.Ok.Should().BeFalse();
        result.ErrorDescription.Should().Be("Cannot remove system subject memberships");
        await using var db = Context();
        (await db.TenantMembers.AnyAsync(m => m.SubjectId == publicSubjectId)).Should().BeTrue();
    }

    /// <summary>An absent membership is the caller's desired end state, not a refusal.</summary>
    [Fact]
    public async Task RemoveMemberAsync_whenTheMembershipIsAlreadyAbsent_succeeds()
    {
        var result = await Service().RemoveMemberAsync(_tenantId, Guid.CreateVersion7());

        result.Ok.Should().BeTrue();
        result.ErrorDescription.Should().BeNull();
    }

    /// <summary>
    /// The owner count is per tenant, so another tenant's sole owner is not what keeps this one
    /// from removing its own.
    /// </summary>
    [Fact]
    public async Task RemoveMemberAsync_doesNotRemoveAMembershipOfAnotherTenant()
    {
        var otherTenantId = Guid.CreateVersion7();
        var subjectId = Guid.CreateVersion7();

        await using (var db = Context())
        {
            db.Tenants.Add(new TenantEntity { Id = otherTenantId, Slug = "other", DisplayName = "Other" });
            db.Subjects.Add(new SubjectEntity { Id = subjectId, Name = "Elsewhere" });
            db.TenantMembers.Add(new TenantMemberEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = otherTenantId,
                SubjectId = subjectId,
            });
            await db.SaveChangesAsync();
        }

        var result = await Service().RemoveMemberAsync(_tenantId, subjectId);

        result.Ok.Should().BeTrue("an absent membership on this tenant is not a refusal");
        await using var check = Context();
        (await check.TenantMembers.AnyAsync(m => m.SubjectId == subjectId)).Should().BeTrue(
            "the other tenant's membership must be untouched");
    }

    /// <summary>
    /// A chat directory row resolves on platform + platform user alone, with no join to the member
    /// list, so a link left behind would keep answering bot commands for a tenant the person no
    /// longer belongs to. Subjects are global, so only this tenant's link may go.
    /// </summary>
    [Fact]
    public async Task RemoveMemberAsync_removesTheMembersChatLinkForThisTenantOnly()
    {
        var subjectId = SeedMember();
        var otherTenantId = Guid.CreateVersion7();
        var otherSubjectId = SeedMember();

        await using (var db = Context())
        {
            db.Tenants.Add(new TenantEntity { Id = otherTenantId, Slug = "other", DisplayName = "Other" });
            db.ChatIdentityDirectory.AddRange(
                Link("chat-user", _tenantId, subjectId, "here"),
                Link("chat-user", otherTenantId, subjectId, "elsewhere"),
                Link("another-chat-user", _tenantId, otherSubjectId, "someone-else"));
            await db.SaveChangesAsync();
        }

        var result = await Service().RemoveMemberAsync(_tenantId, subjectId);

        result.Ok.Should().BeTrue();
        var directory = new ChatIdentityDirectoryService(
            new Factory(_options, _tenantId), Mock.Of<ILogger<ChatIdentityDirectoryService>>());
        (await directory.GetCandidatesAsync("discord", "chat-user", default))
            .Select(c => c.Label).Should().BeEquivalentTo(["elsewhere"]);
        (await directory.GetCandidatesAsync("discord", "another-chat-user", default))
            .Select(c => c.Label).Should().BeEquivalentTo(["someone-else"]);
    }

    private static ChatIdentityDirectoryEntry Link(
        string platformUserId, Guid tenantId, Guid subjectId, string label) => new()
    {
        Id = Guid.CreateVersion7(),
        Platform = "discord",
        PlatformUserId = platformUserId,
        TenantId = tenantId,
        NocturneUserId = subjectId,
        Label = label,
        DisplayName = label,
        IsActive = true,
    };

    public void Dispose() => _connection.Dispose();
}
