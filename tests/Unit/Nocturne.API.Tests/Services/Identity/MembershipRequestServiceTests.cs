using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Nocturne.API.Services.Auth;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Services.Identity;

public class MembershipRequestServiceTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<IInAppNotificationService> _notificationService;
    private readonly Mock<IAuthAuditService> _audit = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly MembershipRequestService _service;

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Guid _adminSubjectId = Guid.CreateVersion7();
    private readonly Guid _adminRoleId = Guid.CreateVersion7();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));

    public MembershipRequestServiceTests()
    {
        _db = TestDbContextFactory.CreateSqlite();

        // The service always works for the request's own tenant.
        _dbContext = _db.CreateContext(_tenantId);

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
            new TenantRepository(_dbContext.AsFactory()),
            new TenantMembershipRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off),
            new SubjectRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off),
            _dbContext.AsFactory(),
            new TenantRoleService(new TenantMembershipRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off), _time, Moq.Mock.Of<Nocturne.Core.Contracts.Identity.IMemberInviteRepository>(), new Nocturne.Infrastructure.Data.Repositories.TeamRepository(_dbContext.AsFactory()), _dbContext.AsFactory(), new Nocturne.API.Services.Auth.GrantRevocationService(new Nocturne.API.Services.Auth.GuestSessionCacheService(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())))),
            _notificationService.Object,
            _audit.Object,
            _time,
            NullLogger<MembershipRequestService>.Instance, AccountLinkRoutes.Off,
            new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))), InertTeamStaff.Announcer(_teams));

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
            Permissions = [Scope.MembersManage],
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
        _db.Dispose();
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
    public async Task CreateRequestAsync_DuplicatePending_IsRefusedAndFilesNoSecondRow()
    {
        await _service.CreateRequestAsync(_tenantId, _subjectId, "First request");

        var result = await _service.CreateRequestAsync(_tenantId, _subjectId, "Second request");

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(CreateMembershipRequestOutcome.AlreadyPending);
        result.Error.Should().Contain("pending request already exists");
        (await _dbContext.MembershipRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateRequestAsync_FromAnExistingMember_IsRefusedAsAConflictAndFilesNothing()
    {
        var result = await _service.CreateRequestAsync(_tenantId, _adminSubjectId, "Let me in");

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(CreateMembershipRequestOutcome.AlreadyMember);
        (await _dbContext.MembershipRequests.AnyAsync()).Should().BeFalse();
        NotifiedSubjectIds().Should().BeEmpty();
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

    [Fact]
    public async Task CreateRequestAsync_NotifiesExactlyTheActiveNonSystemMembersWhoCanApprove()
    {
        var direct = AddMember(directPermissions: [Scope.MembersManage]);
        var viaWildcardRole = AddMember(rolePermissions: [Scope.FullAccess]);
        AddMember(rolePermissions: [Scope.GlucoseRead], directPermissions: [Scope.MembersInvite]);
        AddMember();
        AddMember(rolePermissions: [Scope.FullAccess], isSystemSubject: true);
        AddMember(directPermissions: [Scope.MembersManage], isActive: false);

        await _service.CreateRequestAsync(_tenantId, _subjectId, null);

        NotifiedSubjectIds().Should().BeEquivalentTo(
            [_adminSubjectId.ToString(), direct.ToString(), viaWildcardRole.ToString()]);
    }

    private Guid AddMember(
        string[]? rolePermissions = null,
        string[]? directPermissions = null,
        bool isSystemSubject = false,
        bool isActive = true)
    {
        var subjectId = Guid.CreateVersion7();
        var memberId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = $"Member {subjectId}",
            IsSystemSubject = isSystemSubject,
            IsActive = isActive,
        });

        var member = new TenantMemberEntity
        {
            Id = memberId,
            TenantId = _tenantId,
            SubjectId = subjectId,
            DirectPermissions = directPermissions?.ToList(),
        };

        if (rolePermissions != null)
        {
            var roleId = Guid.CreateVersion7();
            _dbContext.TenantRoles.Add(new TenantRoleEntity
            {
                Id = roleId,
                TenantId = _tenantId,
                Name = $"Role {roleId}",
                Slug = $"role-{roleId}",
                Permissions = rolePermissions.ToList(),
                SysCreatedAt = DateTime.UtcNow,
                SysUpdatedAt = DateTime.UtcNow,
            });
            member.MemberRoles =
            [
                new TenantMemberRoleEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantMemberId = memberId,
                    TenantRoleId = roleId,
                    SysCreatedAt = DateTime.UtcNow,
                },
            ];
        }

        _dbContext.TenantMembers.Add(member);
        _dbContext.SaveChanges();
        return subjectId;
    }

    private IReadOnlyList<string> NotifiedSubjectIds() =>
        _notificationService.Invocations
            .Where(i => i.Method.Name == nameof(IInAppNotificationService.CreateNotificationAsync)
                && (string)i.Arguments[1]! == "membership.requested")
            .Select(i => (string)i.Arguments[0]!)
            .ToList();

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
            request.Id, _tenantId, roleIds, _adminSubjectId, [Scope.FullAccess], null);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();

        // Verify status updated
        await _dbContext.Entry(request).ReloadAsync();
        request.Status.Should().Be("approved");
        request.DecidedBySubjectId.Should().Be(_adminSubjectId);
        request.DecidedAt.Should().NotBeNull();
        request.RoleIds.Should().BeEquivalentTo(roleIds);

        var member = await _dbContext.TenantMembers
            .Include(m => m.MemberRoles)
            .SingleAsync(m => m.TenantId == _tenantId && m.SubjectId == _subjectId);
        member.LimitTo24Hours.Should().BeFalse();
        member.MemberRoles.Select(r => r.TenantRoleId).Should().BeEquivalentTo(roleIds);
        ApprovalNotifications().Should().ContainSingle();
    }

    [Fact]
    public async Task ApproveRequestAsync_recountsTheTeamStaffOfTheSiteAndARefusalDoesNot()
    {
        await _service.CreateRequestAsync(_tenantId, _subjectId, null);
        var request = await _dbContext.MembershipRequests.FirstAsync();

        (await _service.ApproveRequestAsync(request.Id, _tenantId, [_adminRoleId], _adminSubjectId, [Scope.GlucoseRead], null))
            .Success.Should().BeFalse("the role exceeds what the approver holds");
        _teams.Verify(t => t.ListAccessOfTenantAsync(_tenantId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never());

        (await _service.ApproveRequestAsync(request.Id, _tenantId, [_adminRoleId], _adminSubjectId, [Scope.FullAccess], null))
            .Success.Should().BeTrue();

        _teams.Verify(t => t.ListAccessOfTenantAsync(_tenantId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ApproveRequestAsync_forASubjectWhoseMembershipHasEnded_replacesItWithOneMembershipCarryingTheNewEnd()
    {
        var endedId = Guid.CreateVersion7();
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = endedId, TenantId = _tenantId, SubjectId = _subjectId, AccessEndsAt = _time.GetUtcNow().UtcDateTime.AddSeconds(-1),
        });
        await _dbContext.SaveChangesAsync();
        (await _service.CreateRequestAsync(_tenantId, _subjectId, null)).Outcome
            .Should().Be(CreateMembershipRequestOutcome.Created, "an ended membership is no membership");
        var request = await _dbContext.MembershipRequests.FirstAsync();
        var end = _time.GetUtcNow().UtcDateTime.AddDays(14);

        var result = await _service.ApproveRequestAsync(
            request.Id, _tenantId, [_adminRoleId], _adminSubjectId, [Scope.FullAccess], accessEndsAt: end);

        result.Success.Should().BeTrue();
        var rows = await _dbContext.TenantMembers.AsNoTracking().Where(m => m.SubjectId == _subjectId).ToListAsync();
        rows.Should().ContainSingle().Which.Should().Match<TenantMemberEntity>(m => m.Id != endedId && m.AccessEndsAt == end);
    }

    [Fact]
    public async Task ApproveRequestAsync_refusesTheOwnerRoleWithAnEnd()
    {
        var ownerRole = Guid.CreateVersion7();
        _dbContext.TenantRoles.Add(new TenantRoleEntity
        {
            Id = ownerRole, TenantId = _tenantId, Name = "Owner", Slug = RoleSeeds.Owner, Permissions = [Scope.FullAccess], IsSystem = true,
        });
        await _dbContext.SaveChangesAsync();
        await _service.CreateRequestAsync(_tenantId, _subjectId, null);
        var request = await _dbContext.MembershipRequests.FirstAsync();

        var result = await _service.ApproveRequestAsync(
            request.Id, _tenantId, [ownerRole], _adminSubjectId, [Scope.FullAccess], accessEndsAt: _time.GetUtcNow().UtcDateTime.AddDays(1));

        result.Error.Should().Be(AccessEndRequest.OwnerDetail);
        (await _dbContext.TenantMembers.AnyAsync(m => m.SubjectId == _subjectId)).Should().BeFalse();
    }

    [Fact]
    public async Task ApproveRequestAsync_SubjectAlreadyAMember_FailsAndLeavesTheRequestPending()
    {
        await _service.CreateRequestAsync(_tenantId, _subjectId, null);
        var request = await _dbContext.MembershipRequests.FirstAsync();
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = _subjectId,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.ApproveRequestAsync(
            request.Id, _tenantId, [_adminRoleId], _adminSubjectId, [Scope.FullAccess], null);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        await _dbContext.Entry(request).ReloadAsync();
        request.Status.Should().Be("pending");
        request.DecidedBySubjectId.Should().BeNull();
        request.DecidedAt.Should().BeNull();
        (await _dbContext.TenantMembers.CountAsync(m => m.SubjectId == _subjectId)).Should().Be(1);
        ApprovalNotifications().Should().BeEmpty();
    }

    [Fact]
    public async Task ApproveRequestAsync_AuditsEachRoleAndTheClampAgainstTheRequester_AttributedToTheApprover()
    {
        _dbContext.ActingAs(new TestAuditActor { SubjectId = _adminSubjectId });
        await _service.CreateRequestAsync(_tenantId, _subjectId, null);
        var request = await _dbContext.MembershipRequests.FirstAsync();

        await _service.ApproveRequestAsync(
            request.Id, _tenantId, [_adminRoleId], _adminSubjectId, [Scope.FullAccess], null, limitTo24Hours: true);

        var rows = await _dbContext.AuthAuditLog.AsNoTracking().Where(a => a.TargetSubjectId == _subjectId).ToListAsync();
        rows.Should().OnlyContain(a => a.ActorSubjectId == _adminSubjectId && a.TenantId == _tenantId);
        rows.Select(a => (a.EventType, a.DetailsJson)).Should().BeEquivalentTo(new[]
        {
            (AuthAuditEventType.MemberJoined, $$"""{"kind":"tenant","how":"request","by":"{{_adminSubjectId}}"}"""),
            (AuthAuditEventType.RoleAssigned, """{"role":"admin","kind":"tenant"}"""),
            (AuthAuditEventType.HistoryLimitChanged, """{"history_limit":"24_hours","kind":"tenant"}"""),
        });
    }

    [Fact]
    public async Task ApproveRequestAsync_WhenRefused_AuditsNothing()
    {
        await _service.CreateRequestAsync(_tenantId, _subjectId, null);
        var request = await _dbContext.MembershipRequests.FirstAsync();
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = _subjectId,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.ApproveRequestAsync(
            request.Id, _tenantId, [_adminRoleId], _adminSubjectId, [Scope.FullAccess], null, limitTo24Hours: true);

        result.Success.Should().BeFalse();
        (await _dbContext.AuthAuditLog.AsNoTracking().CountAsync(a => a.TargetSubjectId == _subjectId)).Should().Be(0);
    }

    private IReadOnlyList<IInvocation> ApprovalNotifications() =>
        _notificationService.Invocations
            .Where(i => i.Method.Name == nameof(IInAppNotificationService.CreateNotificationAsync)
                && (string)i.Arguments[1]! == "membership.approved")
            .ToList();

    [Fact]
    public async Task ApproveRequestAsync_ByAClampedApprover_AddsAClampedMember()
    {
        await _service.CreateRequestAsync(_tenantId, _subjectId, null);
        var request = await _dbContext.MembershipRequests.FirstAsync();
        var roleIds = new List<Guid> { _adminRoleId };

        var result = await _service.ApproveRequestAsync(
            request.Id, _tenantId, roleIds, _adminSubjectId, [Scope.FullAccess], null, limitTo24Hours: true);

        result.Success.Should().BeTrue();
        (await _dbContext.TenantMembers.SingleAsync(m => m.TenantId == _tenantId && m.SubjectId == _subjectId))
            .LimitTo24Hours.Should().BeTrue();
    }

    [Fact]
    public async Task ApproveRequestAsync_ClampedIntoAnAdministeringRole_IsRefused()
    {
        var settingsRoleId = Guid.CreateVersion7();
        _dbContext.TenantRoles.Add(new TenantRoleEntity
        {
            Id = settingsRoleId,
            TenantId = _tenantId,
            Name = "Settings",
            Slug = "settings",
            Permissions = [Scope.TenantSettings],
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
        await _service.CreateRequestAsync(_tenantId, _subjectId, null);
        var request = await _dbContext.MembershipRequests.FirstAsync();

        var result = await _service.ApproveRequestAsync(
            request.Id, _tenantId, [settingsRoleId], _adminSubjectId, [Scope.FullAccess], null, limitTo24Hours: true);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(MemberScopeResolver.ExemptFromHistoryClampDetail);
        (await _dbContext.TenantMembers.AnyAsync(m => m.SubjectId == _subjectId)).Should().BeFalse();
        await _dbContext.Entry(request).ReloadAsync();
        request.Status.Should().Be("pending");
    }

    [Fact]
    public async Task ApproveRequestAsync_RequestNotFound_ReturnsFailure()
    {
        var result = await _service.ApproveRequestAsync(
            Guid.CreateVersion7(), _tenantId, [_adminRoleId], _adminSubjectId, [Scope.FullAccess], null);

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
            request.Id, _tenantId, [_adminRoleId], _adminSubjectId, [Scope.FullAccess], null);

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
        first.CreatedAt = _time.GetUtcNow().UtcDateTime.AddDays(-1);
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

    // ──────────────────────────────────────────────
    // A requester awaiting access (the anonymous passkey ceremony)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ApproveRequestAsync_FromARequesterAwaitingAccess_ActivatesThemWithTheMembership()
    {
        var requester = await SeedRequesterAsync("Pending", isActive: false);

        var result = await _service.ApproveRequestAsync(
            await RequestIdOf(requester), _tenantId, [_adminRoleId], _adminSubjectId, [Scope.FullAccess], null);

        result.Success.Should().BeTrue();
        var subject = await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == requester);
        subject.IsActive.Should().BeTrue();
        subject.ApprovalStatus.Should().Be("Approved");
        (await _dbContext.TenantMembers.AnyAsync(m => m.TenantId == _tenantId && m.SubjectId == requester)).Should().BeTrue();
        (await new SubjectRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off).ListPendingAccessRequestsAsync(CancellationToken.None))
            .Should().NotContain(s => s.Id == requester, "an approved requester leaves the platform administrators' list");
        AccessDecisionAudits(requester).Should().ContainSingle().Which.Should().Contain("Approved");
    }

    [Fact]
    public async Task ApproveRequestAsync_FromAnActiveRequester_AuditsNoActivation()
    {
        await _service.CreateRequestAsync(_tenantId, _subjectId, null);

        (await _service.ApproveRequestAsync(
            await RequestIdOf(_subjectId), _tenantId, [_adminRoleId], _adminSubjectId, [Scope.FullAccess], null))
            .Success.Should().BeTrue();

        AccessDecisionAudits(_subjectId).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Denied")]
    public async Task ApproveRequestAsync_FromAnInactiveRequesterNotAwaitingAccess_IsRefusedAndChangesNothing(string approvalStatus)
    {
        var requester = await SeedRequesterAsync(approvalStatus, isActive: false);
        var requestId = await RequestIdOf(requester);

        var result = await _service.ApproveRequestAsync(
            requestId, _tenantId, [_adminRoleId], _adminSubjectId, [Scope.FullAccess], null);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not active");
        var subject = await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == requester);
        subject.IsActive.Should().BeFalse();
        subject.ApprovalStatus.Should().Be(approvalStatus);
        (await _dbContext.TenantMembers.AnyAsync(m => m.SubjectId == requester)).Should().BeFalse();
        (await _dbContext.MembershipRequests.AsNoTracking().SingleAsync(r => r.Id == requestId)).Status
            .Should().Be(MembershipRequestRecord.Pending);
        ApprovalNotifications().Should().BeEmpty();
    }

    [Fact]
    public async Task ApproveRequestAsync_AfterAPlatformAdminDeletedTheRequester_FindsNoRequest()
    {
        var requester = await SeedRequesterAsync("Pending", isActive: false);
        var requestId = await RequestIdOf(requester);

        (await new SubjectRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off).DeleteAsync(requester, CancellationToken.None))
            .Should().BeOfType<SubjectChange.Changed>();

        (await _service.GetPendingRequestsAsync(_tenantId)).Should().BeEmpty();
        var result = await _service.ApproveRequestAsync(
            requestId, _tenantId, [_adminRoleId], _adminSubjectId, [Scope.FullAccess], null);
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Request not found.");
    }

    [Fact]
    public async Task DenyRequestAsync_FromARequesterAwaitingAccess_MarksThemDeniedAndKeepsThemInactive()
    {
        var requester = await SeedRequesterAsync("Pending", isActive: false);

        (await _service.DenyRequestAsync(await RequestIdOf(requester), _tenantId, _adminSubjectId))
            .Success.Should().BeTrue();

        var subject = await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == requester);
        subject.IsActive.Should().BeFalse();
        subject.ApprovalStatus.Should().Be("Denied");
        (await _dbContext.TenantMembers.AnyAsync(m => m.SubjectId == requester)).Should().BeFalse();
        (await new SubjectRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off).ListPendingAccessRequestsAsync(CancellationToken.None))
            .Should().NotContain(s => s.Id == requester);
        AccessDecisionAudits(requester).Should().ContainSingle().Which.Should().Contain("Denied");
    }

    [Fact]
    public async Task DenyRequestAsync_FromAnActiveRequester_LeavesTheSubjectAsItIs()
    {
        await _service.CreateRequestAsync(_tenantId, _subjectId, null);

        (await _service.DenyRequestAsync(await RequestIdOf(_subjectId), _tenantId, _adminSubjectId))
            .Success.Should().BeTrue();

        var subject = await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == _subjectId);
        subject.IsActive.Should().BeTrue();
        subject.ApprovalStatus.Should().Be("Approved");
        AccessDecisionAudits(_subjectId).Should().BeEmpty();
    }

    [Fact]
    public async Task ARequestOnOneTenant_IsNeitherListedNorDecidableOnAnother()
    {
        var other = Guid.CreateVersion7();
        _dbContext.Tenants.Add(new TenantEntity { Id = other, Slug = "other", DisplayName = "Other" });
        await _dbContext.SaveChangesAsync();
        var requester = await SeedRequesterAsync("Pending", isActive: false);
        var requestId = await RequestIdOf(requester);

        (await _service.GetPendingRequestsAsync(_tenantId)).Should().ContainSingle(r => r.Id == requestId);
        (await _service.GetPendingRequestsAsync(other)).Should().BeEmpty();
        (await _service.ApproveRequestAsync(requestId, other, [], _adminSubjectId, [Scope.FullAccess], null))
            .Error.Should().Be("Request not found.");
        (await _service.DenyRequestAsync(requestId, other, _adminSubjectId))
            .Error.Should().Be("Request not found.");
        (await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == requester)).ApprovalStatus.Should().Be("Pending");
    }

    /// <summary>A subject in <paramref name="approvalStatus"/> with a pending request on this tenant.</summary>
    private async Task<Guid> SeedRequesterAsync(string approvalStatus, bool isActive)
    {
        var requester = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = requester, Name = "Sam Smith", IsActive = isActive, ApprovalStatus = approvalStatus,
        });
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(), SubjectId = requester, CredentialId = Guid.CreateVersion7().ToByteArray(),
            PublicKey = [1, 2, 3], CreatedAt = DateTime.UtcNow,
        });
        _dbContext.MembershipRequests.Add(new MembershipRequestEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = requester,
            Message = "I am the school nurse",
            Status = MembershipRequestRecord.Pending,
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
        return requester;
    }

    private async Task<Guid> RequestIdOf(Guid subjectId) =>
        (await _dbContext.MembershipRequests.AsNoTracking().SingleAsync(r => r.SubjectId == subjectId)).Id;

    private IReadOnlyList<string> AccessDecisionAudits(Guid subjectId) =>
        _audit.Invocations
            .Where(i => i.Method.Name == nameof(IAuthAuditService.LogAsync)
                && (string)i.Arguments[0]! == AuthAuditEventType.SubjectUpdated
                && (Guid?)i.Arguments[1] == subjectId)
            .Select(i => (string)i.Arguments[6]!)
            .ToList();
}
