using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public sealed class MemberRecoveryControllerTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly Mock<ITenantRoleService> _roles = new();
    private readonly Mock<IRecoveryCodeService> _recoveryCodes = new();
    private readonly Mock<ILoginCodeService> _loginCodes = new();
    private readonly MemberRecoveryController _controller;

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _adminId = Guid.CreateVersion7();
    private readonly Guid _orphanId = Guid.CreateVersion7();
    private readonly Guid _orphanMemberId = Guid.CreateVersion7();

    public MemberRecoveryControllerTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _context = _db.CreateContext(_tenantId);
        _context.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "acme", DisplayName = "Acme", IsActive = true });
        _context.Subjects.Add(new SubjectEntity { Id = _adminId, Name = "Admin", IsActive = true });
        _context.Subjects.Add(new SubjectEntity { Id = _orphanId, Name = "Orphan", Username = "orphan", IsActive = true });
        _context.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = _adminId });
        _context.TenantMembers.Add(new TenantMemberEntity { Id = _orphanMemberId, TenantId = _tenantId, SubjectId = _orphanId });
        _context.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(), SubjectId = _adminId, CredentialId = [9], PublicKey = [1], CreatedAt = DateTime.UtcNow,
        });
        _context.SaveChanges();

        _roles.Setup(r => r.GetEffectivePermissionsAsync(_orphanMemberId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Scope.GlucoseRead]);
        _loginCodes
            .Setup(l => l.IssueAsync(
                It.IsAny<NocturneDbContext>(), It.IsAny<Guid>(), It.IsAny<LoginCodePurpose>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<AuthAuditActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoginCode("issued", DateTime.UtcNow.AddHours(24)));

        _controller = Controller(AccountLinkRoutes.Off, new ControllerContext { HttpContext = new DefaultHttpContext() });
        SignIn(AuthType.SessionCookie, Scope.MembersManage, Scope.GlucoseRead);
    }

    private MemberRecoveryController Controller(IAccountLinkRoute linkRoute, ControllerContext context)
    {
        var contexts = _context.AsFactory();
        return new MemberRecoveryController(
            contexts,
            new TenantMembershipRepository(contexts, linkRoute),
            MockTenantAccessor.Create(_tenantId).Object,
            _roles.Object,
            new MemberRecoveryEligibility(new TenantMemberService(new TenantMembershipRepository(contexts, linkRoute), TimeProvider.System), linkRoute),
            _recoveryCodes.Object,
            _loginCodes.Object)
        {
            ControllerContext = context,
        };
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private void SignIn(AuthType authType, params string[] scopes)
    {
        var http = _controller.ControllerContext.HttpContext;
        http.RequestServices ??= TestRequestServices.Build(clamped: false);
        http.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true, AuthType = authType, SubjectId = _adminId, TenantId = _tenantId,
            SessionId = LiveSession.Id,
        };
        http.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(scopes);
    }

    private void NoCodeIssued() =>
        _loginCodes.Verify(l => l.IssueAsync(
            It.IsAny<NocturneDbContext>(), It.IsAny<Guid>(), It.IsAny<LoginCodePurpose>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<AuthAuditActor>(), It.IsAny<CancellationToken>()),
            Times.Never);

    private void GiveOrphanAnotherTenant()
    {
        var elsewhere = Guid.CreateVersion7();
        _context.Tenants.Add(new TenantEntity { Id = elsewhere, Slug = "elsewhere", DisplayName = "Elsewhere", IsActive = true });
        _context.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = elsewhere, SubjectId = _orphanId });
        _context.SaveChanges();
    }

    [Fact]
    public async Task An_administrator_gets_a_recovery_code_bound_to_the_locked_out_member()
    {
        var result = await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<LoginCode>();
        _loginCodes.Verify(l => l.IssueAsync(
            It.IsAny<NocturneDbContext>(), _orphanId, LoginCodePurpose.Recovery,
            It.IsAny<string?>(), It.IsAny<string?>(),
            It.Is<AuthAuditActor>(a => a.SubjectId == _adminId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_inactive_caller_is_refused_and_no_code_is_issued()
    {
        new RequestMembership(_adminId, _tenantId, null) { SubjectIsInactive = true }
            .StoreOn(_controller.ControllerContext.HttpContext);

        var result = await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None);

        result.Should().BeOfType<ChallengeResult>();
        NoCodeIssued();
    }

    [Fact]
    public async Task A_member_who_can_still_sign_in_cannot_be_given_a_code()
    {
        var result = await _controller.IssueRecoveryCode(_adminId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        NoCodeIssued();
    }

    [Fact]
    public async Task A_member_holding_recovery_codes_can_still_be_given_one_in_case_they_lost_them()
    {
        _recoveryCodes.Setup(r => r.GetRemainingCountAsync(_orphanId)).ReturnsAsync(3);

        var result = await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task A_member_of_another_tenant_is_left_to_the_operator()
    {
        GiveOrphanAnotherTenant();

        var result = await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        NoCodeIssued();
    }

    [Fact]
    public async Task A_platform_admin_is_left_to_the_operator()
    {
        var orphan = await _context.Subjects.FindAsync(_orphanId);
        orphan!.IsPlatformAdmin = true;
        await _context.SaveChangesAsync();

        var result = await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        NoCodeIssued();
    }

    [Theory]
    [InlineData(nameof(MemberRecoveryController.IssueRecoveryCode))]
    [InlineData(nameof(MemberRecoveryController.GetLockedOutMembers))]
    public void A_caller_without_member_management_is_refused(string action)
    {
        ActionGate.Evaluate(
                typeof(MemberRecoveryController),
                action,
                new HashSet<string> { Scope.GlucoseRead },
                new AuthContext { IsAuthenticated = true, AuthType = AuthType.SessionCookie })
            .ShouldBeScopeRefusal(Scope.MembersManage);
    }

    [Fact]
    public async Task A_caller_cannot_recover_a_member_holding_more_than_they_do()
    {
        _roles.Setup(r => r.GetEffectivePermissionsAsync(_orphanMemberId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Scope.FullAccess]);

        var result = await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        NoCodeIssued();
    }

    private void CallerAccessEndsAt(DateTime accessEndsAt) =>
        new RequestMembership(
                _adminId,
                _tenantId,
                new MembershipFacts(Guid.CreateVersion7(), new TenantMemberAccess(new HashSet<string> { Scope.MembersManage }, false, accessEndsAt), new TenantMemberAccess(new HashSet<string> { Scope.MembersManage }, false, accessEndsAt), null))
            .StoreOn(_controller.ControllerContext.HttpContext);

    private void OrphanAccessEndsAt(DateTime? accessEndsAt)
    {
        _context.TenantMembers.Single(m => m.Id == _orphanMemberId).AccessEndsAt = accessEndsAt;
        _context.SaveChanges();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(11)]
    public async Task A_caller_whose_access_ends_cannot_recover_a_member_whose_access_outlasts_theirs(int? orphanEndsInDays)
    {
        var now = DateTime.UtcNow;
        CallerAccessEndsAt(now.AddDays(10));
        OrphanAccessEndsAt(orphanEndsInDays is { } days ? now.AddDays(days) : null);

        var result = await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(HttpContextExtensions.AccessEndCeilingDetail);
        NoCodeIssued();
    }

    [Fact]
    public async Task A_caller_whose_access_ends_can_recover_a_member_whose_access_ends_no_later()
    {
        var now = DateTime.UtcNow;
        CallerAccessEndsAt(now.AddDays(10));
        OrphanAccessEndsAt(now.AddDays(10));

        (await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task A_history_clamped_caller_cannot_recover_a_member_who_sees_more_history()
    {
        CallerIsHistoryClamped();

        var result = await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(HttpContextExtensions.HistoryCeilingDetail);
        NoCodeIssued();
    }

    [Fact]
    public async Task A_mixed_member_cannot_recover_a_member_holding_what_only_the_team_gives()
    {
        _controller.HttpContext.SetGrantCeiling(new GrantCeiling(new HashSet<string> { Scope.MembersManage }, null, false));

        (await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await Listed()).Refusal.Should().Be(RecoveryIssueRefusal.MissingPermissions);
        NoCodeIssued();
    }

    [Fact]
    public async Task A_mixed_member_whose_direct_reason_is_clamped_or_ends_cannot_recover_a_member_who_outreaches_it()
    {
        var http = _controller.HttpContext;
        http.SetGrantCeiling(new GrantCeiling(new HashSet<string> { Scope.MembersManage, Scope.GlucoseRead }, null, true));
        (await Listed()).Refusal.Should().Be(RecoveryIssueRefusal.HistoryClamped);

        http.SetGrantCeiling(new GrantCeiling(new HashSet<string> { Scope.MembersManage, Scope.GlucoseRead }, DateTime.UtcNow.AddDays(10), false));
        (await Listed()).Refusal.Should().Be(RecoveryIssueRefusal.AccessOutlasts);
        (await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        NoCodeIssued();
    }

    [Fact]
    public async Task A_mixed_member_recovers_a_member_within_the_direct_reason()
    {
        _controller.HttpContext.SetGrantCeiling(new GrantCeiling(new HashSet<string> { Scope.MembersManage, Scope.GlucoseRead }, null, false));

        (await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task A_team_only_member_recovers_no_one()
    {
        _controller.HttpContext.SetGrantCeiling(null);

        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None));
        (await Listed()).Refusal.Should().Be(RecoveryIssueRefusal.MissingPermissions);
        NoCodeIssued();
    }

    private async Task<LockedOutMember> Listed() =>
        (await _controller.GetLockedOutMembers(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Which.Value.As<List<LockedOutMember>>().Single();

    private void CallerIsHistoryClamped() =>
        _controller.ControllerContext.HttpContext.RequestServices = TestRequestServices.HistoryClamped();

    private void OrphanIsLimitedTo24Hours()
    {
        _context.TenantMembers.Single(m => m.Id == _orphanMemberId).LimitTo24Hours = true;
        _context.SaveChanges();
    }

    private void CallerAccessEndsInTenDaysAndOrphanLastsLonger()
    {
        var now = DateTime.UtcNow;
        CallerAccessEndsAt(now.AddDays(10));
        OrphanAccessEndsAt(now.AddDays(11));
    }

    [Theory]
    [InlineData(RecoveryIssueRefusal.OperatorOnly)]
    [InlineData(RecoveryIssueRefusal.MissingPermissions)]
    [InlineData(RecoveryIssueRefusal.HistoryClamped)]
    [InlineData(RecoveryIssueRefusal.AccessOutlasts)]
    public async Task The_list_gives_the_reason_the_issue_refuses_with(RecoveryIssueRefusal refusal)
    {
        switch (refusal)
        {
            case RecoveryIssueRefusal.OperatorOnly:
                GiveOrphanAnotherTenant();
                break;
            case RecoveryIssueRefusal.MissingPermissions:
                _roles.Setup(r => r.GetEffectivePermissionsAsync(_orphanMemberId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync([Scope.FullAccess]);
                break;
            case RecoveryIssueRefusal.HistoryClamped:
                CallerIsHistoryClamped();
                break;
            case RecoveryIssueRefusal.AccessOutlasts:
                CallerAccessEndsInTenDaysAndOrphanLastsLonger();
                break;
        }

        var listed = await Listed();
        var issued = await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None);

        listed.Refusal.Should().Be(refusal);
        issued.Should().BeAssignableTo<IActionResult>().Which.Should().NotBeOfType<OkObjectResult>();
        NoCodeIssued();
    }

    [Fact]
    public async Task The_list_offers_a_code_exactly_when_the_issue_gives_one()
    {
        var listed = await Listed();
        var issued = await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None);

        listed.Refusal.Should().BeNull();
        issued.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task A_history_clamped_caller_may_recover_a_member_who_is_clamped_too()
    {
        CallerIsHistoryClamped();
        OrphanIsLimitedTo24Hours();

        var listed = await Listed();
        var issued = await _controller.IssueRecoveryCode(_orphanId, CancellationToken.None);

        listed.Refusal.Should().BeNull();
        issued.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task A_caller_whose_access_ends_can_recover_a_member_whose_access_ends_no_later_and_is_offered_it()
    {
        var now = DateTime.UtcNow;
        CallerAccessEndsAt(now.AddDays(10));
        OrphanAccessEndsAt(now.AddDays(10));

        (await Listed()).Refusal.Should().BeNull();
    }

    [Fact]
    public async Task A_member_with_no_end_date_outlasts_a_caller_whose_access_ends()
    {
        CallerAccessEndsAt(DateTime.UtcNow.AddDays(10));
        OrphanAccessEndsAt(null);

        (await Listed()).Refusal.Should().Be(RecoveryIssueRefusal.AccessOutlasts);
    }

    [Fact]
    public async Task The_operator_only_reason_comes_before_every_other()
    {
        GiveOrphanAnotherTenant();
        _roles.Setup(r => r.GetEffectivePermissionsAsync(_orphanMemberId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Scope.FullAccess]);
        CallerIsHistoryClamped();

        (await Listed()).Refusal.Should().Be(RecoveryIssueRefusal.OperatorOnly);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_member_with_a_confirmed_address_is_left_to_the_emailed_link_only_while_delivery_is_configured(
        bool delivery, bool lockedOut)
    {
        var orphan = await _context.Subjects.FindAsync(_orphanId);
        orphan!.SetEmail("orphan@example.test");
        orphan.ConfirmEmail("orphan@example.test", DateTime.UtcNow);
        await _context.SaveChangesAsync();
        var controller = Controller(AccountLinkRoutes.Of(delivery), _controller.ControllerContext);

        var listed = await controller.GetLockedOutMembers(CancellationToken.None);
        var issued = await controller.IssueRecoveryCode(_orphanId, CancellationToken.None);

        listed.Should().BeOfType<OkObjectResult>().Which.Value.As<List<LockedOutMember>>()
            .Select(m => m.SubjectId).Should().Equal(lockedOut ? [_orphanId] : []);
        if (lockedOut)
            issued.Should().BeOfType<OkObjectResult>();
        else
        {
            issued.Should().BeOfType<NotFoundResult>();
            NoCodeIssued();
        }
    }

    [Fact]
    public async Task The_locked_out_members_are_listed_with_who_can_recover_them()
    {
        _recoveryCodes.Setup(r => r.GetRemainingCountAsync(_orphanId)).ReturnsAsync(2);
        GiveOrphanAnotherTenant();

        var result = await _controller.GetLockedOutMembers(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeEquivalentTo(
            new[] { new LockedOutMember(_orphanId, "Orphan", "orphan", HasRecoveryCodes: true, Refusal: RecoveryIssueRefusal.OperatorOnly) });
    }
}
