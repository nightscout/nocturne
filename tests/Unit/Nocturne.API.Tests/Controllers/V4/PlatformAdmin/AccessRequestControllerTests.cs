using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Controllers.V4.PlatformAdmin;

/// <summary>
/// How the approve action answers what the service reports, and which owners the deny path
/// archives the pending-request notification for.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AccessRequestControllerTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<IInAppNotificationService> _notifications = new();
    private readonly Mock<IAccessRequestService> _accessRequests = new();
    private readonly Mock<ISubjectService> _subjectService = new();
    private readonly AccessRequestController _controller;

    private readonly Guid _tenantId = Guid.CreateVersion7();

    public AccessRequestControllerTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _dbContext = _db.CreateContext(_tenantId);

        _controller = new AccessRequestController(
            new TenantMembershipRepository(_dbContext.AsFactory()),
            new SubjectRepository(_dbContext.AsFactory()),
            _subjectService.Object,
            _accessRequests.Object,
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

    [Theory]
    [InlineData(ApproveAccessRequestOutcome.Approved, typeof(OkResult), null)]
    [InlineData(ApproveAccessRequestOutcome.NotFound, typeof(NotFoundResult), null)]
    [InlineData(ApproveAccessRequestOutcome.NothingGranted, typeof(ObjectResult), 400)]
    [InlineData(ApproveAccessRequestOutcome.AlreadyMember, typeof(ObjectResult), 409)]
    public async Task Approve_answersWhatTheServiceReports(
        ApproveAccessRequestOutcome outcome, Type resultType, int? status)
    {
        SetupApprove(new ApproveAccessRequestResult(outcome));

        var result = await _controller.Approve(
            Guid.CreateVersion7(), new ApproveAccessRequestRequest(), CancellationToken.None);

        result.Should().BeOfType(resultType);
        if (status is { } code)
            ((ObjectResult)result).StatusCode.Should().Be(code);
    }

    [Theory]
    [InlineData(RoleGrantValidation.ForeignRole, 400)]
    [InlineData(RoleGrantValidation.ExceedsGranter, 403)]
    public async Task Approve_answersARefusedRoleGrantByItsCode(string code, int status)
    {
        SetupApprove(new ApproveAccessRequestResult(
            ApproveAccessRequestOutcome.RoleGrantRefused, new RoleGrantValidation(false, code, "refused")));

        var result = await _controller.Approve(
            Guid.CreateVersion7(), new ApproveAccessRequestRequest(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Approve_passesTheRequestAndTheResolvedTenantOn(bool limitTo24Hours)
    {
        var subjectId = Guid.CreateVersion7();
        SetupApprove(new ApproveAccessRequestResult(ApproveAccessRequestOutcome.Approved));

        await _controller.Approve(
            subjectId,
            new ApproveAccessRequestRequest { DirectPermissions = ["api:*:read"], LimitTo24Hours = limitTo24Hours },
            CancellationToken.None);

        _accessRequests.Verify(
            s => s.ApproveAsync(
                subjectId, _tenantId, It.IsAny<List<Guid>>(), It.Is<List<string>?>(p => p!.SequenceEqual(new[] { "api:*:read" })),
                limitTo24Hours, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    private void SetupApprove(ApproveAccessRequestResult result) =>
        _accessRequests
            .Setup(s => s.ApproveAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<List<Guid>>(), It.IsAny<List<string>?>(),
                It.IsAny<bool>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Fact]
    public async Task Deny_archivesForTheStandingOwnerOnly()
    {
        var owner = await SeedOwnerAsync();
        var system = await SeedOwnerAsync(isSystemSubject: true);
        var requestorId = await SeedPendingRequestAsync();

        var result = await _controller.Deny(requestorId, CancellationToken.None);

        Assert.IsType<OkResult>(result);
        ArchivedFor(owner, NotificationArchiveReason.Dismissed, Times.Once());
        ArchivedFor(system, NotificationArchiveReason.Dismissed, Times.Never());
    }

    [Fact]
    public async Task Deny_isRefusedWithAConflictNamingTheTenantsWhenTheSubjectIsTheirLastOwner()
    {
        var requestorId = await SeedPendingRequestAsync();
        _subjectService
            .Setup(s => s.DeleteSubjectAsync(requestorId))
            .ReturnsAsync(new SubjectChange.LastOwnerOf([new LastOwnerTenant(_tenantId, "test"), new LastOwnerTenant(Guid.CreateVersion7(), "other")]));

        var result = await _controller.Deny(requestorId, CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Contain("test").And.Contain("other");
    }

    private void ArchivedFor(Guid subjectId, NotificationArchiveReason reason, Times times) =>
        _notifications.Verify(
            s => s.ArchiveBySourceAsync(
                subjectId.ToString(), "passkey.anonymous_login_request", It.IsAny<string>(),
                reason, It.IsAny<CancellationToken>()),
            times);

    private Task<Guid> SeedOwnerAsync(
        bool isActive = true, bool isSystemSubject = false) =>
        TestDatabaseSeeder.SeedMemberAsync(
            _dbContext, _tenantId,
            isActive: isActive, isSystemSubject: isSystemSubject);

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
