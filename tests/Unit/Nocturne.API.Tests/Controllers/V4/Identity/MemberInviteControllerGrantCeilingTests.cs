using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// Verifies the grant ceiling on member role and permission assignment: a caller can only hand
/// out permissions it holds itself, and cannot edit its own membership. Without the ceiling, a
/// caller past the <c>members.manage</c> gate could write <c>directPermissions: ["*"]</c> onto
/// any membership, including its own.
/// </summary>
public sealed class MemberInviteControllerGrantCeilingTests : IDisposable
{
    private readonly NocturneDbContext _dbContext;
    private readonly PublicAccessCacheService _publicAccessCache = TestPublicAccessCache.Create();
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _callerSubjectId = Guid.CreateVersion7();
    private readonly Guid _targetSubjectId = Guid.CreateVersion7();

    private Guid _callerMemberId;
    private Guid _targetMemberId;
    private Guid _ownerRoleId;
    private Guid _caretakerRoleId;

    /// <summary>Effective permissions of the seeded Administrator role: manage rights, no superuser.</summary>
    private static readonly string[] AdministratorScopes =
        [.. TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Admin]];

    public MemberInviteControllerGrantCeilingTests()
    {
        _dbContext = TestDbContextFactory.CreateInMemoryContext();
        // The controller runs on a tenant-pinned context, and the seeded rows must be written
        // under the same tenant for EnforceTenantOwnership to accept later modifications.
        _dbContext.TenantId = _tenantId;
        Seed();
    }

    private void Seed()
    {
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test Tenant",
        });

        var ownerRole = new TenantRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = TenantPermissions.SeedRoleNames[TenantPermissions.SeedRoles.Owner],
            Slug = TenantPermissions.SeedRoles.Owner,
            Permissions = [TenantPermissions.Superuser],
            IsSystem = true,
        };
        var caretakerRole = new TenantRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = TenantPermissions.SeedRoleNames[TenantPermissions.SeedRoles.Caretaker],
            Slug = TenantPermissions.SeedRoles.Caretaker,
            Permissions = [.. TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Caretaker]],
            IsSystem = true,
        };
        _dbContext.TenantRoles.AddRange(ownerRole, caretakerRole);
        _ownerRoleId = ownerRole.Id;
        _caretakerRoleId = caretakerRole.Id;

        // Every membership needs its subject row. TenantMemberEntity.Subject is a required
        // navigation (subject_id is non-nullable), so the controller's Include(m => m.Subject)
        // drops a member whose subject row is missing and the endpoint answers 404 before
        // reaching the check under test.
        _dbContext.Subjects.AddRange(
            new SubjectEntity { Id = _callerSubjectId, Name = "Caller" },
            new SubjectEntity { Id = _targetSubjectId, Name = "Target" });

        var callerMember = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = _callerSubjectId,
            DirectPermissions = [.. AdministratorScopes],
        };
        var targetMember = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = _targetSubjectId,
            DirectPermissions = [TenantPermissions.GlucoseRead],
        };
        _dbContext.TenantMembers.AddRange(callerMember, targetMember);
        _callerMemberId = callerMember.Id;
        _targetMemberId = targetMember.Id;

        _dbContext.SaveChanges();
        _dbContext.ChangeTracker.Clear();
    }

    /// <summary>
    /// Builds the controller for a caller holding <paramref name="grantedScopes"/>, as
    /// <c>MemberScopeMiddleware</c> leaves the request.
    /// </summary>
    private MemberInviteController BuildController(params string[] grantedScopes)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(_tenantId);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = _callerSubjectId,
            TenantId = _tenantId,
        };

        return new MemberInviteController(
            Mock.Of<IMemberInviteService>(),
            Mock.Of<ITenantService>(),
            // The real service, not a mock: the tenant check and the ceiling are the properties
            // under test, and a mock would assert the mock.
            new TenantRoleService(_dbContext),
            Mock.Of<ITenantMemberService>(),
            tenantAccessor.Object,
            _dbContext)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    /// <summary>
    /// Asserts the refusal the granter actually reads. openapi-remote-codegen 0.2.0 resolves a
    /// ProblemDetails to <c>title</c> before <c>detail</c>, so a reason carried only in the detail
    /// reaches the members page as the literal "Bad Request" or "Forbidden" — asserting the title is
    /// what makes the reason testable.
    /// </summary>
    private static void ShouldRefuse(IActionResult result, int statusCode, string reason)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(statusCode);
        var details = problem.Value.Should().BeOfType<ProblemDetails>().Subject;
        details.Title.Should().Be(reason);
        details.Detail.Should().Be(reason);
    }

    /// <summary>The reason both role and permission edits give for a self-edit.</summary>
    private const string SelfEditReason =
        "Cannot change your own roles or permissions; ask another member with members.manage.";

    private Task<List<string>?> TargetPermissionsAsync() => PermissionsOfAsync(_targetMemberId);

    private async Task<List<string>?> PermissionsOfAsync(Guid memberId)
    {
        var member = await _dbContext.TenantMembers.AsNoTracking().FirstAsync(m => m.Id == memberId);
        return member.DirectPermissions;
    }

    [Fact]
    public async Task SetMemberPermissions_withoutSuperuser_cannotGrantSuperuser()
    {
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberPermissions(
            _targetMemberId,
            new SetMemberPermissionsRequest([TenantPermissions.Superuser]),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(
            result,
            StatusCodes.Status403Forbidden,
            "Cannot grant '*' because the caller does not hold it.");

        (await TargetPermissionsAsync()).Should().BeEquivalentTo([TenantPermissions.GlucoseRead]);
    }

    [Fact]
    public async Task SetMemberPermissions_cannotGrantAPermissionTheCallerLacks()
    {
        // audit.manage is absent from the Administrator seed role.
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberPermissions(
            _targetMemberId,
            new SetMemberPermissionsRequest([TenantPermissions.AuditManage]),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(
            result,
            StatusCodes.Status403Forbidden,
            "Cannot grant 'audit.manage' because the caller does not hold it.");

        (await TargetPermissionsAsync()).Should().BeEquivalentTo([TenantPermissions.GlucoseRead]);
    }

    [Fact]
    public async Task SetMemberPermissions_rejectsAnUnknownPermission()
    {
        // Superuser caller, so the ceiling cannot be what refuses this. 400 rather than 403
        // distinguishes the two: an atom outside the vocabulary is malformed input, and asserting
        // the status is what makes the stated reason testable.
        var controller = BuildController(TenantPermissions.Superuser);

        var result = await controller.SetMemberPermissions(
            _targetMemberId,
            new SetMemberPermissionsRequest(["glucose.destroy"]),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(
            result,
            StatusCodes.Status400BadRequest,
            "'glucose.destroy' is not a known permission.");

        (await TargetPermissionsAsync()).Should().BeEquivalentTo([TenantPermissions.GlucoseRead]);
    }

    [Fact]
    public async Task SetMemberPermissions_allowsAGrantWithinTheCallersOwnPermissions()
    {
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberPermissions(
            _targetMemberId,
            new SetMemberPermissionsRequest([TenantPermissions.GlucoseRead, TenantPermissions.TreatmentsReadWrite]),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();

        (await TargetPermissionsAsync()).Should().BeEquivalentTo(
            [TenantPermissions.GlucoseRead, TenantPermissions.TreatmentsReadWrite]);
    }

    [Fact]
    public async Task SetMemberPermissions_allowsTheReadTierImpliedByAManageGrantTheCallerHolds()
    {
        // audit.manage implies audit.read, so a caller holding the manage tier may hand out read.
        var controller = BuildController(TenantPermissions.MembersManage, TenantPermissions.AuditManage);

        var result = await controller.SetMemberPermissions(
            _targetMemberId,
            new SetMemberPermissionsRequest([TenantPermissions.AuditRead]),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();

        (await TargetPermissionsAsync()).Should().BeEquivalentTo([TenantPermissions.AuditRead]);
    }

    [Fact]
    public async Task SetMemberPermissions_allowsASuperuserToGrantSuperuser()
    {
        // The ceiling is a ceiling, not a ban: an owner still delegates its own access.
        var controller = BuildController(TenantPermissions.Superuser);

        var result = await controller.SetMemberPermissions(
            _targetMemberId,
            new SetMemberPermissionsRequest([TenantPermissions.Superuser]),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();

        (await TargetPermissionsAsync()).Should().BeEquivalentTo([TenantPermissions.Superuser]);
    }

    [Fact]
    public async Task SetMemberPermissions_rejectsEditingTheCallersOwnMembership()
    {
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberPermissions(
            _callerMemberId,
            new SetMemberPermissionsRequest([TenantPermissions.GlucoseRead]),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status400BadRequest, SelfEditReason);

        (await PermissionsOfAsync(_callerMemberId)).Should().BeEquivalentTo(AdministratorScopes);
    }

    [Fact]
    public async Task SetMemberRoles_cannotAssignTheOwnerRoleWithoutSuperuser()
    {
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberRoles(
            _targetMemberId,
            new SetMemberRolesRequest([_ownerRoleId]),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(
            result,
            StatusCodes.Status403Forbidden,
            "Cannot grant '*' because the caller does not hold it.");

        (await _dbContext.TenantMemberRoles.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetMemberRoles_rejectsTheWholeSetWhenOneRoleExceedsTheCeiling()
    {
        // Roles are validated as a union, so a grantable role cannot smuggle in one that isn't.
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberRoles(
            _targetMemberId,
            new SetMemberRolesRequest([_caretakerRoleId, _ownerRoleId]),
            _publicAccessCache,
            CancellationToken.None);

        // Every Caretaker atom is within the Administrator ceiling, so the owner role's superuser
        // is the only violation the union can report, whatever order the two roles resolve in.
        ShouldRefuse(
            result,
            StatusCodes.Status403Forbidden,
            "Cannot grant '*' because the caller does not hold it.");

        (await _dbContext.TenantMemberRoles.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetMemberRoles_allowsARoleWithinTheCallersOwnPermissions()
    {
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberRoles(
            _targetMemberId,
            new SetMemberRolesRequest([_caretakerRoleId]),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await _dbContext.TenantMemberRoles.CountAsync(mr => mr.TenantRoleId == _caretakerRoleId))
            .Should().Be(1);
    }

    [Fact]
    public async Task SetMemberRoles_rejectsEditingTheCallersOwnMembership()
    {
        var controller = BuildController(TenantPermissions.Superuser);

        var result = await controller.SetMemberRoles(
            _callerMemberId,
            new SetMemberRolesRequest([_ownerRoleId]),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status400BadRequest, SelfEditReason);

        (await _dbContext.TenantMemberRoles.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetMemberLimitTo24Hours_rejectsEditingTheCallersOwnMembership()
    {
        // The clamp is the third self-edit refusal, and the only one whose reason had no test.
        var controller = BuildController(TenantPermissions.Superuser);

        var result = await controller.SetMemberLimitTo24Hours(
            _callerMemberId,
            new SetMemberLimitTo24HoursRequest(true),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status400BadRequest, SelfEditReason);

        var caller = await _dbContext.TenantMembers.AsNoTracking().FirstAsync(m => m.Id == _callerMemberId);
        caller.LimitTo24Hours.Should().BeFalse("the refusal returned before the clamp was written");
    }

    public void Dispose() => _dbContext.Dispose();
}
