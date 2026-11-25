using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Identity;
using Nocturne.API.Tests.Infrastructure;
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
        _db.ActingAs(new TestAuditActor { SubjectId = _helperSubjectId });
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
        _db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(), SubjectId = _recipientSubjectId, CredentialId = _recipientSubjectId.ToByteArray(), PublicKey = [1],
        });
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
            TestTenantService.Over(_db),
            new TenantRoleService(new TenantMembershipRepository(_db.AsFactory(), AccountLinkRoutes.Off), TimeProvider.System, Moq.Mock.Of<Nocturne.Core.Contracts.Identity.IMemberInviteRepository>(), new Nocturne.Infrastructure.Data.Repositories.TeamRepository(_db.AsFactory()), _db.AsFactory(), new Nocturne.API.Services.Auth.GrantRevocationService(new Nocturne.API.Services.Auth.GuestSessionCacheService(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())))),
            Mock.Of<ITenantMemberService>(),
            tenantAccessor.Object,
            new TenantMembershipRepository(_db.AsFactory(), AccountLinkRoutes.Off))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private async Task<List<string>> HelperRoleSlugsAsync() =>
        await _db.TenantMemberRoles.AsNoTracking()
            .Where(mr => mr.TenantMemberId == _helperMemberId)
            .Select(mr => mr.TenantRole!.Slug)
            .ToListAsync();

    [Fact]
    public async Task ReplacesTheCallersRolesWithCaretaker_AndKeepsTheirDirectPermissions()
    {
        AddRecipientAsOwner();

        var result = await Controller(Scope.FullAccess).StepDown(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await HelperRoleSlugsAsync()).Should().Equal(RoleSeeds.Caretaker);
        (await _db.TenantMembers.AsNoTracking().SingleAsync(m => m.Id == _helperMemberId))
            .DirectPermissions.Should().Equal(Scope.GlucoseRead);
        (await _db.TenantMembers.AsNoTracking().OwnersOf(_tenantId).Select(m => m.SubjectId).ToListAsync())
            .Should().Equal(_recipientSubjectId);
    }

    /// <summary>
    /// A mixed member: the sum is an owner's, while the direct reason gives only
    /// <paramref name="directScopes"/>, ends in a week and reads the last 24 hours.
    /// </summary>
    private MemberInviteController MixedController(IEnumerable<string> directScopes)
    {
        var controller = Controller(Scope.FullAccess);
        controller.HttpContext.SetGrantCeiling(
            new GrantCeiling(directScopes.ToHashSet(), DateTime.UtcNow.AddDays(7), true));
        return controller;
    }

    [Fact]
    public async Task RefusesAMixedMemberWhoseDirectReasonCouldNotGiveCaretaker()
    {
        AddRecipientAsOwner();

        var result = await MixedController([Scope.GlucoseRead, Scope.MembersManage]).StepDown(CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await HelperRoleSlugsAsync()).Should().Equal(RoleSeeds.Owner);
    }

    [Fact]
    public async Task StepsDownAMixedMemberWhoseDirectReasonCouldGiveCaretaker()
    {
        AddRecipientAsOwner();

        var result = await MixedController(RoleSeeds.Permissions[RoleSeeds.Caretaker]).StepDown(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await HelperRoleSlugsAsync()).Should().Equal(RoleSeeds.Caretaker);
    }

    [Fact]
    public async Task RefusesATeamOnlyMember()
    {
        AddRecipientAsOwner();
        var controller = Controller(Scope.FullAccess);
        controller.HttpContext.SetGrantCeiling(null);

        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(await controller.StepDown(CancellationToken.None));
        (await HelperRoleSlugsAsync()).Should().Equal(RoleSeeds.Owner);
    }

    [Fact]
    public async Task AuditsTheOwnerRoleRemovedAndCaretakerAssigned_AgainstTheCaller()
    {
        AddRecipientAsOwner();

        await Controller(Scope.FullAccess).StepDown(CancellationToken.None);

        var rows = await _db.AuthAuditLog.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        var logged = rows.Select(a => (a.EventType, a.TargetSubjectId, a.ActorSubjectId, a.TenantId, a.DetailsJson)).ToList();
        logged.Should().Contain((AuthAuditEventType.RoleRemoved, (Guid?)_helperSubjectId, (Guid?)_helperSubjectId, (Guid?)_tenantId, (string?)"""{"role":"owner","kind":"tenant"}"""));
        logged.Should().Contain((AuthAuditEventType.RoleAssigned, (Guid?)_helperSubjectId, (Guid?)_helperSubjectId, (Guid?)_tenantId, (string?)"""{"role":"caretaker","kind":"tenant"}"""));
    }

    [Fact]
    public async Task AuditsNothingForACallerWhoAlreadyHoldsOnlyCaretaker()
    {
        _db.TenantMemberRoles.RemoveRange(_db.TenantMemberRoles.Where(mr => mr.TenantMemberId == _helperMemberId));
        _db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(), TenantMemberId = _helperMemberId, TenantRoleId = _caretaker.Id,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await Controller(Scope.FullAccess).StepDown(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await HelperRoleSlugsAsync()).Should().Equal(RoleSeeds.Caretaker);
        (await _db.AuthAuditLog.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RefusesTheLastOwner_AndChangesAndAuditsNothing()
    {
        var result = await Controller(Scope.FullAccess).StepDown(CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(TenantOwnerFilter.LastOwnerRefusal);
        (await HelperRoleSlugsAsync()).Should().Equal(RoleSeeds.Owner);
        (await _db.AuthAuditLog.AsNoTracking().CountAsync()).Should().Be(0);
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
            .StepDown(CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await HelperRoleSlugsAsync()).Should().Equal(RoleSeeds.Viewer);
        (await _db.AuthAuditLog.AsNoTracking().CountAsync()).Should().Be(0);
    }
}
