using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using Nocturne.API.Authorization;
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
    private readonly Mock<ITenantService> _tenantService = new();
    private readonly Mock<ITenantMemberService> _tenantMemberService = new();
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _callerSubjectId = Guid.CreateVersion7();

    /// <summary>Effective permissions of the seeded Caretaker role, which excludes members.invite.</summary>
    private static readonly string[] CaretakerScopes =
        [.. RoleSeeds.Permissions[RoleSeeds.Caretaker]];

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
            _tenantService.Object,
            Mock.Of<ITenantRoleService>(),
            _tenantMemberService.Object,
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
                It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync(expected);

        var controller = BuildController(Scope.FullAccess);

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
                It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync(expected);

        var controller = BuildController(Scope.MembersInvite);

        var result = await controller.CreateInvite(ClinicianInvite(Guid.CreateVersion7()));

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status201Created);
    }

    /// <summary>
    /// Removal is destructive and newly reachable by a tenant rather than only a platform admin,
    /// so the permission gate is pinned in both directions.
    /// </summary>
    [Fact]
    public async Task RemoveMember_withoutMembersManage_isForbidden()
    {
        var controller = BuildController(Scope.MembersInvite);

        var result = await controller.RemoveMember(Guid.CreateVersion7(), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        _tenantService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveMember_withMembersManage_removesFromTheRequestTenant()
    {
        var subjectId = Guid.CreateVersion7();
        _tenantService
            .Setup(s => s.RemoveMemberAsync(_tenantId, subjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemberRemovalResult(true));

        var controller = BuildController(Scope.MembersManage);

        var result = await controller.RemoveMember(subjectId, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _tenantService.Verify(
            s => s.RemoveMemberAsync(_tenantId, subjectId, It.IsAny<CancellationToken>()),
            Times.Once,
            "the tenant comes from the request, not from the caller");
    }

    /// <summary>
    /// A tenant that removes its last owner locks itself out of a live site, so the refusal has to
    /// reach the member list rather than arrive as a bare status phrase.
    /// </summary>
    [Fact]
    public async Task RemoveMember_whenRefused_surfacesTheReason()
    {
        _tenantService
            .Setup(s => s.RemoveMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemberRemovalResult(false, "Cannot remove the last owner of a tenant"));

        var controller = BuildController(Scope.MembersManage);

        var result = await controller.RemoveMember(Guid.CreateVersion7(), CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var details = problem.Value.Should().BeOfType<ProblemDetails>().Subject;
        details.Title.Should().Be("Cannot remove the last owner of a tenant");
        details.Detail.Should().Be("Cannot remove the last owner of a tenant");
    }

    /// <summary>
    /// The regression itself was a class-level <c>[Authorize(Roles = "platform_admin")]</c>, which
    /// the MVC filter pipeline applies before any action body runs — so invoking the action
    /// directly, as every other test here does, cannot see it. Assert on the attributes instead.
    /// </summary>
    [Fact]
    public void InviteEndpoints_carryNoRoleRestriction()
    {
        var controller = typeof(MemberInviteController);
        var members = new MemberInfo[]
        {
            controller,
            controller.GetMethod(nameof(MemberInviteController.CreateInvite))!,
            controller.GetMethod(nameof(MemberInviteController.ListInvites))!,
            controller.GetMethod(nameof(MemberInviteController.RevokeInvite))!,
        };

        var roleRestricted = members
            .SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .Where(a => !string.IsNullOrWhiteSpace(a.Roles))
                .Select(a => $"{m.Name} -> Roles = \"{a.Roles}\""))
            .ToList();

        roleRestricted.Should().BeEmpty(
            "invite management is gated on the members.invite permission; a role restriction "
            + "rejects ordinary tenant members before the permission check runs");
    }

    /// <summary>
    /// The platform-admin controller must not regain an invite route: a second copy behind
    /// <c>[Authorize(Roles = "platform_admin")]</c> is how the endpoints became unreachable.
    /// </summary>
    [Fact]
    public void TenantController_exposesNoInviteRoutes()
    {
        var inviteRoutes = typeof(Nocturne.API.Controllers.V4.PlatformAdmin.TenantController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>()
                .Where(a => a.Template?.Contains("invites", StringComparison.OrdinalIgnoreCase) == true)
                .Select(a => $"{m.Name} -> {a.Template}"))
            .ToList();

        inviteRoutes.Should().BeEmpty();
    }

    /// <summary>
    /// An instance-key caller clears the permission gate but carries no subject, and
    /// <c>MemberInviteEntity.CreatedBySubjectId</c> is a non-nullable FK. Answer 401 rather than
    /// dereferencing a null subject id.
    /// </summary>
    [Fact]
    public async Task CreateInvite_withoutASubject_isUnauthorized()
    {
        var controller = BuildController(Scope.FullAccess);
        controller.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = null,
            TenantId = _tenantId,
        };

        var result = await controller.CreateInvite(ClinicianInvite(Guid.CreateVersion7()));

        result.Should().BeOfType<UnauthorizedResult>();
        _inviteService.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The caller's own scopes bound what the invite may confer. Passing the wrong set — or an
    /// empty one — would let the grant ceiling in the service pass everything.
    /// </summary>
    [Fact]
    public async Task CreateInvite_passesTheCallersScopesAsTheGrantCeiling()
    {
        _inviteService
            .Setup(s => s.CreateInviteAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<List<Guid>>(),
                It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync(new MemberInviteResult(Guid.CreateVersion7(), "tok", "/join?token=tok", DateTime.UtcNow));

        var controller = BuildController(Scope.MembersInvite, Scope.GlucoseRead);

        await controller.CreateInvite(ClinicianInvite(Guid.CreateVersion7()));

        _inviteService.Verify(s => s.CreateInviteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.Is<IEnumerable<string>>(scopes => scopes.OrderBy(x => x).SequenceEqual(
                new[] { Scope.GlucoseRead, Scope.MembersInvite }.OrderBy(x => x))),
            It.IsAny<List<Guid>>(), It.IsAny<List<string>?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<string?>()),
            Times.Once);
    }

    /// <summary>
    /// The join page is served per tenant, so the invite must be built on the host it was minted
    /// on. The configured base URL is the instance apex, which in Nocturne Cloud serves the
    /// marketing site — an invite built on it 404s for the invitee.
    /// </summary>
    [Fact]
    public async Task CreateInvite_buildsTheInviteOnTheRequestHost()
    {
        _inviteService
            .Setup(s => s.CreateInviteAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<List<Guid>>(),
                It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(),
                It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync(new MemberInviteResult(Guid.CreateVersion7(), "tok", "/join?token=tok", DateTime.UtcNow));

        var controller = BuildController(Scope.MembersInvite);
        controller.HttpContext.Request.Scheme = "https";
        controller.HttpContext.Request.Host = new HostString("chris-natoli-aps.nocturne.run");

        await controller.CreateInvite(ClinicianInvite(Guid.CreateVersion7()));

        _inviteService.Verify(s => s.CreateInviteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<List<Guid>>(),
            It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(),
            It.IsAny<bool>(), "https://chris-natoli-aps.nocturne.run"),
            Times.Once);
    }

    /// <summary>
    /// A member clamped to 24 hours cannot hand out unclamped access: the clamp is enforced in RLS
    /// and lifting it on an existing membership already needs members.manage.
    /// </summary>
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task CreateInvite_clampsTheInviteWhenTheCallerIsClamped(
        bool callerClamped, bool requested, bool expected)
    {
        _inviteService
            .Setup(s => s.CreateInviteAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<List<Guid>>(),
                It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync(new MemberInviteResult(Guid.CreateVersion7(), "tok", "/join?token=tok", DateTime.UtcNow));

        var controller = BuildController(Scope.MembersInvite);
        controller.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = _callerSubjectId,
            TenantId = _tenantId,
            LimitTo24Hours = callerClamped,
        };

        var invite = ClinicianInvite(Guid.CreateVersion7());
        invite.LimitTo24Hours = requested;

        await controller.CreateInvite(invite);

        _inviteService.Verify(s => s.CreateInviteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<List<Guid>>(),
            It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(), expected, It.IsAny<string?>()),
            Times.Once);
    }

    /// <summary>
    /// members.manage governs editing existing memberships; it does not imply the right to mint a
    /// new invite. Keeping the two atoms distinct is what makes the members page's own gate honest.
    /// </summary>
    [Fact]
    public async Task CreateInvite_withMembersManageAlone_isForbidden()
    {
        var controller = BuildController(Scope.MembersManage);

        var result = await controller.CreateInvite(ClinicianInvite(Guid.CreateVersion7()));

        result.Should().BeOfType<ForbidResult>();
        _inviteService.VerifyNoOtherCalls();
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
                It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync(new MemberInviteResult(Guid.CreateVersion7(), "tok", "/join?token=tok", DateTime.UtcNow));

        var controller = BuildController(Scope.MembersInvite);

        await controller.CreateInvite(ClinicianInvite(Guid.CreateVersion7()));

        _inviteService.Verify(s => s.CreateInviteAsync(
            _tenantId, _callerSubjectId, It.IsAny<IEnumerable<string>>(), It.IsAny<List<Guid>>(),
            It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<string?>()),
            Times.Once);
    }

    /// <summary>
    /// The service reports a refused grant — and an out-of-range expiry — as
    /// <see cref="ArgumentException"/>. The reason has to reach the creator, and the generated
    /// client resolves a ProblemDetails to <c>title</c> before <c>detail</c>, so carrying it only
    /// in the detail would render the branch's own reported symptom: a bare failure message.
    /// </summary>
    [Fact]
    public async Task CreateInvite_surfacesTheServiceRefusalReason()
    {
        _inviteService
            .Setup(s => s.CreateInviteAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<List<Guid>>(),
                It.IsAny<List<string>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ThrowsAsync(new ArgumentException("One or more role IDs do not belong to this tenant."));

        var controller = BuildController(Scope.MembersInvite);

        var result = await controller.CreateInvite(ClinicianInvite(Guid.CreateVersion7()));

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var details = problem.Value.Should().BeOfType<ProblemDetails>().Subject;
        details.Title.Should().Be("One or more role IDs do not belong to this tenant.");
        details.Detail.Should().Be("One or more role IDs do not belong to this tenant.");
    }

    [Fact]
    public async Task ListInvites_withMembersInvite_returnsTheTenantsInvites()
    {
        _inviteService.Setup(s => s.GetInvitesForTenantAsync(_tenantId)).ReturnsAsync([]);

        var controller = BuildController(Scope.MembersInvite);

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

        var controller = BuildController(Scope.MembersInvite);

        var result = await controller.RevokeInvite(inviteId);

        result.Should().BeOfType<NotFoundResult>();
        _inviteService.Verify(s => s.RevokeInviteAsync(inviteId, _tenantId), Times.Once);
    }

    /// <summary>An invite as the tenant-bounded lookup returns it.</summary>
    private MemberInviteInfo Invite(
        bool isValid = true,
        bool isExpired = false,
        bool isRevoked = false,
        List<InviteUsageInfo>? usedBy = null) => new(
        Guid.CreateVersion7(),
        _tenantId,
        "Chris",
        "Chris",
        [],
        [Scope.GlucoseRead],
        "Dr. Smith",
        false,
        DateTime.UtcNow.AddDays(7),
        null,
        0,
        isValid,
        isExpired,
        isRevoked,
        DateTime.UtcNow,
        usedBy ?? []);

    /// <summary>
    /// The invitee's browser resolves the tenant by host, and the invite belongs to exactly one
    /// tenant. Looking a token up unbounded would let tenant A's link be redeemed on tenant B.
    /// </summary>
    [Fact]
    public async Task GetInviteInfo_scopesTheLookupToTheRequestTenant()
    {
        _inviteService.Setup(s => s.GetInviteByTokenAsync("tok", _tenantId)).ReturnsAsync(Invite());

        var controller = BuildController();

        var result = await controller.GetInviteInfo("tok", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _inviteService.Verify(s => s.GetInviteByTokenAsync("tok", _tenantId), Times.Once);
    }

    /// <summary>
    /// The join page decides between "accept" and "register" from this. Session cookies are
    /// domain-wide, so an invitee who already follows another patient on the instance arrives
    /// signed in — and the general session state reports them unauthenticated here, because they
    /// are not a member of the tenant they are being invited to.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetInviteInfo_reportsTheSignedInViewer(bool alreadyMember)
    {
        _inviteService.Setup(s => s.GetInviteByTokenAsync("tok", _tenantId)).ReturnsAsync(Invite());
        _tenantMemberService
            .Setup(s => s.IsMemberAsync(_callerSubjectId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(alreadyMember);

        var controller = BuildController();

        var result = await controller.GetInviteInfo("tok", CancellationToken.None);

        var info = result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<MemberInviteInfo>().Subject;
        info.Viewer.Should().NotBeNull();
        info.Viewer!.SubjectId.Should().Be(_callerSubjectId);
        info.Viewer.IsMember.Should().Be(alreadyMember);
    }

    /// <summary>
    /// The invitee with no account at all is the case that already worked; the page must still
    /// offer them registration.
    /// </summary>
    [Fact]
    public async Task GetInviteInfo_withoutASubject_reportsNoViewer()
    {
        _inviteService.Setup(s => s.GetInviteByTokenAsync("tok", _tenantId)).ReturnsAsync(Invite());

        var controller = BuildController();
        controller.HttpContext.Items["AuthContext"] = AuthContext.Unauthenticated();

        var result = await controller.GetInviteInfo("tok", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<MemberInviteInfo>().Which.Viewer.Should().BeNull();
        _tenantMemberService.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The accept page names the tenant and the inviter and lists what is being granted, so an
    /// invite that can still be accepted keeps returning all of it — the redaction below must not
    /// reach the case the page exists for.
    /// </summary>
    [Fact]
    public async Task GetInviteInfo_forAnAcceptableInvite_returnsWhatTheAcceptPageNeeds()
    {
        _inviteService.Setup(s => s.GetInviteByTokenAsync("tok", _tenantId)).ReturnsAsync(Invite());

        var controller = BuildController();

        var result = await controller.GetInviteInfo("tok", CancellationToken.None);

        var info = result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<MemberInviteInfo>().Subject;
        info.TenantName.Should().Be("Chris");
        info.CreatedByName.Should().Be("Chris");
        info.DirectPermissions.Should().Equal(Scope.GlucoseRead);
        info.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    /// <summary>
    /// The token is the only thing gating this endpoint, and a link outlives the invite behind it.
    /// An invite that can no longer be accepted answers with the reason alone: the record names the
    /// tenant, the inviter, the roles and permissions being granted, and every subject that has
    /// already joined through it.
    /// <para>
    /// 400, like the acceptance refusal: the generated client passes a 400 ProblemDetails through
    /// to the join page, where the reason is shown; other 4xx statuses collapse to the generic
    /// message. The reason is asserted on the title because openapi-remote-codegen 0.2.0 resolves a
    /// ProblemDetails to <c>title</c> before <c>detail</c>.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true, false, "This invite has expired.")]
    [InlineData(false, true, "This invite has been revoked.")]
    [InlineData(false, false, "This invite has reached its maximum uses.")]
    public async Task GetInviteInfo_forAnUnacceptableInvite_returnsTheReasonAndNothingElse(
        bool isExpired, bool isRevoked, string expectedReason)
    {
        var joinedSubjectId = Guid.CreateVersion7();
        var invite = Invite(
            isValid: false,
            isExpired: isExpired,
            isRevoked: isRevoked,
            usedBy: [new InviteUsageInfo(joinedSubjectId, "Prior Joiner", DateTime.UtcNow)])
            with
        { TenantName = "Acme Clinic", CreatedByName = "Invite Author" };
        _inviteService.Setup(s => s.GetInviteByTokenAsync("tok", _tenantId)).ReturnsAsync(invite);

        var controller = BuildController();

        var result = await controller.GetInviteInfo("tok", CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var details = problem.Value.Should().BeOfType<ProblemDetails>().Subject;
        details.Title.Should().Be(expectedReason);
        details.Detail.Should().Be(expectedReason);

        var body = JsonSerializer.Serialize(problem.Value);
        body.Should().NotContain("Acme Clinic");
        body.Should().NotContain("Invite Author");
        body.Should().NotContain("Prior Joiner");
        body.Should().NotContain(joinedSubjectId.ToString());
        body.Should().NotContain(Scope.GlucoseRead);
    }

    /// <summary>
    /// Membership is not consulted for an invite nobody can accept — the viewer block exists to
    /// let the page offer acceptance.
    /// </summary>
    [Fact]
    public async Task GetInviteInfo_forAnUnacceptableInvite_doesNotReportTheViewer()
    {
        _inviteService
            .Setup(s => s.GetInviteByTokenAsync("tok", _tenantId))
            .ReturnsAsync(Invite(isValid: false, isExpired: true));

        var controller = BuildController();

        await controller.GetInviteInfo("tok", CancellationToken.None);

        _tenantMemberService.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The acceptance writes a membership, so it is bounded by the tenant the request resolved to
    /// rather than by the tenant the token names.
    /// </summary>
    [Fact]
    public async Task AcceptInvite_scopesTheAcceptanceToTheRequestTenant()
    {
        _inviteService
            .Setup(s => s.AcceptInviteAsync("tok", _callerSubjectId, _tenantId))
            .ReturnsAsync(new AcceptMemberInviteResult(true, MembershipId: Guid.CreateVersion7()));

        var controller = BuildController();

        var result = await controller.AcceptInvite("tok");

        result.Should().BeOfType<OkObjectResult>();
        _inviteService.Verify(s => s.AcceptInviteAsync("tok", _callerSubjectId, _tenantId), Times.Once);
    }

    /// <summary>
    /// The reason is written for the invitee, and openapi-remote-codegen 0.2.0 resolves a
    /// ProblemDetails to <c>title</c> before <c>detail</c> — so a reason carried only in the detail
    /// reaches them as the literal "Bad Request". Asserting the title is what makes the reason the
    /// invitee actually reads testable.
    /// </summary>
    [Fact]
    public async Task AcceptInvite_surfacesTheRefusalReason()
    {
        _inviteService
            .Setup(s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(new AcceptMemberInviteResult(
                false, "already_member", "You are already a member of this tenant."));

        var controller = BuildController();

        var result = await controller.AcceptInvite("tok");

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var details = problem.Value.Should().BeOfType<ProblemDetails>().Subject;
        details.Title.Should().Be("You are already a member of this tenant.");
        details.Detail.Should().Be("You are already a member of this tenant.");
    }

    /// <summary>
    /// <see cref="InviteTokenAuthorizedAttribute"/> suspends the tenant-membership requirement for
    /// the action it marks, and an exempted endpoint carries no permission gate of its own — the
    /// invite token is the only thing authorizing the caller. Pin the exact set: a third endpoint
    /// acquiring the marker is a non-member gaining a capability, and would be invisible in review.
    /// </summary>
    [Fact]
    public void OnlyTheJoinEndpointsCarryTheInviteTokenExemption()
    {
        var marked = typeof(MemberInviteController).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<InviteTokenAuthorizedAttribute>(inherit: true) != null)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .OrderBy(n => n)
            .ToList();

        marked.Should().Equal(
            $"{nameof(MemberInviteController)}.{nameof(MemberInviteController.AcceptInvite)}",
            $"{nameof(MemberInviteController)}.{nameof(MemberInviteController.GetInviteInfo)}");
    }

    /// <summary>
    /// The exemption is keyed on the <c>{token}</c> route value, so a marked action whose route
    /// does not carry one can never be exempted — and would silently 401 every invitee.
    /// </summary>
    [Fact]
    public void EveryExemptedEndpointTakesTheTokenInItsRoute()
    {
        var routesWithoutToken = typeof(MemberInviteController).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<InviteTokenAuthorizedAttribute>(inherit: true) != null)
            .Where(m => !m.GetCustomAttributes<HttpMethodAttribute>().Any(
                a => a.Template?.Contains(
                    $"{{{InviteTokenAuthorizedAttribute.TokenRouteValue}}}", StringComparison.Ordinal) == true))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        routesWithoutToken.Should().BeEmpty();
    }

    public void Dispose() => _dbContext.Dispose();
}
