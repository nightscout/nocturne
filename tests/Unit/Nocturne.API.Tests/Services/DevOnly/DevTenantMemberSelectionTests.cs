using FluentAssertions;
using Nocturne.API.Services.DevOnly;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.DevOnly;

/// <summary>
/// Which member the dev-only endpoints act on when the caller named nobody.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DevTenantMemberSelectionTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OwnerRoleId = Guid.CreateVersion7();

    [Fact]
    public void PickOwnerOrFirst_skipsARevokedOwner()
    {
        var revoked = Member(isOwner: true, revokedAt: DateTime.UtcNow);
        var owner = Member(isOwner: true);

        DevTenantMemberSelection.PickOwnerOrFirst([revoked, owner], TenantId)
            .Should().BeSameAs(owner);
    }

    [Fact]
    public void PickOwnerOrFirst_skipsAnOwnerOfAnotherTenant()
    {
        var elsewhere = Member(isOwner: true, tenantId: Guid.CreateVersion7());
        var plainMember = Member();

        DevTenantMemberSelection.PickOwnerOrFirst([elsewhere, plainMember], TenantId)
            .Should().BeSameAs(elsewhere, "the first candidate is the fallback when no owner stands");
    }

    /// <summary>Several owners resolve to the longest-standing one on every call.</summary>
    [Fact]
    public void PickOwnerOrFirst_takesTheOldestOwner()
    {
        var newer = Member(isOwner: true, createdAt: new DateTime(2024, 1, 1));
        var older = Member(isOwner: true, createdAt: new DateTime(2020, 1, 1));

        DevTenantMemberSelection.PickOwnerOrFirst([newer, older], TenantId)
            .Should().BeSameAs(older);
    }

    [Fact]
    public void Candidates_dropsDeactivatedAndSystemSubjects()
    {
        var usable = Member();

        DevTenantMemberSelection.Candidates(
            [Member(isActive: false), Member(isSystemSubject: true), usable])
            .Should().Equal(usable);
    }

    private static TenantMemberEntity Member(
        bool isOwner = false,
        bool isActive = true,
        bool isSystemSubject = false,
        DateTime? revokedAt = null,
        DateTime? createdAt = null,
        Guid? tenantId = null) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId ?? TenantId,
        SubjectId = Guid.CreateVersion7(),
        RevokedAt = revokedAt,
        SysCreatedAt = createdAt ?? DateTime.UtcNow,
        Subject = new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = "Member",
            IsActive = isActive,
            IsSystemSubject = isSystemSubject,
        },
        MemberRoles = isOwner
            ?
            [
                new TenantMemberRoleEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantRoleId = OwnerRoleId,
                    TenantRole = new TenantRoleEntity
                    {
                        Id = OwnerRoleId,
                        TenantId = tenantId ?? TenantId,
                        Name = "Owner",
                        Slug = RoleSeeds.Owner,
                        Permissions = [Scope.FullAccess],
                        IsSystem = true,
                    },
                },
            ]
            : [],
    };
}
