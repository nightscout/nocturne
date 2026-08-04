using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// Invite management authorizes on the <c>members.invite</c> permission and scopes every call to
/// the request's tenant. These endpoints previously lived on the platform-admin
/// <c>TenantController</c>, whose class-level <c>[Authorize(Roles = "platform_admin")]</c> rejected
/// ordinary tenant members before the endpoint's own check ran — so no tenant could mint an invite
/// while the UI kept offering the button.
/// </summary>
public sealed class MemberInviteControllerAuthorizationTests : IDisposable
{
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<IMemberInviteService> _inviteService = new();
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _callerSubjectId = Guid.CreateVersion7();

    /// <summary>Effective permissions of the seeded Caretaker role, which excludes members.invite.</summary>
    private static readonly string[] CaretakerScopes =
        [.. TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Caretaker]];

    public MemberInviteControllerAuthorizationTests()
    {
        _dbContext = TestDbContextFactory.CreateInMemoryContext();
        _dbContext.TenantId = _tenantId;
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
            _inviteService.Object,
            Mock.Of<ITenantService>(),
            Mock.Of<ITenantRoleService>(),
            tenantAccessor.Object,
            _dbContext)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static CreateMemberInviteRequest ClinicianInvite(Guid roleId) => new()
    {
        RoleIds = [roleId],
        Label = "Dr. Smith",
        MaxUses = null,
    };

    /// <summary>
    /// The reported failure: a tenant owner who is not the instance's platform admin creates a
    /// multi-use clinician link. Superuser stands in for the Owner role, whose seeded permission
    /// set is <c>*</c>.
    /// </summary>
    [Fact]
    public async Task CreateInvite_asTenantOwner_createsTheInvite()
    {
        var roleId = Guid.CreateVersion7();
        var expected = new MemberInviteResult(
            Guid.CreateVersion7(), "tok", "https://example.test/join?token=tok", DateTime.UtcNow.AddDays(7));
        _inviteService
            .Setup(s => s.CreateInviteAsync(
                _tenantId, _callerSubjectId, It.IsAny<IEnumerable<string>>(), It.IsAny<List<Guid>>(),
                It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>()))
            .ReturnsAsync(expected);

        var controller = BuildController(TenantPermissions.Superuser);

        var result = await controller.CreateInvite(ClinicianInvite(roleId));

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status201Created);
    }

    /// <summary>
    /// members.invite is what the members page gates its invite card on, so an Administrator
    /// holding it — but not the Owner role — has to be able to use the button it is shown.
    /// </summary>
    [Fact]
    public async Task CreateInvite_withMembersInviteButNotOwner_createsTheInvite()
    {
        var expected = new MemberInviteResult(
            Guid.CreateVersion7(), "tok", "https://example.test/join?token=tok", DateTime.UtcNow.AddDays(7));
        _inviteService
            .Setup(s => s.CreateInviteAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<List<Guid>>(),
                It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>()))
            .ReturnsAsync(expected);

        var controller = BuildController(TenantPermissions.MembersInvite);

        var result = await controller.CreateInvite(ClinicianInvite(Guid.CreateVersion7()));

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status201Created);
    }

    [Fact]
    public async Task CreateInvite_withoutMembersInvite_isForbidden()
    {
        var controller = BuildController(CaretakerScopes);

        var result = await controller.CreateInvite(ClinicianInvite(Guid.CreateVersion7()));

        result.Should().BeOfType<ForbidResult>();
        _inviteService.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The tenant comes from the request's tenant context, not from the caller — there is no
    /// route id to point at someone else's tenant.
    /// </summary>
    [Fact]
    public async Task CreateInvite_scopesTheInviteToTheRequestTenant()
    {
        _inviteService
            .Setup(s => s.CreateInviteAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<List<Guid>>(),
                It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>()))
            .ReturnsAsync(new MemberInviteResult(Guid.CreateVersion7(), "tok", "/join?token=tok", DateTime.UtcNow));

        var controller = BuildController(TenantPermissions.MembersInvite);

        await controller.CreateInvite(ClinicianInvite(Guid.CreateVersion7()));

        _inviteService.Verify(s => s.CreateInviteAsync(
            _tenantId, _callerSubjectId, It.IsAny<IEnumerable<string>>(), It.IsAny<List<Guid>>(),
            It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>()),
            Times.Once);
    }

    /// <summary>
    /// The service reports a refused grant as <see cref="ArgumentException"/>; the reason has to
    /// reach the caller, because the invite card renders whatever detail comes back.
    /// </summary>
    [Fact]
    public async Task CreateInvite_surfacesTheServiceRefusalReason()
    {
        _inviteService
            .Setup(s => s.CreateInviteAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<List<Guid>>(),
                It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>()))
            .ThrowsAsync(new ArgumentException("One or more role IDs do not belong to this tenant."));

        var controller = BuildController(TenantPermissions.MembersInvite);

        var result = await controller.CreateInvite(ClinicianInvite(Guid.CreateVersion7()));

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be("One or more role IDs do not belong to this tenant.");
    }

    [Fact]
    public async Task ListInvites_withMembersInvite_returnsTheTenantsInvites()
    {
        _inviteService.Setup(s => s.GetInvitesForTenantAsync(_tenantId)).ReturnsAsync([]);

        var controller = BuildController(TenantPermissions.MembersInvite);

        var result = await controller.ListInvites();

        result.Should().BeOfType<OkObjectResult>();
        _inviteService.Verify(s => s.GetInvitesForTenantAsync(_tenantId), Times.Once);
    }

    [Fact]
    public async Task ListInvites_withoutMembersInvite_isForbidden()
    {
        var controller = BuildController(CaretakerScopes);

        var result = await controller.ListInvites();

        result.Should().BeOfType<ForbidResult>();
        _inviteService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RevokeInvite_withoutMembersInvite_isForbidden()
    {
        var controller = BuildController(CaretakerScopes);

        var result = await controller.RevokeInvite(Guid.CreateVersion7());

        result.Should().BeOfType<ForbidResult>();
        _inviteService.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Revocation is bounded by the request tenant, so an invite id from another tenant answers
    /// 404 rather than deleting across the boundary.
    /// </summary>
    [Fact]
    public async Task RevokeInvite_scopesTheRevocationToTheRequestTenant()
    {
        var inviteId = Guid.CreateVersion7();
        _inviteService.Setup(s => s.RevokeInviteAsync(inviteId, _tenantId)).ReturnsAsync(false);

        var controller = BuildController(TenantPermissions.MembersInvite);

        var result = await controller.RevokeInvite(inviteId);

        result.Should().BeOfType<NotFoundResult>();
        _inviteService.Verify(s => s.RevokeInviteAsync(inviteId, _tenantId), Times.Once);
    }

    public void Dispose() => _dbContext.Dispose();
}
