using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.PlatformAdmin;

/// <summary>
/// Which owners the approve and deny paths archive the pending-request notification for.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AccessRequestControllerTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<IInAppNotificationService> _notifications = new();
    private readonly Mock<ITenantService> _tenantService = new();
    private readonly AccessRequestController _controller;

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _ownerRoleId = Guid.CreateVersion7();

    public AccessRequestControllerTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _dbContext = _db.CreateContext(_tenantId);

        var roleService = new Mock<ITenantRoleService>();
        roleService
            .Setup(s => s.ValidateRoleGrantAsync(
                It.IsAny<Guid>(), It.IsAny<List<Guid>>(), It.IsAny<IReadOnlySet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoleGrantValidation.Valid);

        _controller = new AccessRequestController(
            _dbContext,
            Mock.Of<ISubjectService>(),
            _tenantService.Object,
            roleService.Object,
            MockTenantAccessor.Create(_tenantId).Object,
            _notifications.Object,
            NullLogger<AccessRequestController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test",
        });
        _dbContext.TenantRoles.Add(new TenantRoleEntity
        {
            Id = _ownerRoleId,
            TenantId = _tenantId,
            Name = "Owner",
            Slug = RoleSeeds.Owner,
            Permissions = [Scope.FullAccess],
            IsSystem = true,
        });
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Approve_archivesForTheStandingOwnerOnly()
    {
        var owner = SeedOwner();
        var revoked = SeedOwner(revokedAt: DateTime.UtcNow);
        var deactivated = SeedOwner(isActive: false);
        var requestorId = SeedPendingRequest();

        var result = await _controller.Approve(
            requestorId,
            new ApproveAccessRequestRequest { RoleIds = [_ownerRoleId] },
            CancellationToken.None);

        Assert.IsType<OkResult>(result);
        ArchivedFor(owner, NotificationArchiveReason.Completed, Times.Once());
        ArchivedFor(revoked, NotificationArchiveReason.Completed, Times.Never());
        ArchivedFor(deactivated, NotificationArchiveReason.Completed, Times.Never());
    }

    [Fact]
    public async Task Deny_archivesForTheStandingOwnerOnly()
    {
        var owner = SeedOwner();
        var revoked = SeedOwner(revokedAt: DateTime.UtcNow);
        var system = SeedOwner(isSystemSubject: true);
        var requestorId = SeedPendingRequest();

        var result = await _controller.Deny(requestorId, CancellationToken.None);

        Assert.IsType<OkResult>(result);
        ArchivedFor(owner, NotificationArchiveReason.Dismissed, Times.Once());
        ArchivedFor(revoked, NotificationArchiveReason.Dismissed, Times.Never());
        ArchivedFor(system, NotificationArchiveReason.Dismissed, Times.Never());
    }

    private void ArchivedFor(Guid subjectId, NotificationArchiveReason reason, Times times) =>
        _notifications.Verify(
            s => s.ArchiveBySourceAsync(
                subjectId.ToString(), "passkey.anonymous_login_request", It.IsAny<string>(),
                reason, It.IsAny<CancellationToken>()),
            times);

    private Guid SeedOwner(
        DateTime? revokedAt = null, bool isActive = true, bool isSystemSubject = false)
    {
        var subjectId = Guid.CreateVersion7();
        var memberId = Guid.CreateVersion7();

        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Owner",
            IsActive = isActive,
            IsSystemSubject = isSystemSubject,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = memberId,
            TenantId = _tenantId,
            SubjectId = subjectId,
            RevokedAt = revokedAt,
        });
        _dbContext.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = memberId,
            TenantRoleId = _ownerRoleId,
        });
        _dbContext.SaveChanges();
        return subjectId;
    }

    private Guid SeedPendingRequest()
    {
        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Requestor",
            IsActive = false,
            ApprovalStatus = "Pending",
        });
        _dbContext.SaveChanges();
        return subjectId;
    }
}
