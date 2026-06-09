using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

public class MembershipRequestServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<ITenantService> _tenantService;
    private readonly Mock<IInAppNotificationService> _notificationService;
    private readonly MembershipRequestService _service;

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Guid _adminSubjectId = Guid.CreateVersion7();
    private readonly Guid _adminRoleId = Guid.CreateVersion7();

    public MembershipRequestServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(_dbOptions);
        _dbContext.Database.EnsureCreated();

        _tenantService = new Mock<ITenantService>();
        _notificationService = new Mock<IInAppNotificationService>();

        _notificationService
            .Setup(n => n.CreateNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<List<NotificationActionDto>?>(),
                It.IsAny<ResolutionConditions?>(), It.IsAny<Dictionary<string, object>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InAppNotificationDto());

        _service = new MembershipRequestService(
            _dbContext,
            _tenantService.Object,
            _notificationService.Object,
            NullLogger<MembershipRequestService>.Instance);

        SeedData();
    }

    private void SeedData()
    {
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test Tenant",
        });

        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = _subjectId,
            Name = "Requester User",
        });

        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = _adminSubjectId,
            Name = "Admin User",
        });

        // Seed an admin role with members.manage permission
        _dbContext.TenantRoles.Add(new TenantRoleEntity
        {
            Id = _adminRoleId,
            TenantId = _tenantId,
            Name = "Admin",
            Slug = "admin",
            Permissions = [TenantPermissions.MembersManage],
            IsSystem = true,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });

        // Seed an admin tenant member with the admin role
        var memberId = Guid.CreateVersion7();
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = memberId,
            TenantId = _tenantId,
            SubjectId = _adminSubjectId,
            MemberRoles =
            [
                new TenantMemberRoleEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantMemberId = memberId,
                    TenantRoleId = _adminRoleId,
                    SysCreatedAt = DateTime.UtcNow,
                },
            ],
        });

        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    // ──────────────────────────────────────────────
    // CreateRequestAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CreateRequestAsync_HappyPath_ReturnsSuccess()
    {
        var result = await _service.CreateRequestAsync(_tenantId, _subjectId, "Please add me");

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();

        var entity = await _dbContext.MembershipRequests.FirstOrDefaultAsync();
        entity.Should().NotBeNull();
        entity!.SubjectId.Should().Be(_subjectId);
        entity.TenantId.Should().Be(_tenantId);
        entity.Status.Should().Be("pending");
        entity.Message.Should().Be("Please add me");
    }

    [Fact]
    public async Task CreateRequestAsync_DuplicatePending_ReturnsFailure()
    {
        await _service.CreateRequestAsync(_tenantId, _subjectId, "First request");

        var result = await _service.CreateRequestAsync(_tenantId, _subjectId, "Second request");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("pending request already exists");
    }

    [Fact]
    public async Task CreateRequestAsync_SendsNotificationToMembersWithManagePermission()
    {
        await _service.CreateRequestAsync(_tenantId, _subjectId, "Hello");

        _notificationService.Verify(n => n.CreateNotificationAsync(
            _adminSubjectId.ToString(),
            "membership.requested",
            It.Is<string>(s => s.Contains("Requester User")),
            It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            "Hello",
            It.IsAny<string?>(), It.IsAny<List<NotificationActionDto>?>(),
            It.IsAny<ResolutionConditions?>(), It.IsAny<Dictionary<string, object>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ──────────────────────────────────────────────
    // ApproveRequestAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ApproveRequestAsync_HappyPath_SetsApprovedAndAddsMember()
    {
        await _service.CreateRequestAsync(_tenantId, _subjectId, null);
        var request = await _dbContext.MembershipRequests.FirstAsync();
        var roleIds = new List<Guid> { _adminRoleId };

        var result = await _service.ApproveRequestAsync(
            request.Id, _tenantId, roleIds, _adminSubjectId);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();

        // Verify status updated
        await _dbContext.Entry(request).ReloadAsync();
        request.Status.Should().Be("approved");
        request.DecidedBySubjectId.Should().Be(_adminSubjectId);
        request.DecidedAt.Should().NotBeNull();
        request.RoleIds.Should().BeEquivalentTo(roleIds);

        // Verify AddMemberAsync was called
        _tenantService.Verify(t => t.AddMemberAsync(
            _tenantId, _subjectId, roleIds,
            null, null, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApproveRequestAsync_RequestNotFound_ReturnsFailure()
    {
        var result = await _service.ApproveRequestAsync(
            Guid.CreateVersion7(), _tenantId, [_adminRoleId], _adminSubjectId);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task ApproveRequestAsync_AlreadyDecided_ReturnsFailure()
    {
        await _service.CreateRequestAsync(_tenantId, _subjectId, null);
        var request = await _dbContext.MembershipRequests.FirstAsync();

        // Deny it first
        await _service.DenyRequestAsync(request.Id, _tenantId, _adminSubjectId);

        // Try to approve the already-denied request
        var result = await _service.ApproveRequestAsync(
            request.Id, _tenantId, [_adminRoleId], _adminSubjectId);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("no longer pending");
    }

    // ──────────────────────────────────────────────
    // DenyRequestAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DenyRequestAsync_HappyPath_SetsDenied()
    {
        await _service.CreateRequestAsync(_tenantId, _subjectId, null);
        var request = await _dbContext.MembershipRequests.FirstAsync();

        var result = await _service.DenyRequestAsync(
            request.Id, _tenantId, _adminSubjectId);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();

        await _dbContext.Entry(request).ReloadAsync();
        request.Status.Should().Be("denied");
        request.DecidedBySubjectId.Should().Be(_adminSubjectId);
        request.DecidedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DenyRequestAsync_RequestNotFound_ReturnsFailure()
    {
        var result = await _service.DenyRequestAsync(
            Guid.CreateVersion7(), _tenantId, _adminSubjectId);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    // ──────────────────────────────────────────────
    // GetMyRequestAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetMyRequestAsync_ReturnsMostRecentRequest()
    {
        // Create first request, then deny it so a second pending can be created
        await _service.CreateRequestAsync(_tenantId, _subjectId, "First");
        var first = await _dbContext.MembershipRequests.FirstAsync();
        first.CreatedAt = DateTime.UtcNow.AddDays(-1);
        await _dbContext.SaveChangesAsync();

        // Deny so that duplicate check passes
        await _service.DenyRequestAsync(first.Id, _tenantId, _adminSubjectId);

        await _service.CreateRequestAsync(_tenantId, _subjectId, "Second");

        var result = await _service.GetMyRequestAsync(_tenantId, _subjectId);

        result.Should().NotBeNull();
        result!.Message.Should().Be("Second");
        result.Status.Should().Be("pending");
        result.SubjectName.Should().Be("Requester User");
    }

    // ──────────────────────────────────────────────
    // GetPendingRequestsAsync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetPendingRequestsAsync_ReturnsOnlyPendingRequests()
    {
        // Create two requests from different subjects
        var otherSubjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = otherSubjectId,
            Name = "Other User",
        });
        await _dbContext.SaveChangesAsync();

        await _service.CreateRequestAsync(_tenantId, _subjectId, "Pending one");
        await _service.CreateRequestAsync(_tenantId, otherSubjectId, "Pending two");

        // Deny the first request
        var first = await _dbContext.MembershipRequests
            .FirstAsync(r => r.SubjectId == _subjectId);
        await _service.DenyRequestAsync(first.Id, _tenantId, _adminSubjectId);

        var pending = await _service.GetPendingRequestsAsync(_tenantId);

        pending.Should().HaveCount(1);
        pending[0].SubjectId.Should().Be(otherSubjectId);
        pending[0].SubjectName.Should().Be("Other User");
        pending[0].Status.Should().Be("pending");
    }

    // ──────────────────────────────────────────────
    // Allow-requests settings
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetAllowRequestsAsync_DefaultsToTrue()
    {
        (await _service.GetAllowRequestsAsync(_tenantId)).Should().BeTrue();
    }

    [Fact]
    public async Task SetAllowRequestsAsync_TogglesTheTenantFlag()
    {
        (await _service.SetAllowRequestsAsync(_tenantId, false)).Should().BeFalse();
        (await _service.GetAllowRequestsAsync(_tenantId)).Should().BeFalse();

        (await _service.SetAllowRequestsAsync(_tenantId, true)).Should().BeTrue();
        (await _service.GetAllowRequestsAsync(_tenantId)).Should().BeTrue();
    }

    [Fact]
    public async Task CreateRequestAsync_WhenRequestsDisabled_ReturnsFailureAndPersistsNothing()
    {
        await _service.SetAllowRequestsAsync(_tenantId, false);

        var result = await _service.CreateRequestAsync(_tenantId, _subjectId, "Please add me");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not accepting");
        (await _dbContext.MembershipRequests.AnyAsync()).Should().BeFalse();
    }
}
