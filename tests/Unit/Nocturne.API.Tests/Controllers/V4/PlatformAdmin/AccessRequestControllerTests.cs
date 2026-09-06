using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
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
    private readonly AccessRequestController _controller;

    private readonly Guid _tenantId = Guid.CreateVersion7();

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
            Mock.Of<ITenantService>(),
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
        var owner = await SeedOwnerAsync();
        var revoked = await SeedOwnerAsync(revokedAt: DateTime.UtcNow);
        var deactivated = await SeedOwnerAsync(isActive: false);
        var requestorId = await SeedPendingRequestAsync();

        var result = await _controller.Approve(
            requestorId,
            new ApproveAccessRequestRequest { DirectPermissions = ["api:*:read"] },
            CancellationToken.None);

        Assert.IsType<OkResult>(result);
        ArchivedFor(owner, NotificationArchiveReason.Completed, Times.Once());
        ArchivedFor(revoked, NotificationArchiveReason.Completed, Times.Never());
        ArchivedFor(deactivated, NotificationArchiveReason.Completed, Times.Never());
    }

    [Fact]
    public async Task Deny_archivesForTheStandingOwnerOnly()
    {
        var owner = await SeedOwnerAsync();
        var revoked = await SeedOwnerAsync(revokedAt: DateTime.UtcNow);
        var system = await SeedOwnerAsync(isSystemSubject: true);
        var requestorId = await SeedPendingRequestAsync();

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

    private Task<Guid> SeedOwnerAsync(
        DateTime? revokedAt = null, bool isActive = true, bool isSystemSubject = false) =>
        TestDatabaseSeeder.SeedMemberAsync(
            _dbContext, _tenantId,
            isActive: isActive, isSystemSubject: isSystemSubject, revokedAt: revokedAt);

    private async Task<Guid> SeedPendingRequestAsync()
    {
        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Requestor",
            IsActive = false,
            ApprovalStatus = "Pending",
        });
        await _dbContext.SaveChangesAsync();
        return subjectId;
    }
}
