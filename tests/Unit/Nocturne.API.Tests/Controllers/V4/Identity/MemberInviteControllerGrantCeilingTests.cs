using Nocturne.API.Tests.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.API.Services.Auth;
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
        [.. RoleSeeds.Permissions[RoleSeeds.Admin]];

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
            Name = RoleSeeds.DisplayNames[RoleSeeds.Owner],
            Slug = RoleSeeds.Owner,
            Permissions = [Scope.FullAccess],
            IsSystem = true,
        };
        var caretakerRole = new TenantRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Name = RoleSeeds.DisplayNames[RoleSeeds.Caretaker],
            Slug = RoleSeeds.Caretaker,
            Permissions = [.. RoleSeeds.Permissions[RoleSeeds.Caretaker]],
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
            DirectPermissions = [Scope.GlucoseRead],
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
    private MemberInviteController BuildController(params string[] grantedScopes) =>
        BuildController(Mock.Of<IMemberInviteService>(), grantedScopes);

    private MemberInviteController BuildController(IMemberInviteService invites, params string[] grantedScopes)
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
            invites,
            // The real service: setting an end date also caps the guest links the member made.
            new TenantService(
                _dbContext.AsFactory(),
                new TenantRepository(_dbContext.AsFactory()),
                new TenantMembershipRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off),
                new SubjectRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off),
                new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new OperatorConfiguration()),
                Mock.Of<IHttpClientFactory>(),
                Mock.Of<ITenantRoleService>(),
                new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
                TimeProvider.System,
                Mock.Of<ILogger<TenantService>>(),
                AccountLinkRoutes.Off, InertTeamStaff.Announcer()),
            // The real service, not a mock: the tenant check and the ceiling are the properties
            // under test, and a mock would assert the mock.
            new TenantRoleService(new TenantMembershipRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off), TimeProvider.System, Moq.Mock.Of<Nocturne.Core.Contracts.Identity.IMemberInviteRepository>(), new Nocturne.Infrastructure.Data.Repositories.TeamRepository(_dbContext.AsFactory()), _dbContext.AsFactory(), new Nocturne.API.Services.Auth.GrantRevocationService(new Nocturne.API.Services.Auth.GuestSessionCacheService(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())))),
            Mock.Of<ITenantMemberService>(),
            tenantAccessor.Object,
            new TenantMembershipRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    /// <summary>
    /// Asserts the refusal the granter actually reads, rather than the status phrase.
    /// </summary>
    private static void ShouldRefuse(IActionResult result, int statusCode, string reason)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(statusCode);
        var details = problem.Value.Should().BeOfType<ProblemDetails>().Subject;
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
            new SetMemberPermissionsRequest([Scope.FullAccess]),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(
            result,
            StatusCodes.Status403Forbidden,
            "Cannot grant '*' because the caller does not hold it.");

        (await TargetPermissionsAsync()).Should().BeEquivalentTo([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task SetMemberPermissions_cannotGrantAPermissionTheCallerLacks()
    {
        // audit.manage is absent from the Administrator seed role.
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberPermissions(
            _targetMemberId,
            new SetMemberPermissionsRequest([Scope.AuditManage]),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(
            result,
            StatusCodes.Status403Forbidden,
            "Cannot grant 'audit.manage' because the caller does not hold it.");

        (await TargetPermissionsAsync()).Should().BeEquivalentTo([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task SetMemberPermissions_rejectsAnUnknownPermission()
    {
        // Superuser caller, so the ceiling cannot be what refuses this. 400 rather than 403
        // distinguishes the two: an atom outside the vocabulary is malformed input, and asserting
        // the status is what makes the stated reason testable.
        var controller = BuildController(Scope.FullAccess);

        var result = await controller.SetMemberPermissions(
            _targetMemberId,
            new SetMemberPermissionsRequest(["glucose.destroy"]),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(
            result,
            StatusCodes.Status400BadRequest,
            "'glucose.destroy' is not a known permission.");

        (await TargetPermissionsAsync()).Should().BeEquivalentTo([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task SetMemberPermissions_allowsAGrantWithinTheCallersOwnPermissions()
    {
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberPermissions(
            _targetMemberId,
            new SetMemberPermissionsRequest([Scope.GlucoseRead, Scope.TreatmentsReadWrite]),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();

        (await TargetPermissionsAsync()).Should().BeEquivalentTo(
            [Scope.GlucoseRead, Scope.TreatmentsReadWrite]);
    }

    [Fact]
    public async Task SetMemberPermissions_allowsTheReadTierImpliedByAManageGrantTheCallerHolds()
    {
        // audit.manage implies audit.read, so a caller holding the manage tier may hand out read.
        var controller = BuildController(Scope.MembersManage, Scope.AuditManage);

        var result = await controller.SetMemberPermissions(
            _targetMemberId,
            new SetMemberPermissionsRequest([Scope.AuditRead]),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();

        (await TargetPermissionsAsync()).Should().BeEquivalentTo([Scope.AuditRead]);
    }

    [Fact]
    public async Task SetMemberPermissions_allowsASuperuserToGrantSuperuser()
    {
        // The ceiling is a ceiling, not a ban: an owner still delegates its own access.
        var controller = BuildController(Scope.FullAccess);

        var result = await controller.SetMemberPermissions(
            _targetMemberId,
            new SetMemberPermissionsRequest([Scope.FullAccess]),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();

        (await TargetPermissionsAsync()).Should().BeEquivalentTo([Scope.FullAccess]);
    }

    [Fact]
    public async Task SetMemberPermissions_rejectsEditingTheCallersOwnMembership()
    {
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberPermissions(
            _callerMemberId,
            new SetMemberPermissionsRequest([Scope.GlucoseRead]),
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

    private async Task GiveOwnerRoleAsync(Guid memberId)
    {
        _dbContext.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = memberId,
            TenantRoleId = _ownerRoleId,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    [Fact]
    public async Task SetMemberRoles_refusesTakingOwnerFromTheOnlyOwner()
    {
        await GiveOwnerRoleAsync(_targetMemberId);
        var controller = BuildController(Scope.FullAccess);

        var result = await controller.SetMemberRoles(
            _targetMemberId,
            new SetMemberRolesRequest([_caretakerRoleId]),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status400BadRequest, "Cannot remove the only owner, or the last owner who can sign in");

        (await _dbContext.TenantMemberRoles.Where(mr => mr.TenantMemberId == _targetMemberId)
            .Select(mr => mr.TenantRoleId).ToListAsync()).Should().BeEquivalentTo([_ownerRoleId]);
    }

    [Fact]
    public async Task SetMemberRoles_allowsChangingTheOnlyOwnersRolesWhenOwnerIsKept()
    {
        await GiveOwnerRoleAsync(_targetMemberId);
        var controller = BuildController(Scope.FullAccess);

        var result = await controller.SetMemberRoles(
            _targetMemberId,
            new SetMemberRolesRequest([_ownerRoleId, _caretakerRoleId]),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await _dbContext.TenantMemberRoles.Where(mr => mr.TenantMemberId == _targetMemberId)
            .Select(mr => mr.TenantRoleId).ToListAsync()).Should().BeEquivalentTo([_ownerRoleId, _caretakerRoleId]);
    }

    private async Task AddSecondOwnerAsync()
    {
        var secondSubjectId = Guid.CreateVersion7();
        var secondMember = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = secondSubjectId,
        };
        _dbContext.Subjects.Add(new SubjectEntity { Id = secondSubjectId, Name = "Second owner" });
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(), SubjectId = secondSubjectId, CredentialId = secondSubjectId.ToByteArray(), PublicKey = [1],
        });
        _dbContext.TenantMembers.Add(secondMember);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        await GiveOwnerRoleAsync(secondMember.Id);
    }

    [Fact]
    public async Task SetMemberRoles_allowsTakingOwnerFromAnOwnerWhenAnotherActiveOwnerRemains()
    {
        await AddSecondOwnerAsync();
        await GiveOwnerRoleAsync(_targetMemberId);
        var controller = BuildController(Scope.FullAccess);

        var result = await controller.SetMemberRoles(
            _targetMemberId,
            new SetMemberRolesRequest([_caretakerRoleId]),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await TargetRoleIdsAsync()).Should().BeEquivalentTo([_caretakerRoleId]);
    }

    private Task<List<Guid>> TargetRoleIdsAsync() =>
        _dbContext.TenantMemberRoles.AsNoTracking()
            .Where(mr => mr.TenantMemberId == _targetMemberId)
            .Select(mr => mr.TenantRoleId)
            .ToListAsync();

    private async Task GiveCaretakerRoleAsync(Guid memberId)
    {
        _dbContext.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = memberId,
            TenantRoleId = _caretakerRoleId,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    [Fact]
    public async Task SetMemberRoles_mayAddAGrantableRoleToAMemberHoldingAnUngrantableOne()
    {
        await GiveOwnerRoleAsync(_targetMemberId);
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberRoles(
            _targetMemberId,
            new SetMemberRolesRequest([_ownerRoleId, _caretakerRoleId]),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await TargetRoleIdsAsync()).Should().BeEquivalentTo([_ownerRoleId, _caretakerRoleId]);
    }

    [Fact]
    public async Task SetMemberRoles_mayRemoveAGrantableRoleFromAMemberHoldingAnUngrantableOne()
    {
        await GiveOwnerRoleAsync(_targetMemberId);
        await GiveCaretakerRoleAsync(_targetMemberId);
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberRoles(
            _targetMemberId,
            new SetMemberRolesRequest([_ownerRoleId]),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await TargetRoleIdsAsync()).Should().BeEquivalentTo([_ownerRoleId]);
    }

    [Fact]
    public async Task SetMemberRoles_savingTheHeldSetUnchangedPassesWhenItHoldsAnUngrantableRole()
    {
        await GiveOwnerRoleAsync(_targetMemberId);
        await GiveCaretakerRoleAsync(_targetMemberId);
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberRoles(
            _targetMemberId,
            new SetMemberRolesRequest([_caretakerRoleId, _ownerRoleId]),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await TargetRoleIdsAsync()).Should().BeEquivalentTo([_ownerRoleId, _caretakerRoleId]);
    }

    [Fact]
    public async Task SetMemberRoles_cannotRemoveARoleTheCallerCouldNotGrant()
    {
        // A second owner remains, so the last-owner rule cannot be what refuses this.
        await AddSecondOwnerAsync();
        await GiveOwnerRoleAsync(_targetMemberId);
        var controller = BuildController(AdministratorScopes);

        var result = await controller.SetMemberRoles(
            _targetMemberId,
            new SetMemberRolesRequest([_caretakerRoleId]),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status403Forbidden, "Cannot remove a role you cannot grant.");
        (await TargetRoleIdsAsync()).Should().BeEquivalentTo([_ownerRoleId]);
    }

    [Fact]
    public async Task SetMemberRoles_rejectsEditingTheCallersOwnMembership()
    {
        var controller = BuildController(Scope.FullAccess);

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
        var controller = BuildController(Scope.FullAccess);

        var result = await controller.SetMemberLimitTo24Hours(
            _callerMemberId,
            new SetMemberLimitTo24HoursRequest(true),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status400BadRequest, SelfEditReason);

        var caller = await _dbContext.TenantMembers.AsNoTracking().FirstAsync(m => m.Id == _callerMemberId);
        caller.LimitTo24Hours.Should().BeFalse("the refusal returned before the clamp was written");
    }

    private const string AdministratorClampReason =
        "An owner or a member who manages site settings cannot be limited to the last 24 hours.";

    private async Task<bool> TargetClampAsync() =>
        (await _dbContext.TenantMembers.AsNoTracking().FirstAsync(m => m.Id == _targetMemberId))
            .LimitTo24Hours;

    [Fact]
    public async Task SetMemberLimitTo24Hours_refusesClampingAnOwner()
    {
        _dbContext.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = _targetMemberId,
            TenantRoleId = _ownerRoleId,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        var controller = BuildController(Scope.FullAccess);

        var result = await controller.SetMemberLimitTo24Hours(
            _targetMemberId,
            new SetMemberLimitTo24HoursRequest(true),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status400BadRequest, AdministratorClampReason);
        (await TargetClampAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetMemberLimitTo24Hours_refusesClampingATenantSettingsHolder()
    {
        var target = await _dbContext.TenantMembers.FirstAsync(m => m.Id == _targetMemberId);
        target.DirectPermissions = [Scope.GlucoseRead, Scope.TenantSettings];
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        var controller = BuildController(Scope.FullAccess);

        var result = await controller.SetMemberLimitTo24Hours(
            _targetMemberId,
            new SetMemberLimitTo24HoursRequest(true),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status400BadRequest, AdministratorClampReason);
        (await TargetClampAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetMemberLimitTo24Hours_clampsAFollower()
    {
        var controller = BuildController(Scope.FullAccess);

        var result = await controller.SetMemberLimitTo24Hours(
            _targetMemberId,
            new SetMemberLimitTo24HoursRequest(true),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await TargetClampAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task CreateInvite_refusesAClampedOwnerInvite()
    {
        var controller = BuildController(Scope.FullAccess);

        var result = await controller.CreateInvite(new CreateMemberInviteRequest
        {
            RoleIds = [_ownerRoleId],
            LimitTo24Hours = true,
        }, TimeProvider.System);

        ShouldRefuse(result, StatusCodes.Status400BadRequest, AdministratorClampReason);
    }

    [Fact]
    public async Task CreateInvite_byAClampedCaller_refusesAnInviteThatWouldAdministerTheTenant()
    {
        // The caller's clamp is inherited, so an administrator invite would be born clamped.
        var controller = BuildController(Scope.FullAccess);
        controller.HttpContext.RequestServices = TestRequestServices.HistoryClamped();

        var result = await controller.CreateInvite(new CreateMemberInviteRequest
        {
            DirectPermissions = [Scope.TenantSettings],
        }, TimeProvider.System);

        ShouldRefuse(result, StatusCodes.Status400BadRequest, AdministratorClampReason);
    }

    [Fact]
    public async Task SetMemberLimitTo24Hours_byAClampedCaller_refusesLiftingAClamp()
    {
        var target = await _dbContext.TenantMembers.FirstAsync(m => m.Id == _targetMemberId);
        target.LimitTo24Hours = true;
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        var controller = BuildController(AdministratorScopes);
        controller.HttpContext.RequestServices = TestRequestServices.HistoryClamped();

        var result = await controller.SetMemberLimitTo24Hours(
            _targetMemberId,
            new SetMemberLimitTo24HoursRequest(false),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status403Forbidden, HistoryCeilingReason);
        (await TargetClampAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task SetMemberLimitTo24Hours_byAClampedCaller_mayStillClampAFollower()
    {
        var controller = BuildController(AdministratorScopes);
        controller.HttpContext.RequestServices = TestRequestServices.HistoryClamped();

        var result = await controller.SetMemberLimitTo24Hours(
            _targetMemberId,
            new SetMemberLimitTo24HoursRequest(true),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await TargetClampAsync()).Should().BeTrue();
    }

    private const string HistoryCeilingReason =
        "You can see the last 24 hours only, so you cannot give anyone more history than that.";

    private async Task ClampTargetAsync()
    {
        var target = await _dbContext.TenantMembers.FirstAsync(m => m.Id == _targetMemberId);
        target.LimitTo24Hours = true;
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    [Fact]
    public async Task SetMemberRoles_byAClampedCaller_refusesExemptingAClampedMember()
    {
        await ClampTargetAsync();
        var controller = BuildController(Scope.FullAccess);
        controller.HttpContext.RequestServices = TestRequestServices.HistoryClamped();

        var result = await controller.SetMemberRoles(
            _targetMemberId,
            new SetMemberRolesRequest([_ownerRoleId]),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status403Forbidden, HistoryCeilingReason);
        (await _dbContext.TenantMemberRoles.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetMemberPermissions_byAClampedCaller_refusesExemptingAClampedMember()
    {
        await ClampTargetAsync();
        var controller = BuildController(Scope.FullAccess);
        controller.HttpContext.RequestServices = TestRequestServices.HistoryClamped();

        var result = await controller.SetMemberPermissions(
            _targetMemberId,
            new SetMemberPermissionsRequest([Scope.GlucoseRead, Scope.TenantSettings]),
            _publicAccessCache,
            CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status403Forbidden, HistoryCeilingReason);
        (await TargetPermissionsAsync()).Should().BeEquivalentTo([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task SetMemberRoles_byAnUnclampedCaller_mayPromoteAClampedMember()
    {
        await ClampTargetAsync();
        var controller = BuildController(Scope.FullAccess);
        controller.HttpContext.RequestServices = TestRequestServices.Build(clamped: false);

        var result = await controller.SetMemberRoles(
            _targetMemberId,
            new SetMemberRolesRequest([_ownerRoleId]),
            _publicAccessCache,
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task CreateInvite_allowsAClampedFollowerInvite()
    {
        var controller = BuildController(Scope.FullAccess);

        var result = await controller.CreateInvite(new CreateMemberInviteRequest
        {
            RoleIds = [_caretakerRoleId],
            LimitTo24Hours = true,
        }, TimeProvider.System);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status201Created);
    }

    /// <summary>A mixed member: the sum holds the Administrator role, the direct reason less.</summary>
    private MemberInviteController BuildMixedController(GrantCeiling? ceiling)
    {
        var controller = BuildController(AdministratorScopes);
        controller.HttpContext.SetGrantCeiling(ceiling);
        return controller;
    }

    private static readonly GrantCeiling DirectMembersManage = new(
        new HashSet<string> { Scope.MembersManage, Scope.MembersInvite, Scope.GlucoseRead }, null, false);

    [Fact]
    public async Task MixedMember_mayNotGiveAPermissionOnlyTheTeamGives()
    {
        var controller = BuildMixedController(DirectMembersManage);

        var result = await controller.SetMemberPermissions(
            _targetMemberId, new SetMemberPermissionsRequest([Scope.GlucoseRead, Scope.TreatmentsRead]), _publicAccessCache, CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status403Forbidden, "Cannot grant 'treatments.read' because the caller does not hold it.");
        (await TargetPermissionsAsync()).Should().BeEquivalentTo([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task MixedMember_mayGiveWhatTheDirectReasonHolds()
    {
        var controller = BuildMixedController(DirectMembersManage);

        var result = await controller.SetMemberPermissions(
            _targetMemberId, new SetMemberPermissionsRequest([Scope.GlucoseRead, Scope.MembersInvite]), _publicAccessCache, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task MixedMember_whoseDirectReasonIsClamped_mayNotLiftAClamp()
    {
        var target = await _dbContext.TenantMembers.FirstAsync(m => m.Id == _targetMemberId);
        target.LimitTo24Hours = true;
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        var controller = BuildMixedController(DirectMembersManage with { HistoryClamped = true });

        var result = await controller.SetMemberLimitTo24Hours(
            _targetMemberId, new SetMemberLimitTo24HoursRequest(false), _publicAccessCache, CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status403Forbidden, HistoryCeilingReason);
        (await TargetClampAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task MixedMember_whoseDirectReasonEnds_mayNotRemoveAMemberWhoOutlastsIt()
    {
        var controller = BuildMixedController(DirectMembersManage with { AccessEndsAt = AccessEndNow.UtcDateTime.AddDays(3) });

        var result = await controller.RemoveMember(_targetSubjectId, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await _dbContext.TenantMembers.AsNoTracking().AnyAsync(m => m.Id == _targetMemberId)).Should().BeTrue();
    }

    /// <summary>
    /// A mixed member's direct reason: less than the Administrator sum, with an end the sum lacks and
    /// the 24-hour limit the sum lacks.
    /// </summary>
    private static GrantCeiling MixedCeiling(DateTime directEnd, params string[] extraScopes) =>
        new(DirectMembersManage.Scopes.Concat(extraScopes).ToHashSet(), directEnd, true);

    private MemberInviteController BuildMixedController(IMemberInviteService invites, GrantCeiling ceiling)
    {
        var controller = BuildController(invites, AdministratorScopes);
        controller.HttpContext.SetGrantCeiling(ceiling);
        return controller;
    }

    [Fact]
    public async Task CreateInvite_byAMixedMember_givesOnlyTheDirectReasonsScopesEndAndLimit()
    {
        var ceiling = MixedCeiling(DateTime.UtcNow.Date.AddDays(30));
        var invites = new Mock<IMemberInviteService>();
        IEnumerable<string>? granter = null;
        DateTime? end = null;
        bool? limited = null;
        invites.Setup(i => i.CreateInviteAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<List<Guid>>(),
                It.IsAny<DateTime?>(), It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(),
                It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .Callback((Guid _, Guid _, IEnumerable<string> g, List<Guid> _, DateTime? e, List<string>? _, string? _, int _, int? _, bool l, string? _) =>
            {
                granter = g;
                end = e;
                limited = l;
            })
            .ReturnsAsync((MemberInviteResult)null!);

        var result = await BuildMixedController(invites.Object, ceiling).CreateInvite(
            new CreateMemberInviteRequest { RoleIds = [_caretakerRoleId], LimitTo24Hours = false }, AccessEndClock);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status201Created);
        granter.Should().BeSameAs(ceiling.Scopes, "a role or permission only the team gives is beyond what the invite may give");
        granter.Should().NotContain(Scope.TreatmentsRead);
        end.Should().Be(ceiling.AccessEndsAt);
        limited.Should().BeTrue("a limited direct reason makes the invite limited while the team reason is not");
    }

    [Fact]
    public async Task CreateInvite_byAMixedMember_refusesAnEndPastTheDirectEnd()
    {
        var directEnd = DateTime.UtcNow.Date.AddDays(30);
        var invites = new Mock<IMemberInviteService>();

        var result = await BuildMixedController(invites.Object, MixedCeiling(directEnd)).CreateInvite(
            new CreateMemberInviteRequest { RoleIds = [_caretakerRoleId], AccessEndsAt = directEnd.AddDays(1) }, AccessEndClock);

        ShouldRefuse(result, StatusCodes.Status403Forbidden, HttpContextExtensions.AccessEndCeilingDetail);
        invites.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task SetMemberRoles_byAMixedMember_givesOnlyARoleTheDirectReasonHolds()
    {
        var directEnd = DateTime.UtcNow.Date.AddDays(30);
        await GiveTargetAccessEndAsync(directEnd.AddDays(-10));

        var refused = await BuildMixedController(Mock.Of<IMemberInviteService>(), MixedCeiling(directEnd)).SetMemberRoles(
            _targetMemberId, new SetMemberRolesRequest([_caretakerRoleId]), _publicAccessCache, CancellationToken.None);

        refused.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await TargetRoleIdsAsync()).Should().BeEmpty();

        var given = await BuildMixedController(
                Mock.Of<IMemberInviteService>(), MixedCeiling(directEnd, [.. RoleSeeds.Permissions[RoleSeeds.Caretaker]]))
            .SetMemberRoles(_targetMemberId, new SetMemberRolesRequest([_caretakerRoleId]), _publicAccessCache, CancellationToken.None);

        given.Should().BeOfType<NoContentResult>();
        (await TargetRoleIdsAsync()).Should().BeEquivalentTo([_caretakerRoleId]);
    }

    [Fact]
    public async Task SetMemberAccessEnd_byAMixedMember_refusesAnEndPastTheDirectEnd()
    {
        var directEnd = DateTime.UtcNow.Date.AddDays(30);
        await GiveTargetAccessEndAsync(directEnd.AddDays(-10));
        var controller = BuildMixedController(Mock.Of<IMemberInviteService>(), MixedCeiling(directEnd));

        ShouldRefuse(
            await SetTargetAccessEndAsync(controller, new DateTimeOffset(directEnd.AddDays(1), TimeSpan.Zero)),
            StatusCodes.Status403Forbidden, HttpContextExtensions.AccessEndCeilingDetail);
        (await TargetAccessEndAsync()).Should().Be(directEnd.AddDays(-10));

        var within = new DateTimeOffset(directEnd.AddDays(-5), TimeSpan.Zero);
        (await SetTargetAccessEndAsync(controller, within)).Should().BeOfType<NoContentResult>();
        (await TargetAccessEndAsync()).Should().Be(within.UtcDateTime);
    }

    [Fact]
    public async Task RevokeInvite_byATeamOnlyMember_givesNothing()
    {
        var invite = InviteEndingAt(null);
        var invites = InviteService(invite);
        var controller = BuildController(invites.Object, Scope.MembersInvite);
        controller.HttpContext.SetGrantCeiling(null);

        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(await controller.RevokeInvite(invite.Id));
        invites.Verify(i => i.RevokeInviteAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task TeamOnlyMember_givesNothing()
    {
        var controller = BuildMixedController(null);

        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(await controller.SetMemberPermissions(
            _targetMemberId, new SetMemberPermissionsRequest([Scope.GlucoseRead]), _publicAccessCache, CancellationToken.None));
        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(await controller.SetMemberRoles(
            _targetMemberId, new SetMemberRolesRequest([_caretakerRoleId]), _publicAccessCache, CancellationToken.None));
        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(await controller.SetMemberLimitTo24Hours(
            _targetMemberId, new SetMemberLimitTo24HoursRequest(true), _publicAccessCache, CancellationToken.None));
        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(await SetTargetAccessEndAsync(controller, AccessEndNow.AddDays(30)));
        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(await controller.RemoveMember(_targetSubjectId, CancellationToken.None));
        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(await controller.CreateInvite(
            new CreateMemberInviteRequest { RoleIds = [_caretakerRoleId] }, TimeProvider.System));

        (await TargetPermissionsAsync()).Should().BeEquivalentTo([Scope.GlucoseRead]);
        (await TargetAccessEndAsync()).Should().BeNull();
        (await TargetClampAsync()).Should().BeFalse();
    }

    private static readonly DateTimeOffset AccessEndNow = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static readonly FakeTimeProvider AccessEndClock = new(AccessEndNow);

    /// <summary>The caller's own membership, as the request authenticated it, ending at <paramref name="accessEndsAt"/>.</summary>
    private void CallerAccessEndsAt(MemberInviteController controller, DateTime? accessEndsAt) =>
        new RequestMembership(
                _callerSubjectId,
                _tenantId,
                new MembershipFacts(_callerMemberId, new TenantMemberAccess(AdministratorScopes.ToHashSet(), false, accessEndsAt), new TenantMemberAccess(AdministratorScopes.ToHashSet(), false, accessEndsAt), null))
            .StoreOn(controller.HttpContext);

    private async Task GiveTargetAccessEndAsync(DateTime? accessEndsAt)
    {
        var target = await _dbContext.TenantMembers.FirstAsync(m => m.Id == _targetMemberId);
        target.AccessEndsAt = accessEndsAt;
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private async Task<DateTime?> TargetAccessEndAsync() =>
        (await _dbContext.TenantMembers.AsNoTracking().FirstAsync(m => m.Id == _targetMemberId)).AccessEndsAt;

    private Task<IActionResult> SetTargetAccessEndAsync(MemberInviteController controller, DateTimeOffset? accessEndsAt, Guid? member = null) =>
        controller.SetMemberAccessEnd(
            member ?? _targetMemberId, new SetMemberAccessEndRequest(accessEndsAt), AccessEndClock, CancellationToken.None);

    [Fact]
    public async Task SetMemberAccessEnd_setsMovesAndClearsTheDateForACallerWithNoEnd()
    {
        var controller = BuildController(AdministratorScopes);

        (await SetTargetAccessEndAsync(controller, AccessEndNow.AddDays(30))).Should().BeOfType<NoContentResult>();
        (await TargetAccessEndAsync()).Should().Be(AccessEndNow.UtcDateTime.AddDays(30));

        (await SetTargetAccessEndAsync(controller, AccessEndNow.AddDays(400))).Should().BeOfType<NoContentResult>();
        (await TargetAccessEndAsync()).Should().Be(AccessEndNow.UtcDateTime.AddDays(400));

        (await SetTargetAccessEndAsync(controller, null)).Should().BeOfType<NoContentResult>();
        (await TargetAccessEndAsync()).Should().BeNull();
    }

    [Fact]
    public async Task SetMemberAccessEnd_sentTwiceWithSevenFractionalDigits_storesMicrosecondsAndAuditsOnce()
    {
        var controller = BuildController(AdministratorScopes);
        var requested = AccessEndNow.AddDays(30).AddTicks(1234567);

        await SetTargetAccessEndAsync(controller, requested);
        await SetTargetAccessEndAsync(controller, requested);

        (await TargetAccessEndAsync())!.Value.Ticks.Should().Be(
            requested.UtcDateTime.Ticks - requested.UtcDateTime.Ticks % TimeSpan.TicksPerMicrosecond,
            "the column stores microseconds, so the request is cut to them before it is compared");
        (await _dbContext.AuthAuditLog.AsNoTracking()
                .CountAsync(r => r.TargetSubjectId == _targetSubjectId && r.EventType == AuthAuditEventType.AccessEndChanged))
            .Should().Be(1);
    }

    [Fact]
    public async Task SetMemberAccessEnd_takesTheInstantInWhateverOffsetItIsSentIn()
    {
        var controller = BuildController(AdministratorScopes);

        await SetTargetAccessEndAsync(controller, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(10)));

        (await TargetAccessEndAsync()).Should().Be(new DateTime(2026, 11, 30, 14, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public async Task SetMemberAccessEnd_refusesAnEndAtOrBeforeNow(int secondsFromNow)
    {
        var controller = BuildController(AdministratorScopes);

        var result = await SetTargetAccessEndAsync(controller, AccessEndNow.AddSeconds(secondsFromNow));

        ShouldRefuse(result, StatusCodes.Status400BadRequest, AccessEndRequest.PastDetail);
        (await TargetAccessEndAsync()).Should().BeNull();
    }

    private async Task<Guid> SeedGuestLinkMadeByTargetAsync(DateTime expiresAt)
    {
        var id = Guid.CreateVersion7();
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = id,
            TenantId = _tenantId,
            SubjectId = _callerSubjectId,
            CreatedBySubjectId = _targetSubjectId,
            GrantType = OAuthGrantTypes.Guest,
            Scopes = [Scope.GlucoseRead],
            TokenHash = id.ToString("N"),
            ExpiresAt = expiresAt,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return id;
    }

    private async Task<DateTime?> GrantExpiryAsync(Guid id) =>
        (await _dbContext.OAuthGrants.AsNoTracking().FirstAsync(g => g.Id == id)).ExpiresAt;

    [Fact]
    public async Task SetMemberAccessEnd_capsTheGuestLinksTheMemberMadeAtTheNewDate()
    {
        var expiresAt = AccessEndNow.UtcDateTime.AddHours(48);
        var link = await SeedGuestLinkMadeByTargetAsync(expiresAt);
        var controller = BuildController(AdministratorScopes);

        (await SetTargetAccessEndAsync(controller, AccessEndNow.AddDays(1))).Should().BeOfType<NoContentResult>();

        (await GrantExpiryAsync(link)).Should().Be(AccessEndNow.UtcDateTime.AddDays(1));
    }

    [Fact]
    public async Task SetMemberAccessEnd_refusedForAPastDate_capsNoLink()
    {
        var expiresAt = AccessEndNow.UtcDateTime.AddHours(48);
        var link = await SeedGuestLinkMadeByTargetAsync(expiresAt);
        var controller = BuildController(AdministratorScopes);

        ShouldRefuse(
            await SetTargetAccessEndAsync(controller, AccessEndNow.AddMinutes(-1)),
            StatusCodes.Status400BadRequest, AccessEndRequest.PastDetail);

        (await GrantExpiryAsync(link)).Should().Be(expiresAt);
    }

    [Fact]
    public async Task SetMemberAccessEnd_rejectsEditingTheCallersOwnMembership()
    {
        var controller = BuildController(AdministratorScopes);

        var result = await SetTargetAccessEndAsync(controller, AccessEndNow.AddDays(1), _callerMemberId);

        ShouldRefuse(result, StatusCodes.Status400BadRequest, SelfEditReason);
    }

    [Theory]
    [InlineData(DeviceSubjectFilter.DeviceSubjectName)]
    [InlineData(PublicSubjectFilter.PublicSubjectName)]
    public async Task SetMemberAccessEnd_refusesTheSystemSubjects(string subjectName)
    {
        var target = await _dbContext.Subjects.FirstAsync(s => s.Id == _targetSubjectId);
        target.Name = subjectName;
        target.IsSystemSubject = true;
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        var controller = BuildController(Scope.FullAccess);

        var result = await SetTargetAccessEndAsync(controller, AccessEndNow.AddDays(1));

        ShouldRefuse(result, StatusCodes.Status400BadRequest, AccessEndRequest.SystemSubjectDetail);
        (await TargetAccessEndAsync()).Should().BeNull();
    }

    [Fact]
    public async Task SetMemberAccessEnd_refusesAnOwner()
    {
        _dbContext.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(), TenantMemberId = _targetMemberId, TenantRoleId = _ownerRoleId,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        var controller = BuildController(Scope.FullAccess);

        var result = await SetTargetAccessEndAsync(controller, AccessEndNow.AddDays(1));

        ShouldRefuse(result, StatusCodes.Status400BadRequest, AccessEndRequest.OwnerDetail);
        (await TargetAccessEndAsync()).Should().BeNull();
    }

    [Fact]
    public async Task SetMemberRoles_refusesTheOwnerRoleForAMemberWhoseAccessEnds()
    {
        var target = await _dbContext.TenantMembers.FirstAsync(m => m.Id == _targetMemberId);
        target.AccessEndsAt = AccessEndNow.UtcDateTime.AddDays(1);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        var controller = BuildController(Scope.FullAccess);

        var result = await controller.SetMemberRoles(
            _targetMemberId, new SetMemberRolesRequest([_ownerRoleId]), _publicAccessCache, CancellationToken.None);

        ShouldRefuse(result, StatusCodes.Status400BadRequest, AccessEndRequest.ClearBeforeOwnerDetail);
        (await _dbContext.TenantMemberRoles.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SetMemberAccessEnd_byACallerWhoseAccessEnds_allowsAnEndNoLaterThanTheirs()
    {
        await GiveTargetAccessEndAsync(AccessEndNow.UtcDateTime.AddDays(10));
        var controller = BuildController(AdministratorScopes);
        CallerAccessEndsAt(controller, AccessEndNow.UtcDateTime.AddDays(10));

        (await SetTargetAccessEndAsync(controller, AccessEndNow.AddDays(10))).Should().BeOfType<NoContentResult>();
        (await SetTargetAccessEndAsync(controller, AccessEndNow.AddDays(3))).Should().BeOfType<NoContentResult>();
        (await TargetAccessEndAsync()).Should().Be(AccessEndNow.UtcDateTime.AddDays(3));
    }

    [Fact]
    public async Task SetMemberAccessEnd_byACallerWhoseAccessEnds_refusesALaterEndOrNone()
    {
        await GiveTargetAccessEndAsync(AccessEndNow.UtcDateTime.AddDays(5));
        var controller = BuildController(AdministratorScopes);
        CallerAccessEndsAt(controller, AccessEndNow.UtcDateTime.AddDays(10));

        ShouldRefuse(
            await SetTargetAccessEndAsync(controller, AccessEndNow.AddDays(11)),
            StatusCodes.Status403Forbidden, HttpContextExtensions.AccessEndCeilingDetail);
        ShouldRefuse(
            await SetTargetAccessEndAsync(controller, null),
            StatusCodes.Status403Forbidden, HttpContextExtensions.AccessEndCeilingDetail);
        (await TargetAccessEndAsync()).Should().Be(AccessEndNow.UtcDateTime.AddDays(5));
    }

    private async Task MakeTargetThePublicSubjectAsync()
    {
        var target = await _dbContext.Subjects.FirstAsync(s => s.Id == _targetSubjectId);
        target.Name = PublicSubjectFilter.PublicSubjectName;
        target.IsSystemSubject = true;
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    public static TheoryData<string> PublicMemberEdits => new() { "roles", "permissions", "limit" };

    private async Task<IActionResult> EditTargetAsync(MemberInviteController controller, string edit) => edit switch
    {
        "roles" => await controller.SetMemberRoles(
            _targetMemberId, new SetMemberRolesRequest([_caretakerRoleId]), _publicAccessCache, CancellationToken.None),
        "permissions" => await controller.SetMemberPermissions(
            _targetMemberId, new SetMemberPermissionsRequest([Scope.GlucoseRead, Scope.TreatmentsRead]),
            _publicAccessCache, CancellationToken.None),
        _ => await controller.SetMemberLimitTo24Hours(
            _targetMemberId, new SetMemberLimitTo24HoursRequest(true), _publicAccessCache, CancellationToken.None),
    };

    private async Task<(string Permissions, bool Limited, int Roles)> TargetStateAsync()
    {
        var member = await _dbContext.TenantMembers.AsNoTracking().FirstAsync(m => m.Id == _targetMemberId);
        return (string.Join(",", member.DirectPermissions ?? []), member.LimitTo24Hours, await _dbContext.TenantMemberRoles.CountAsync());
    }

    [Theory]
    [MemberData(nameof(PublicMemberEdits))]
    public async Task EditingThePublicMember_byACallerWhoseAccessEnds_isRefusedAndWritesNothing(string edit)
    {
        await MakeTargetThePublicSubjectAsync();
        var before = await TargetStateAsync();
        var controller = BuildController(Scope.FullAccess);
        CallerAccessEndsAt(controller, AccessEndNow.UtcDateTime.AddDays(10));

        var result = await EditTargetAsync(controller, edit);

        ShouldRefuse(result, StatusCodes.Status403Forbidden, HttpContextExtensions.PublicSharingEndDateDetail);
        (await TargetStateAsync()).Should().Be(before);
    }

    [Theory]
    [MemberData(nameof(PublicMemberEdits))]
    public async Task EditingThePublicMember_byACallerWithNoEnd_isAllowedAndWrites(string edit)
    {
        await MakeTargetThePublicSubjectAsync();
        var before = await TargetStateAsync();
        var controller = BuildController(Scope.FullAccess);

        var result = await EditTargetAsync(controller, edit);

        result.Should().BeOfType<NoContentResult>();
        (await TargetStateAsync()).Should().NotBe(before);
    }

    [Theory]
    [MemberData(nameof(PublicMemberEdits))]
    public async Task EditingAnotherMember_byACallerWhoseAccessEnds_isNotRefusedForPublicSharing(string edit)
    {
        await GiveTargetAccessEndAsync(AccessEndNow.UtcDateTime.AddDays(5));
        var controller = BuildController(Scope.FullAccess);
        CallerAccessEndsAt(controller, AccessEndNow.UtcDateTime.AddDays(10));

        var result = await EditTargetAsync(controller, edit);

        result.Should().BeOfType<NoContentResult>();
    }

    private static readonly DateTime CallerEnd = AccessEndNow.UtcDateTime.AddDays(10);

    /// <summary>The target's end in days from now against the caller's ten: none, later, the same, earlier.</summary>
    public static TheoryData<string, int?> MemberActionsAndTargetEnds()
    {
        var data = new TheoryData<string, int?>();
        foreach (var action in new[] { "roles", "permissions", "limit", "access-end", "remove" })
            foreach (var end in new int?[] { null, 11, 10, 3 })
                data.Add(action, end);
        return data;
    }

    private async Task<IActionResult> ActOnTargetAsync(MemberInviteController controller, string action) => action switch
    {
        "access-end" => await SetTargetAccessEndAsync(controller, AccessEndNow.AddDays(2)),
        "remove" => await controller.RemoveMember(_targetSubjectId, CancellationToken.None),
        _ => await EditTargetAsync(controller, action),
    };

    private async Task<string> TargetWholeStateAsync()
    {
        var member = await _dbContext.TenantMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == _targetMemberId);
        if (member is null)
            return "removed";

        var roles = await _dbContext.TenantMemberRoles.CountAsync(r => r.TenantMemberId == _targetMemberId);
        return string.Join(",", member.DirectPermissions ?? []) + "|" + member.LimitTo24Hours + "|" + member.AccessEndsAt?.ToString("O") + "|" + roles;
    }

    [Theory]
    [MemberData(nameof(MemberActionsAndTargetEnds))]
    public async Task ChangingAMember_byACallerWhoseAccessEnds_isRefusedOnlyWhenTheMemberOutlastsThem(string action, int? targetEndDays)
    {
        await GiveTargetAccessEndAsync(targetEndDays is { } days ? AccessEndNow.UtcDateTime.AddDays(days) : null);
        var before = await TargetWholeStateAsync();
        var controller = BuildController(Scope.FullAccess);
        CallerAccessEndsAt(controller, CallerEnd);

        var result = await ActOnTargetAsync(controller, action);

        if (targetEndDays is null or > 10)
        {
            ShouldRefuse(result, StatusCodes.Status403Forbidden, HttpContextExtensions.MemberOutlastsCallerDetail);
            (await TargetWholeStateAsync()).Should().Be(before, "a refusal writes nothing");
        }
        else
        {
            result.Should().BeOfType<NoContentResult>();
            (await TargetWholeStateAsync()).Should().NotBe(before);
        }
    }

    [Theory]
    [MemberData(nameof(MemberActionsAndTargetEnds))]
    public async Task ChangingAMember_byACallerWithNoEnd_isNeverRefusedForItsEnd(string action, int? targetEndDays)
    {
        await GiveTargetAccessEndAsync(targetEndDays is { } days ? AccessEndNow.UtcDateTime.AddDays(days) : null);
        var controller = BuildController(Scope.FullAccess);

        var result = await ActOnTargetAsync(controller, action);

        result.Should().BeOfType<NoContentResult>();
    }

    private static MemberInviteInfo InviteEndingAt(DateTime? accessEndsAt) => new(
        Guid.CreateVersion7(), Guid.Empty, "Tenant", "Chris", [], [Scope.GlucoseRead], null, false,
        AccessEndNow.UtcDateTime.AddDays(7), null, 0, true, false, false, AccessEndNow.UtcDateTime, [], [], [Scope.GlucoseRead],
        accessEndsAt);

    private static Mock<IMemberInviteService> InviteService(params MemberInviteInfo[] invites)
    {
        var service = new Mock<IMemberInviteService>();
        service.Setup(i => i.GetInvitesForTenantAsync(It.IsAny<Guid>())).ReturnsAsync([.. invites]);
        service.Setup(i => i.RevokeInviteAsync(It.IsAny<Guid>(), It.IsAny<Guid>())).ReturnsAsync(true);
        return service;
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(11, true)]
    [InlineData(10, false)]
    [InlineData(3, false)]
    public async Task RevokeInvite_byACallerWhoseAccessEnds_isRefusedOnlyWhenTheInviteOutlastsThem(int? inviteEndDays, bool refused)
    {
        var invite = InviteEndingAt(inviteEndDays is { } days ? AccessEndNow.UtcDateTime.AddDays(days) : null);
        var invites = InviteService(invite);
        var controller = BuildController(invites.Object, Scope.MembersInvite);
        CallerAccessEndsAt(controller, CallerEnd);

        var result = await controller.RevokeInvite(invite.Id);

        if (refused)
            ShouldRefuse(result, StatusCodes.Status403Forbidden, HttpContextExtensions.InviteOutlastsCallerDetail);
        else
            result.Should().BeOfType<NoContentResult>();
        invites.Verify(i => i.RevokeInviteAsync(invite.Id, _tenantId), refused ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task RevokeInvite_byACallerWithNoEnd_revokesAnInviteWithNoEnd()
    {
        var invite = InviteEndingAt(null);
        var invites = InviteService(invite);
        var controller = BuildController(invites.Object, Scope.MembersInvite);

        (await controller.RevokeInvite(invite.Id)).Should().BeOfType<NoContentResult>();
        invites.Verify(i => i.RevokeInviteAsync(invite.Id, _tenantId), Times.Once);
    }

    [Fact]
    public async Task ListInvites_marksTheInvitesThatOutlastACallerWhoseAccessEnds()
    {
        var invites = InviteService(
            InviteEndingAt(null),
            InviteEndingAt(AccessEndNow.UtcDateTime.AddDays(11)),
            InviteEndingAt(CallerEnd),
            InviteEndingAt(AccessEndNow.UtcDateTime.AddDays(3)));
        var dated = BuildController(invites.Object, Scope.MembersInvite);
        CallerAccessEndsAt(dated, CallerEnd);
        var undated = BuildController(invites.Object, Scope.MembersInvite);

        var datedList = (await dated.ListInvites()).Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<MemberInviteInfo>>().Subject;
        var undatedList = (await undated.ListInvites()).Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<MemberInviteInfo>>().Subject;

        datedList.Select(i => i.OutlastsCaller).Should().Equal(true, true, false, false);
        undatedList.Should().HaveCount(4).And.OnlyContain(i => !i.OutlastsCaller);
    }

    [Fact]
    public async Task GetMembers_marksTheMembersThatOutlastACallerWhoseAccessEnds()
    {
        var caller = await _dbContext.TenantMembers.FirstAsync(m => m.Id == _callerMemberId);
        caller.AccessEndsAt = CallerEnd;
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        var dated = BuildController(Scope.FullAccess);
        CallerAccessEndsAt(dated, CallerEnd);
        var undated = BuildController(Scope.FullAccess);

        async Task<List<TenantMemberDto>> MembersAsync(MemberInviteController controller) =>
            (await controller.GetMembers(CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeAssignableTo<List<TenantMemberDto>>().Subject;

        var cases = new (int? Days, bool Outlasts)[] { (null, true), (11, true), (10, false), (3, false) };
        foreach (var (days, outlasts) in cases)
        {
            await GiveTargetAccessEndAsync(days is { } d ? AccessEndNow.UtcDateTime.AddDays(d) : null);

            (await MembersAsync(dated)).Single(m => m.Id == _targetMemberId).OutlastsCaller
                .Should().Be(outlasts, $"the target ends in {days} days and the caller in 10");
            (await MembersAsync(dated)).Single(m => m.Id == _callerMemberId).OutlastsCaller.Should().BeFalse();
            (await MembersAsync(undated)).Should().OnlyContain(m => !m.OutlastsCaller);
        }
    }

    public void Dispose() => _dbContext.Dispose();
}
