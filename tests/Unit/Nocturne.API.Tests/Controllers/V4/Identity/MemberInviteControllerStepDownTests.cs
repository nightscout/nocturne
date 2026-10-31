using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// Stepping down is the one edit a member may make to their own roles, so it must only ever
/// narrow them, never leave the tenant without an owner, and be audited like any role change.
/// </summary>
public sealed class MemberInviteControllerStepDownTests : IDisposable
{
    private readonly NocturneDbContext _db;
    private readonly Mock<IAuthAuditService> _audit = new();
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _helperSubjectId = Guid.CreateVersion7();
    private readonly Guid _recipientSubjectId = Guid.CreateVersion7();
    private readonly TenantRoleEntity _owner;
    private readonly TenantRoleEntity _caretaker;
    private readonly TenantRoleEntity _viewer;
    private readonly Guid _helperMemberId = Guid.CreateVersion7();

    public MemberInviteControllerStepDownTests()
    {
        _db = TestDbContextFactory.CreateInMemoryContext();
        _db.TenantId = _tenantId;
        _db.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "test", DisplayName = "Test" });

        _owner = Role(RoleSeeds.Owner);
        _caretaker = Role(RoleSeeds.Caretaker);
        _viewer = Role(RoleSeeds.Viewer);
        _db.TenantRoles.AddRange(_owner, _caretaker, _viewer);

        _db.Subjects.AddRange(
            new SubjectEntity { Id = _helperSubjectId, Name = "Alex" },
            new SubjectEntity { Id = _recipientSubjectId, Name = "Sam" });
        _db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = _helperMemberId, TenantId = _tenantId, SubjectId = _helperSubjectId,
            DirectPermissions = [Scope.GlucoseRead],
        });
        _db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(), TenantMemberId = _helperMemberId, TenantRoleId = _owner.Id,
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

    private void AddRecipientAsOwner()
    {
        var member = new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = _recipientSubjectId };
        _db.TenantMembers.Add(member);
        _db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(), TenantMemberId = member.Id, TenantRoleId = _owner.Id,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private MemberInviteController Controller(params string[] grantedScopes)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(_tenantId);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true, SubjectId = _helperSubjectId, TenantId = _tenantId,
        };

        return new MemberInviteController(
            Mock.Of<IMemberInviteService>(),
            Mock.Of<ITenantService>(),
            new TenantRoleService(new TenantMembershipRepository(_db.AsFactory()), TimeProvider.System),
            Mock.Of<ITenantMemberService>(),
            tenantAccessor.Object,
            new TenantMembershipRepository(_db.AsFactory()))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private async Task<List<string?>> HelperRoleSlugsAsync() =>
        await _db.TenantMemberRoles.AsNoTracking()
            .Where(mr => mr.TenantMemberId == _helperMemberId)
            .Select(mr => mr.TenantRole!.Slug)
            .ToListAsync();

    [Fact]
    public async Task ReplacesTheCallersRolesWithCaretaker_AndKeepsTheirDirectPermissions()
    {
        AddRecipientAsOwner();

        var result = await Controller(Scope.FullAccess).StepDown(_audit.Object, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await HelperRoleSlugsAsync()).Should().Equal(RoleSeeds.Caretaker);
        (await _db.TenantMembers.AsNoTracking().SingleAsync(m => m.Id == _helperMemberId))
            .DirectPermissions.Should().Equal(Scope.GlucoseRead);
        (await _db.TenantMembers.AsNoTracking().OwnersOf(_tenantId).Select(m => m.SubjectId).ToListAsync())
            .Should().Equal(_recipientSubjectId);
    }

    [Fact]
    public async Task AuditsTheOwnerRoleRemovedAndCaretakerAssigned_AgainstTheCaller()
    {
        AddRecipientAsOwner();

        await Controller(Scope.FullAccess).StepDown(_audit.Object, CancellationToken.None);

        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.RoleRemoved, _helperSubjectId, true, It.IsAny<string?>(), It.IsAny<string?>(),
            null, """{"role":"owner","kind":"tenant"}""", null, null, null), Times.Once);
        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.RoleAssigned, _helperSubjectId, true, It.IsAny<string?>(), It.IsAny<string?>(),
            null, """{"role":"caretaker","kind":"tenant"}""", null, null, null), Times.Once);
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RefusesTheLastOwner_AndChangesAndAuditsNothing()
    {
        var result = await Controller(Scope.FullAccess).StepDown(_audit.Object, CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(TenantOwnerFilter.LastOwnerRefusal);
        (await HelperRoleSlugsAsync()).Should().Equal(RoleSeeds.Owner);
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RefusesACallerForWhomCaretakerWouldBeMoreAccess()
    {
        _db.TenantMemberRoles.RemoveRange(_db.TenantMemberRoles.Where(mr => mr.TenantMemberId == _helperMemberId));
        _db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(), TenantMemberId = _helperMemberId, TenantRoleId = _viewer.Id,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await Controller([.. RoleSeeds.Permissions[RoleSeeds.Viewer]])
            .StepDown(_audit.Object, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await HelperRoleSlugsAsync()).Should().Equal(RoleSeeds.Viewer);
        _audit.VerifyNoOtherCalls();
    }
}
