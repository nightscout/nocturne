using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// A change to a member's roles or direct permissions is audited against the member's subject,
/// one row per role or permission added or removed, and only once the change is stored.
/// </summary>
public sealed class MemberInviteControllerAuditTests : IDisposable
{
    private readonly NocturneDbContext _db;
    private readonly Mock<IAuthAuditService> _audit = new();
    private readonly PublicAccessCacheService _publicAccessCache = TestPublicAccessCache.Create();
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _callerSubjectId = Guid.CreateVersion7();
    private readonly Guid _targetSubjectId = Guid.CreateVersion7();
    private readonly Guid _targetMemberId = Guid.CreateVersion7();
    private readonly TenantRoleEntity _owner;
    private readonly TenantRoleEntity _caretaker;
    private readonly TenantRoleEntity _viewer;

    public MemberInviteControllerAuditTests()
    {
        _db = TestDbContextFactory.CreateInMemoryContext();
        _db.TenantId = _tenantId;
        _db.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "test", DisplayName = "Test" });

        _owner = Role(RoleSeeds.Owner);
        _caretaker = Role(RoleSeeds.Caretaker);
        _viewer = Role(RoleSeeds.Viewer);
        _db.TenantRoles.AddRange(_owner, _caretaker, _viewer);

        _db.Subjects.AddRange(
            new SubjectEntity { Id = _callerSubjectId, Name = "Caller" },
            new SubjectEntity { Id = _targetSubjectId, Name = "Target" });
        _db.TenantMembers.AddRange(
            new TenantMemberEntity
            {
                Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = _callerSubjectId,
                DirectPermissions = [Scope.FullAccess],
            },
            new TenantMemberEntity
            {
                Id = _targetMemberId, TenantId = _tenantId, SubjectId = _targetSubjectId,
                DirectPermissions = [Scope.GlucoseRead, Scope.TreatmentsRead],
            });
        _db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(), TenantMemberId = _targetMemberId, TenantRoleId = _viewer.Id,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    public void Dispose() => _db.Dispose();

    private TenantRoleEntity Role(string slug) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = _tenantId, Name = RoleSeeds.DisplayNames[slug], Slug = slug,
        Permissions = [.. RoleSeeds.Permissions[slug]], IsSystem = true,
    };

    private MemberInviteController Controller(params string[] grantedScopes) =>
        Controller(new TenantMembershipRepository(_db.AsFactory()), grantedScopes);

    private MemberInviteController Controller(ITenantMembershipRepository members, params string[] grantedScopes)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(_tenantId);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true, SubjectId = _callerSubjectId, TenantId = _tenantId,
        };

        return new MemberInviteController(
            Mock.Of<IMemberInviteService>(),
            Mock.Of<ITenantService>(),
            new TenantRoleService(new TenantMembershipRepository(_db.AsFactory()), TimeProvider.System),
            Mock.Of<ITenantMemberService>(),
            tenantAccessor.Object,
            members,
            _audit.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private Task<IActionResult> SetRoles(MemberInviteController controller, params Guid[] roleIds) =>
        controller.SetMemberRoles(
            _targetMemberId, new SetMemberRolesRequest([.. roleIds]), _publicAccessCache, CancellationToken.None);

    private Task<IActionResult> SetPermissions(MemberInviteController controller, params string[]? permissions) =>
        controller.SetMemberPermissions(
            _targetMemberId, new SetMemberPermissionsRequest(permissions?.ToList()), _publicAccessCache,
            CancellationToken.None);

    private void VerifyLogged(string eventType, string detailsJson) =>
        _audit.Verify(a => a.LogAsync(
            eventType, _targetSubjectId, true, It.IsAny<string?>(), It.IsAny<string?>(),
            null, detailsJson, null, null, null), Times.Once);

    [Fact]
    public async Task SetMemberRoles_AuditsEachRoleRemovedAndEachRoleAdded()
    {
        var result = await SetRoles(Controller(Scope.FullAccess), _owner.Id, _caretaker.Id);

        result.Should().BeOfType<NoContentResult>();
        VerifyLogged(AuthAuditEventType.RoleRemoved, """{"role":"viewer","kind":"tenant"}""");
        VerifyLogged(AuthAuditEventType.RoleAssigned, """{"role":"owner","kind":"tenant"}""");
        VerifyLogged(AuthAuditEventType.RoleAssigned, """{"role":"caretaker","kind":"tenant"}""");
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetMemberRoles_AuditsOnlyTheRoleThatChanged()
    {
        await SetRoles(Controller(Scope.FullAccess), _viewer.Id, _caretaker.Id);

        VerifyLogged(AuthAuditEventType.RoleAssigned, """{"role":"caretaker","kind":"tenant"}""");
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetMemberRoles_AuditsNothingWhenTheSetIsUnchanged()
    {
        var result = await SetRoles(Controller(Scope.FullAccess), _viewer.Id);

        result.Should().BeOfType<NoContentResult>();
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetMemberRoles_AuditsOnlyAfterTheChangeIsStored()
    {
        var rolesWhenAudited = new List<string?>();
        _audit.Setup(a => a.LogAsync(
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(),
                It.IsAny<Guid?>()))
            .Callback(() => rolesWhenAudited = _db.TenantMemberRoles.AsNoTracking()
                .Where(mr => mr.TenantMemberId == _targetMemberId)
                .Select(mr => mr.TenantRole!.Slug)
                .ToList())
            .Returns(Task.CompletedTask);

        await SetRoles(Controller(Scope.FullAccess), _caretaker.Id);

        rolesWhenAudited.Should().Equal(RoleSeeds.Caretaker);
    }

    [Fact]
    public async Task SetMemberRoles_ReadsNothingFromTheRepositoryOnceTheChangeIsStored()
    {
        var repository = RecordingRepository.Wrap(new TenantMembershipRepository(_db.AsFactory()));
        var callsAfterSave = new List<string>();
        _audit.Setup(a => a.LogAsync(
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(),
                It.IsAny<Guid?>()))
            .Callback(() => callsAfterSave.AddRange(((RecordingRepository)repository).CallsAfterTheRoleChange))
            .Returns(Task.CompletedTask);

        await SetRoles(Controller(repository, Scope.FullAccess), _owner.Id, _caretaker.Id);

        callsAfterSave.Should().BeEmpty();
        VerifyLogged(AuthAuditEventType.RoleRemoved, """{"role":"viewer","kind":"tenant"}""");
        VerifyLogged(AuthAuditEventType.RoleAssigned, """{"role":"owner","kind":"tenant"}""");
        VerifyLogged(AuthAuditEventType.RoleAssigned, """{"role":"caretaker","kind":"tenant"}""");
    }

    [Fact]
    public async Task SetMemberRoles_AuditsNothingWhenTheLastOwnerWouldBeRemoved()
    {
        _db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(), TenantMemberId = _targetMemberId, TenantRoleId = _owner.Id,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await SetRoles(Controller(Scope.FullAccess), _viewer.Id);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetMemberRoles_AuditsNothingWhenTheCallerCannotGrantTheRole()
    {
        var result = await SetRoles(Controller(Scope.MembersManage), _viewer.Id, _owner.Id);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetMemberPermissions_AuditsEachPermissionRemovedAndEachPermissionAdded()
    {
        var result = await SetPermissions(Controller(Scope.FullAccess), Scope.TreatmentsRead, Scope.DevicesRead);

        result.Should().BeOfType<NoContentResult>();
        VerifyLogged(AuthAuditEventType.RoleRemoved, """{"permission":"glucose.read","kind":"tenant"}""");
        VerifyLogged(AuthAuditEventType.RoleAssigned, """{"permission":"devices.read","kind":"tenant"}""");
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetMemberPermissions_AuditsEachPermissionRemovedWhenTheyAreCleared()
    {
        await SetPermissions(Controller(Scope.FullAccess), permissions: null);

        VerifyLogged(AuthAuditEventType.RoleRemoved, """{"permission":"glucose.read","kind":"tenant"}""");
        VerifyLogged(AuthAuditEventType.RoleRemoved, """{"permission":"treatments.read","kind":"tenant"}""");
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetMemberPermissions_AuditsNothingWhenTheSetIsUnchanged()
    {
        var result = await SetPermissions(Controller(Scope.FullAccess), Scope.TreatmentsRead, Scope.GlucoseRead);

        result.Should().BeOfType<NoContentResult>();
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetMemberPermissions_AuditsOnlyAfterTheChangeIsStored()
    {
        IReadOnlyList<string>? permissionsWhenAudited = null;
        _audit.Setup(a => a.LogAsync(
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(),
                It.IsAny<Guid?>()))
            .Callback(() => permissionsWhenAudited = _db.TenantMembers.AsNoTracking()
                .Single(m => m.Id == _targetMemberId).DirectPermissions)
            .Returns(Task.CompletedTask);

        await SetPermissions(Controller(Scope.FullAccess), Scope.DevicesRead);

        permissionsWhenAudited.Should().Equal(Scope.DevicesRead);
    }

    [Fact]
    public async Task SetMemberPermissions_AuditsNothingWhenTheCallerCannotGrantThePermission()
    {
        var result = await SetPermissions(Controller(Scope.MembersManage), Scope.DevicesRead);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _audit.VerifyNoOtherCalls();
    }

    /// <summary>Forwards to a real repository and records each call made after <c>ReplaceRolesUnlessLastOwnerAsync</c>.</summary>
    public class RecordingRepository : DispatchProxy
    {
        private ITenantMembershipRepository _inner = null!;
        private bool _saved;

        public List<string> CallsAfterTheRoleChange { get; } = [];

        public static ITenantMembershipRepository Wrap(ITenantMembershipRepository inner)
        {
            var proxy = Create<ITenantMembershipRepository, RecordingRepository>();
            ((RecordingRepository)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (_saved)
                CallsAfterTheRoleChange.Add(targetMethod!.Name);
            _saved |= targetMethod!.Name == nameof(ITenantMembershipRepository.ReplaceRolesUnlessLastOwnerAsync);
            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                throw e.InnerException;
            }
        }
    }
}
