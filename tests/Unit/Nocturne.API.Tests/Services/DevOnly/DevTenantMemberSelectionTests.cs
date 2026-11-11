using FluentAssertions;
using Nocturne.API.Services.DevOnly;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
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
    public void PickOwnerOrFirst_takesTheOldestOwner()
    {
        var newer = Member(isOwner: true, createdAt: new DateTime(2024, 1, 1));
        var older = Member(isOwner: true, createdAt: new DateTime(2020, 1, 1));

        DevTenantMemberSelection.PickOwnerOrFirst([newer, older])
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

    private static TenantMemberRecord Member(
        bool isOwner = false,
        bool isActive = true,
        bool isSystemSubject = false,
        DateTime? createdAt = null) => new(
        Id: Guid.CreateVersion7(),
        TenantId: TenantId,
        SubjectId: Guid.CreateVersion7(),
        SubjectName: "Member",
        SubjectUsername: null,
        SubjectIsActive: isActive,
        IsSystemSubject: isSystemSubject,
        IsDemoSubject: false,
        IsPlatformAdmin: false,
        Roles: isOwner
            ? [new TenantMemberRoleRecord(OwnerRoleId, "Owner", RoleSeeds.Owner, [Scope.FullAccess])]
            : [],
        DirectPermissions: null,
        Label: null,
        Username: null,
        LimitTo24Hours: false,
        LastUsedAt: null,
        CreatedFromInviteId: null,
        SysCreatedAt: createdAt ?? DateTime.UtcNow);
}
