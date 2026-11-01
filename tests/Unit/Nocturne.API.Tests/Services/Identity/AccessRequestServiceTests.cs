using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

[Trait("Category", "Unit")]
public sealed class AccessRequestServiceTests : IDisposable
{
    private static readonly string[] Granter = ["*"];

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<IInAppNotificationService> _notifications = new();
    private readonly Mock<ITenantRoleService> _roles = new();
    private readonly AccessRequestService _service;

    private readonly Guid _tenantId = Guid.CreateVersion7();

    public AccessRequestServiceTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _dbContext = _db.CreateContext(_tenantId);

        _roles
            .Setup(s => s.ValidateRoleGrantAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoleGrantValidation.Valid);

        _service = new AccessRequestService(
            new SubjectRepository(_dbContext.AsFactory()),
            new TenantMembershipRepository(_dbContext.AsFactory()),
            _dbContext.AsFactory(),
            _roles.Object,
            _notifications.Object,
            NullLogger<AccessRequestService>.Instance);

        _dbContext.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "test", DisplayName = "Test" });
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Approve_activatesTheSubjectAndAddsTheMembership()
    {
        var requestorId = await SeedPendingRequestAsync();

        var result = await Approve(requestorId, directPermissions: ["api:*:read"]);

        result.Outcome.Should().Be(ApproveAccessRequestOutcome.Approved);
        var subject = await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == requestorId);
        subject.IsActive.Should().BeTrue();
        subject.ApprovalStatus.Should().Be("Approved");
        (await _dbContext.TenantMembers.AsNoTracking().SingleAsync(m => m.SubjectId == requestorId))
            .DirectPermissions.Should().Equal("api:*:read");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Approve_carriesLimitTo24HoursOntoTheMembership(bool limitTo24Hours)
    {
        var requestorId = await SeedPendingRequestAsync();

        await Approve(requestorId, directPermissions: ["api:*:read"], limitTo24Hours: limitTo24Hours);

        (await _dbContext.TenantMembers.AsNoTracking().SingleAsync(m => m.SubjectId == requestorId))
            .LimitTo24Hours.Should().Be(limitTo24Hours);
    }

    [Fact]
    public async Task Approve_archivesForTheStandingOwnerOnly()
    {
        var owner = await SeedOwnerAsync();
        var deactivated = await SeedOwnerAsync(isActive: false);
        var requestorId = await SeedPendingRequestAsync();

        await Approve(requestorId, directPermissions: ["api:*:read"]);

        ArchivedFor(owner, Times.Once());
        ArchivedFor(deactivated, Times.Never());
    }

    [Fact]
    public async Task Approve_whenTheMembershipCannotBeWritten_leavesTheSubjectPending()
    {
        var owner = await SeedOwnerAsync();
        var requestorId = await SeedPendingRequestAsync();

        var act = () => Approve(requestorId, roleIds: [Guid.CreateVersion7()]);

        await act.Should().ThrowAsync<DbUpdateException>();
        var subject = await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == requestorId);
        subject.IsActive.Should().BeFalse();
        subject.ApprovalStatus.Should().Be("Pending");
        (await _dbContext.TenantMembers.AnyAsync(m => m.SubjectId == requestorId)).Should().BeFalse();
        ArchivedFor(owner, Times.Never());
    }

    [Fact]
    public async Task Approve_whenTheSubjectIsAlreadyAMember_leavesItPending()
    {
        var requestorId = await SeedPendingRequestAsync();
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = requestorId,
        });
        await _dbContext.SaveChangesAsync();

        var result = await Approve(requestorId, directPermissions: ["api:*:read"]);

        result.Outcome.Should().Be(ApproveAccessRequestOutcome.AlreadyMember);
        (await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == requestorId))
            .ApprovalStatus.Should().Be("Pending");
    }

    [Fact]
    public async Task Approve_whenTheRolesExceedTheApprover_writesNothing()
    {
        var requestorId = await SeedPendingRequestAsync();
        var refusal = new RoleGrantValidation(false, RoleGrantValidation.ExceedsGranter, "no");
        _roles
            .Setup(s => s.ValidateRoleGrantAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(refusal);

        var result = await Approve(requestorId, roleIds: [Guid.CreateVersion7()]);

        result.Should().Be(new ApproveAccessRequestResult(ApproveAccessRequestOutcome.RoleGrantRefused, refusal));
        (await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == requestorId))
            .ApprovalStatus.Should().Be("Pending");
        (await _dbContext.TenantMembers.AnyAsync(m => m.SubjectId == requestorId)).Should().BeFalse();
    }

    [Fact]
    public async Task Approve_withNothingToGrant_writesNothing()
    {
        var requestorId = await SeedPendingRequestAsync();

        var result = await Approve(requestorId);

        result.Outcome.Should().Be(ApproveAccessRequestOutcome.NothingGranted);
        (await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == requestorId))
            .ApprovalStatus.Should().Be("Pending");
    }

    [Fact]
    public async Task Approve_ofASubjectThatIsNotPending_isNotFound()
    {
        var requestorId = await SeedPendingRequestAsync(approvalStatus: "Approved");

        (await Approve(requestorId, directPermissions: ["api:*:read"])).Outcome
            .Should().Be(ApproveAccessRequestOutcome.NotFound);
        (await Approve(Guid.CreateVersion7(), directPermissions: ["api:*:read"])).Outcome
            .Should().Be(ApproveAccessRequestOutcome.NotFound);
    }

    [Fact]
    public async Task Approve_withNoTenant_approvesTheSubjectAndJoinsNone()
    {
        var requestorId = await SeedPendingRequestAsync();

        var result = await _service.ApproveAsync(
            requestorId, tenantId: null, [], ["api:*:read"], limitTo24Hours: false, Granter);

        result.Outcome.Should().Be(ApproveAccessRequestOutcome.Approved);
        (await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == requestorId))
            .IsActive.Should().BeTrue();
        (await _dbContext.TenantMembers.AnyAsync(m => m.SubjectId == requestorId)).Should().BeFalse();
    }

    private Task<ApproveAccessRequestResult> Approve(
        Guid subjectId,
        List<Guid>? roleIds = null,
        List<string>? directPermissions = null,
        bool limitTo24Hours = false) =>
        _service.ApproveAsync(subjectId, _tenantId, roleIds ?? [], directPermissions, limitTo24Hours, Granter);

    private void ArchivedFor(Guid subjectId, Times times) =>
        _notifications.Verify(
            s => s.ArchiveBySourceAsync(
                subjectId.ToString(), "passkey.anonymous_login_request", It.IsAny<string>(),
                NotificationArchiveReason.Completed, It.IsAny<CancellationToken>()),
            times);

    private Task<Guid> SeedOwnerAsync(bool isActive = true) =>
        TestDatabaseSeeder.SeedMemberAsync(_dbContext, _tenantId, isActive: isActive);

    private async Task<Guid> SeedPendingRequestAsync(string approvalStatus = "Pending")
    {
        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Requestor",
            IsActive = false,
            ApprovalStatus = approvalStatus,
        });
        await _dbContext.SaveChangesAsync();
        return subjectId;
    }
}
