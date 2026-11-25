using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.Identity;
using Xunit;
using Subject = Nocturne.Core.Models.Authorization.Subject;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Tests for the cross-tenant login guard in
/// <see cref="OidcAuthService.CompleteLoginAsync"/>. An OIDC identity resolves to a global
/// subject, so a valid external identity must still be a member of the tenant being logged
/// into before a session is issued. Without this gate, any external identity could mint a
/// session on any tenant's subdomain.
/// </summary>
public class OidcAuthServiceLoginGateTests
{
    private readonly Mock<ISubjectService> _subjectService = new();
    private readonly Mock<IOidcProviderService> _providerService = new();
    private readonly Mock<ISessionService> _sessionService = new();
    private readonly Mock<IJwtService> _jwtService = new();
    private readonly Mock<IRefreshTokenService> _refreshTokenService = new();
    private readonly Mock<IHttpClientFactory> _httpFactory = new();
    private readonly Mock<ITenantMemberService> _tenantMemberService = new();
    private readonly Mock<IMemberInviteService> _memberInviteService = new();
    private readonly Mock<ITenantCreationInviteService> _tenantCreationInvites = new();
    private readonly Mock<ITenantRepository> _tenants = new();
    private readonly Mock<ITotpService> _totpService = new();
    private readonly ListLogger<OidcAuthService> _logger = new();
    private readonly OidcAuthService _service;

    public OidcAuthServiceLoginGateTests()
    {
        // An unanswered mock reads as "a code is required"; these subjects have no authenticator.
        _totpService
            .Setup(t => t.GetSecondFactorRequirementAsync(It.IsAny<Guid>(), It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.NotEnrolled);

        var options = Options.Create(new OidcOptions());
        _service = new OidcAuthService(
            _providerService.Object,
            _subjectService.Object,
            _sessionService.Object,
            _jwtService.Object,
            _refreshTokenService.Object,
            _httpFactory.Object,
            _tenantMemberService.Object,
            _memberInviteService.Object,
            _tenantCreationInvites.Object,
            _tenants.Object,
            new SignInCompletion(_totpService.Object, _tenantMemberService.Object,
                _sessionService.Object, _subjectService.Object, new Mock<IAuthAuditService>().Object),
            new EphemeralDataProtectionProvider(),
            options,
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            _logger);
    }

    private static OidcAuthService.OidcStateData LoginState(string tenantSlug = "erik", string returnUrl = "/")
        => new()
        {
            Intent = "login",
            ReturnUrl = returnUrl,
            ProviderId = Guid.NewGuid(),
            Nonce = "n",
            TenantSlug = tenantSlug,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

    private static OidcProvider Provider()
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Google",
            IssuerUrl = "https://accounts.google.com",
            ClientId = "nocturne",
            IsEnabled = true,
        };

    private static OidcAuthService.OidcIdTokenClaims Claims()
        => new() { Sub = "google-123", Email = "user@example.com" };

    private Subject SetupResolvedSubject()
    {
        var subject = new Subject { Id = Guid.NewGuid(), Name = "Rhys", Email = "user@example.com" };
        _subjectService
            .Setup(s => s.FindFromOidcAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(subject);
        _subjectService
            .Setup(s => s.FindOrCreateFromOidcAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(subject);
        return subject;
    }

    private void SetupSessionIssuance()
    {
        _subjectService.Setup(s => s.UpdateLastLoginAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        _subjectService.Setup(s => s.GetSubjectPermissionsAsync(It.IsAny<Guid>())).ReturnsAsync(new List<string>());
        _subjectService.Setup(s => s.GetSubjectRolesAsync(It.IsAny<Guid>())).ReturnsAsync(new List<string>());
        _sessionService
            .Setup(s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access-token", "refresh-token", 3600, Guid.Empty));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteSetupAsync_LinksTheIdentityAndAnswersItsProviderAndSessionIdWithoutSigningIn()
    {
        var provider = Provider();
        var ownerId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var state = LoginState(returnUrl: "/setup");
        state.Intent = "setup";
        state.SubjectId = ownerId;
        _subjectService
            .Setup(s => s.AttachSetupOwnerOidcIdentityAsync(
                tenantId, ownerId, provider.Id, "google-123", provider.IssuerUrl, "user@example.com", false))
            .ReturnsAsync(OidcLinkOutcome.Created);
        _subjectService.Setup(s => s.GetSubjectByIdAsync(ownerId)).ReturnsAsync(new Subject { Id = ownerId, Name = "Owner" });

        var claims = Claims();
        claims.Sid = "provider-sid";

        var result = await _service.CompleteSetupAsync(state, provider, claims, tenantId);

        result.Success.Should().BeTrue(result.ErrorDescription);
        result.SubjectId.Should().Be(ownerId);
        result.ProviderId.Should().Be(provider.Id);
        result.OidcSessionId.Should().Be("provider-sid");
        result.ReturnUrl.Should().Be("/setup");
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the caller ends setup before the sign-in issues a session");
        _subjectService.Verify(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteSetupAsync_WhenAnotherCompletionHasClaimedTheSetup_AnswersSetupAlreadyCompleteAndAdoptsNothing()
    {
        var provider = Provider();
        var ownerId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var state = LoginState(returnUrl: "/setup");
        state.Intent = "setup";
        state.SubjectId = ownerId;
        state.TenantSlug = "my-instance";
        _subjectService
            .Setup(s => s.AttachSetupOwnerOidcIdentityAsync(
                tenantId, ownerId, provider.Id, "google-123", provider.IssuerUrl, "user@example.com", false))
            .ReturnsAsync((OidcLinkOutcome?)null);

        var result = await _service.CompleteSetupAsync(state, provider, Claims(), tenantId);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("setup_already_complete");
        result.SubjectId.Should().BeNull();
        _subjectService.Verify(
            s => s.AdoptForSetupAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        _subjectService.Verify(
            s => s.AttachOidcIdentityAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_WhenSubjectIsNotMemberOfTenant_DeniesWithoutIssuingSession()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _service.CompleteLoginAsync(
            LoginState(), Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        result.Success.Should().BeFalse();
        result.IsAccessDenied.Should().BeTrue("a non-member must be denied a session");
        result.SubjectId.Should().Be(subject.Id);
        result.Tokens.Should().BeNull("no session may be issued for a non-member");

        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the cross-tenant login must not mint a session");
    }

    /// <summary>
    /// The invitee's OIDC identity is a member of nothing the first time they use it, so the
    /// membership gate alone bounced them back to a login page that could not help — the callback
    /// returns to /join still signed out, and the page offers registration again. The invite token
    /// carried in the login's own return URL is the authorization to join, so it is accepted here.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_whenTheLoginCameFromAValidJoinLink_issuesSessionWithoutJoining()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _memberInviteService
            .Setup(s => s.GetInviteByTokenAsync("invite-token", tenantId))
            .ReturnsAsync(ValidInvite(tenantId));
        SetupSessionIssuance();

        var result = await _service.CompleteLoginAsync(
            LoginState(returnUrl: "/join?token=invite-token"),
            Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        result.Success.Should().BeTrue();
        result.IsAccessDenied.Should().BeFalse();
        _memberInviteService.Verify(
            s => s.GetInviteByTokenAsync("invite-token", tenantId),
            Times.Once,
            "the invite is looked up against the tenant the callback resolved to, not the token's");
    }

    /// <summary>
    /// The login endpoint is anonymous and takes its return URL from the query string, so joining
    /// here would let anyone navigate a victim to a join link of their choosing and have the silent
    /// IdP round-trip write the membership. The session is issued; the join stays behind the Accept
    /// button.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_fromAJoinLink_neverWritesTheMembershipItself()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _memberInviteService
            .Setup(s => s.GetInviteByTokenAsync("invite-token", tenantId))
            .ReturnsAsync(ValidInvite(tenantId));
        SetupSessionIssuance();

        await _service.CompleteLoginAsync(
            LoginState(returnUrl: "/join?token=invite-token"),
            Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        _memberInviteService.Verify(
            s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()),
            Times.Never);
    }

    /// <summary>
    /// The token is the whole of the authorization, so a refusal from the invite service — expired,
    /// revoked, exhausted, or minted for another tenant — leaves the login exactly as denied as it
    /// was before.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_whenTheJoinLinkInviteIsRefused_deniesWithoutIssuingSession()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _memberInviteService
            .Setup(s => s.GetInviteByTokenAsync(It.IsAny<string>(), It.IsAny<Guid>()))
            .ReturnsAsync((MemberInviteInfo?)null);

        var result = await _service.CompleteLoginAsync(
            LoginState(returnUrl: "/join?token=someone-elses-token"),
            Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        result.Success.Should().BeFalse();
        result.IsAccessDenied.Should().BeTrue();
        result.Tokens.Should().BeNull();

        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Only the join link carries an invite; every other return URL leaves the membership gate as
    /// the sole answer, so an ordinary login cannot reach the invite path at all.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("/")]
    [InlineData("/reports?token=invite-token")]
    [InlineData("/joinery?token=invite-token")]
    [InlineData("/join")]
    public async Task CompleteLoginAsync_whenTheReturnUrlIsNotAJoinLink_neverConsultsTheInvite(string returnUrl)
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _service.CompleteLoginAsync(
            LoginState(returnUrl: returnUrl), Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        result.IsAccessDenied.Should().BeTrue();
        _memberInviteService.VerifyNoOtherCalls();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_WhenSubjectIsMemberOfTenant_IssuesSession()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        SetupSessionIssuance();

        var result = await _service.CompleteLoginAsync(
            LoginState(), Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        result.Success.Should().BeTrue();
        result.IsAccessDenied.Should().BeFalse();
        result.Tokens.Should().NotBeNull();
        result.StepUpToken.Should().BeNull();

        _sessionService.Verify(
            s => s.IssueSessionAsync(subject.Id, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_OnSuccess_LogsTheSubjectAndProviderIdsAndNoPersonalData()
    {
        var subject = SetupResolvedSubject();
        var provider = Provider();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        SetupSessionIssuance();

        var result = await _service.CompleteLoginAsync(
            LoginState(), provider, Claims(), Guid.NewGuid(), ipAddress: null, userAgent: null);

        result.Success.Should().BeTrue();
        var logged = string.Join(" ", _logger.Entries.Select(e => e.Message));
        logged.Should().Contain(subject.Id.ToString());
        logged.Should().Contain(provider.Id.ToString());
        logged.Should().NotContain(subject.Email!);
        logged.Should().NotContain(subject.Name);
        logged.Should().NotContain(Claims().Sub);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_WhenSubjectHasAnAuthenticator_WithholdsTheSessionForTheSecondFactor()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        var provider = Provider();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _totpService
            .Setup(t => t.GetSecondFactorRequirementAsync(subject.Id, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        _totpService.Setup(t => t.CreateStepUpTokenAsync(subject.Id, tenantId, false)).ReturnsAsync("step-up-token");
        SetupSessionIssuance();

        var result = await _service.CompleteLoginAsync(
            LoginState(returnUrl: "/reports"), provider, Claims(), tenantId, ipAddress: null, userAgent: null);

        result.Success.Should().BeFalse();
        result.StepUpToken.Should().Be("step-up-token");
        result.Tokens.Should().BeNull();
        result.SubjectId.Should().Be(subject.Id);
        result.ProviderId.Should().Be(provider.Id);
        result.ReturnUrl.Should().Be("/reports");

        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _subjectService.Verify(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_FromATrustedDevice_IssuesTheSessionWithoutAStepUpToken()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _totpService
            .Setup(t => t.GetSecondFactorRequirementAsync(subject.Id, "device-token"))
            .ReturnsAsync(SecondFactorRequirement.SatisfiedByTrustedDevice);
        SetupSessionIssuance();

        var result = await _service.CompleteLoginAsync(
            LoginState(), Provider(), Claims(), tenantId, ipAddress: null, userAgent: null,
            trustedDeviceToken: "device-token");

        result.Success.Should().BeTrue();
        result.UsedTrustedDevice.Should().BeTrue();
        result.StepUpToken.Should().BeNull();
        _totpService.Verify(
            t => t.CreateStepUpTokenAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_WhenANonMemberPresentsATrustedDevice_IsStillDenied()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _totpService
            .Setup(t => t.GetSecondFactorRequirementAsync(subject.Id, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.SatisfiedByTrustedDevice);

        var result = await _service.CompleteLoginAsync(
            LoginState(), Provider(), Claims(), tenantId, ipAddress: null, userAgent: null,
            trustedDeviceToken: "device-token");

        result.IsAccessDenied.Should().BeTrue("a trusted device replaces the code, not the membership check");
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_WhenANonMemberHasAnAuthenticator_IsDeniedBeforeAStepUpTokenIsMinted()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _totpService
            .Setup(t => t.GetSecondFactorRequirementAsync(subject.Id, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);

        var result = await _service.CompleteLoginAsync(
            LoginState(), Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        result.IsAccessDenied.Should().BeTrue();
        result.StepUpToken.Should().BeNull();
        _totpService.Verify(
            t => t.CreateStepUpTokenAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_WhenAnInviteeHasAnAuthenticator_BindsTheInviteToTheStepUpToken()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _memberInviteService
            .Setup(s => s.GetInviteByTokenAsync("invite-token", tenantId))
            .ReturnsAsync(ValidInvite(tenantId));
        _totpService
            .Setup(t => t.GetSecondFactorRequirementAsync(subject.Id, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        _totpService.Setup(t => t.CreateStepUpTokenAsync(subject.Id, tenantId, true)).ReturnsAsync("step-up-token");

        var result = await _service.CompleteLoginAsync(
            LoginState(returnUrl: "/join?token=invite-token"),
            Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        result.StepUpToken.Should().Be("step-up-token");
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_OnATenantlessHostWithAnAuthenticator_BindsTheTokenToNoTenant()
    {
        var subject = SetupResolvedSubject();
        _totpService
            .Setup(t => t.GetSecondFactorRequirementAsync(subject.Id, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        _totpService.Setup(t => t.CreateStepUpTokenAsync(subject.Id, null, false)).ReturnsAsync("step-up-token");

        var result = await _service.CompleteLoginAsync(
            LoginState(tenantSlug: null!), Provider(), Claims(), currentTenantId: null, ipAddress: null, userAgent: null);

        result.StepUpToken.Should().Be("step-up-token");
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_WhenStateHasNoSlugButATenantResolved_StillChecksMembership()
    {
        // The crossed pair, and the reachable one: a login started on the apex mints a state
        // with no slug, but the redirect URI is fixed, so the callback can be delivered to
        // {tenant}.{basedomain} where that tenant resolves. Gating the check on the slug rather
        // than on the resolved tenant would let a non-member take a session on that subdomain.
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _service.CompleteLoginAsync(
            LoginState(tenantSlug: null!), Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        result.Success.Should().BeFalse();
        result.IsAccessDenied.Should().BeTrue();

        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "an apex-minted state replayed at a tenant subdomain must not mint a session for a non-member");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_WhenStateNamesATenantButNoneResolved_IsDenied()
    {
        // Nothing to check membership against, so the login is unverifiable rather than tenantless.
        var subject = SetupResolvedSubject();

        var result = await _service.CompleteLoginAsync(
            LoginState(tenantSlug: "erik"), Provider(), Claims(), currentTenantId: null, ipAddress: null, userAgent: null);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("invalid_state");

        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>An invite of <paramref name="tenantId"/> that is currently acceptable.</summary>
    private static MemberInviteInfo ValidInvite(Guid tenantId) => new(
        Id: Guid.NewGuid(),
        TenantId: tenantId,
        TenantName: "Test Tenant",
        CreatedByName: "Owner",
        RoleIds: [Guid.NewGuid()],
        DirectPermissions: null,
        Label: "Dr. Smith",
        LimitTo24Hours: false,
        ExpiresAt: DateTime.UtcNow.AddDays(7),
        MaxUses: null,
        UseCount: 0,
        IsValid: true,
        IsExpired: false,
        IsRevoked: false,
        CreatedAt: DateTime.UtcNow,
        UsedBy: [],
        RoleNames: ["Viewer"],
        Permissions: [], null);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_WhenNoTenantResolved_IssuesSession()
    {
        // Single-tenant / unresolved-tenant deployments have no subdomain tenant to gate on.
        var subject = SetupResolvedSubject();
        SetupSessionIssuance();

        var result = await _service.CompleteLoginAsync(
            LoginState(tenantSlug: null!), Provider(), Claims(), currentTenantId: null, ipAddress: null, userAgent: null);

        result.Success.Should().BeTrue();
        result.IsAccessDenied.Should().BeFalse();

        _tenantMemberService.Verify(
            t => t.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "with no resolved tenant there is nothing to check membership against");
        _sessionService.Verify(
            s => s.IssueSessionAsync(subject.Id, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_ForADeactivatedMember_FailsWithoutASessionOrAStepUpToken()
    {
        var subject = SetupResolvedSubject();
        subject.IsActive = false;
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _totpService
            .Setup(t => t.GetSecondFactorRequirementAsync(subject.Id, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        SetupSessionIssuance();

        var result = await _service.CompleteLoginAsync(
            LoginState(), Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        result.Success.Should().BeFalse();
        result.IsAccessDenied.Should().BeFalse("a deactivated account is not offered the membership request");
        result.Error.Should().Be("callback_failed");
        result.StepUpToken.Should().BeNull();
        _totpService.Verify(
            t => t.CreateStepUpTokenAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>()), Times.Never);
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _subjectService.Verify(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_ForADeactivatedSubjectWithNoTenantResolved_Fails()
    {
        var subject = SetupResolvedSubject();
        subject.IsActive = false;
        SetupSessionIssuance();

        var result = await _service.CompleteLoginAsync(
            LoginState(tenantSlug: null!), Provider(), Claims(), currentTenantId: null, ipAddress: null, userAgent: null);

        result.Success.Should().BeFalse();
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void SetupFirstTimeIdentity() =>
        _subjectService
            .Setup(s => s.FindFromOidcAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((Subject?)null);

    private void VerifyNothingWrittenForTheIdentity()
    {
        _subjectService.Verify(
            s => s.FindOrCreateFromOidcAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>?>()),
            Times.Never);
        _subjectService.Verify(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()), Times.Never);
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_ForAnUnseenIdentityThatIsNotAMember_IsRefusedWithoutASubject()
    {
        SetupFirstTimeIdentity();
        var provider = Provider();

        var result = await _service.CompleteLoginAsync(
            LoginState(), provider, Claims(), Guid.NewGuid(), ipAddress: null, userAgent: null);

        result.IsAccessDenied.Should().BeTrue();
        result.SubjectId.Should().BeNull();
        result.ProviderId.Should().Be(provider.Id);
        result.ExternalIdentityDigest.Should().Be(
            OidcAuthService.ExternalIdentityDigest(provider.IssuerUrl, Claims().Sub));
        result.ExternalIdentityDigest.Should().NotContain(Claims().Sub);
        VerifyNothingWrittenForTheIdentity();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_ForAKnownIdentityThatIsNotAMember_LeavesItsSubjectAsItWas()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _service.CompleteLoginAsync(
            LoginState(), Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        result.IsAccessDenied.Should().BeTrue();
        result.SubjectId.Should().Be(subject.Id);
        result.ExternalIdentityDigest.Should().BeNull("the subject id already names the person");
        VerifyNothingWrittenForTheIdentity();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_ForARefusalWithNoTenantResolved_LogsNoTenantProperty()
    {
        SetupFirstTimeIdentity();

        var result = await _service.CompleteLoginAsync(
            LoginState(tenantSlug: ""), Provider(), Claims(), currentTenantId: null, ipAddress: null, userAgent: null);

        result.IsAccessDenied.Should().BeTrue();
        var refusal = _logger.WarningEvents.Should().ContainSingle(e => e.Message.Contains("is not admitted")).Which;
        refusal.Message.Should().EndWith("(no tenant resolved)");
        refusal.Properties.Should().NotContainKey("TenantId");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_ForARefusalOnATenant_LogsTheTenant()
    {
        SetupFirstTimeIdentity();
        var tenantId = Guid.NewGuid();

        await _service.CompleteLoginAsync(
            LoginState(), Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        var refusal = _logger.WarningEvents.Should().ContainSingle(e => e.Message.Contains("is not admitted")).Which;
        refusal.Message.Should().EndWith($"to tenant {tenantId}");
        refusal.Properties.Should().Contain("TenantId", tenantId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_ForARefusalAtCompletion_LogsTheTenantItWasRefusedFrom()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .SetupSequence(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);

        var result = await _service.CompleteLoginAsync(
            LoginState(), Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        result.IsAccessDenied.Should().BeTrue();
        result.RefusalAudited.Should().BeTrue();
        var refusal = _logger.WarningEvents.Should().ContainSingle(e => e.Message.Contains("refused at completion")).Which;
        refusal.Message.Should().EndWith($"to tenant {tenantId}");
        refusal.Properties.Should().Contain("TenantId", tenantId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ExternalIdentityDigest_IsStablePerIssuerAndSub_AndDoesNotHoldTheSub()
    {
        var digest = OidcAuthService.ExternalIdentityDigest("https://idp.example.com", "alice@example.com");

        digest.Should().Be(OidcAuthService.ExternalIdentityDigest("https://idp.example.com", "alice@example.com"));
        digest.Should().NotBe(OidcAuthService.ExternalIdentityDigest("https://other.example.com", "alice@example.com"));
        digest.Should().NotBe(OidcAuthService.ExternalIdentityDigest("https://idp.example.com", "bob@example.com"));
        digest.Should().MatchRegex("^[0-9a-f]{64}$");
        digest.Should().NotContain("alice");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_WhenStateNamesATenantButNoneResolved_WritesNothingForTheIdentity()
    {
        SetupFirstTimeIdentity();

        var result = await _service.CompleteLoginAsync(
            LoginState(tenantSlug: "erik"), Provider(), Claims(), currentTenantId: null, ipAddress: null, userAgent: null);

        result.Error.Should().Be("invalid_state");
        VerifyNothingWrittenForTheIdentity();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_ForAnUnseenIdentityWithAValidJoinLink_CreatesItsSubjectWithTheProvidersDefaultRoles()
    {
        SetupFirstTimeIdentity();
        var created = new Subject { Id = Guid.NewGuid(), Name = "Invitee" };
        var provider = Provider();
        provider.DefaultRoles = ["provider-default"];
        _subjectService
            .Setup(s => s.FindOrCreateFromOidcAsync(
                provider.Id, Claims().Sub, provider.IssuerUrl,
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<string?>(), provider.DefaultRoles))
            .ReturnsAsync(created);
        var tenantId = Guid.NewGuid();
        _memberInviteService
            .Setup(s => s.GetInviteByTokenAsync("invite-token", tenantId))
            .ReturnsAsync(ValidInvite(tenantId));
        SetupSessionIssuance();

        var result = await _service.CompleteLoginAsync(
            LoginState(returnUrl: "/join?token=invite-token"), provider, Claims(), tenantId,
            ipAddress: null, userAgent: null);

        result.Success.Should().BeTrue();
        _sessionService.Verify(
            s => s.IssueSessionAsync(created.Id, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _tenantMemberService.Verify(
            t => t.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "an identity with no subject is a member of nothing");
    }

    private static TenantRecord SoleTenant(Guid id) => new(
        Id: id, Slug: "sole", DisplayName: "Sole", IsActive: true, IsDemo: false, AllowAccessRequests: false,
        AllowPublicDocs: false, OnboardingCompletedAt: null, SetupHubEnrolledAt: null, SetupStripDismissedRevision: null,
        PatientRelationship: null, HandedOverAt: null, DefaultGlucoseUnits: null, LastReadingAt: null,
        ShareTokenHash: null, ShareTokenEncrypted: null, ShareTokenSetAt: null, ShareLastAccessedAt: null,
        SysCreatedAt: DateTime.UtcNow, SysUpdatedAt: DateTime.UtcNow, DemoConfig: null);

    /// <summary>A first-time identity whose subject the sign-in creates, with the provider's default roles.</summary>
    private (OidcProvider Provider, Subject Created) SetupCreatableFirstTimeIdentity()
    {
        SetupFirstTimeIdentity();
        var provider = Provider();
        provider.DefaultRoles = ["provider-default"];
        var created = new Subject { Id = Guid.NewGuid(), Name = "Newcomer" };
        _subjectService
            .Setup(s => s.FindOrCreateFromOidcAsync(
                provider.Id, Claims().Sub, provider.IssuerUrl,
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<string?>(), provider.DefaultRoles))
            .ReturnsAsync(created);
        SetupSessionIssuance();
        return (provider, created);
    }

    private Task<OidcCallbackResult> SignInOnTheApexAsync(OidcProvider provider, string returnUrl) =>
        _service.CompleteLoginAsync(
            LoginState(tenantSlug: null!, returnUrl: returnUrl), provider, Claims(), currentTenantId: null,
            ipAddress: null, userAgent: null);

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("/")]
    [InlineData("/settings/account")]
    [InlineData("/api/auth/platform-access?tenant=erik")]
    public async Task CompleteLoginAsync_OnTheApexForAnUnseenIdentityWithNoLink_IsRefusedWithoutASubject(string returnUrl)
    {
        var (provider, _) = SetupCreatableFirstTimeIdentity();

        var result = await SignInOnTheApexAsync(provider, returnUrl);

        result.IsAccessDenied.Should().BeTrue();
        result.SubjectId.Should().BeNull();
        result.ProviderId.Should().Be(provider.Id);
        result.ExternalIdentityDigest.Should().Be(
            OidcAuthService.ExternalIdentityDigest(provider.IssuerUrl, Claims().Sub));
        VerifyNothingWrittenForTheIdentity();
        _tenants.Verify(t => t.FindSoleTenantAsync(It.IsAny<CancellationToken>()), Times.Never);
        _tenantCreationInvites.Verify(
            t => t.GetInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_OnTheApexForAnUnseenIdentityWithAUsableTenantCreationLink_CreatesItsSubjectWithTheProvidersDefaultRoles()
    {
        var (provider, created) = SetupCreatableFirstTimeIdentity();
        _tenantCreationInvites
            .Setup(t => t.GetInfoAsync("creation-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<TenantCreationInviteInfo>.Ok(
                new TenantCreationInviteInfo("nocturne.example.com", "Admin", DateTime.UtcNow.AddDays(1))));

        var result = await SignInOnTheApexAsync(provider, "/start?token=creation-token&slug=erik&name=Erik");

        result.Success.Should().BeTrue();
        _sessionService.Verify(
            s => s.IssueSessionAsync(created.Id, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_OnTheApexForAnUnseenIdentityWithARefusedTenantCreationLink_IsRefusedWithoutASubject()
    {
        var (provider, _) = SetupCreatableFirstTimeIdentity();
        _tenantCreationInvites
            .Setup(t => t.GetInfoAsync("creation-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<TenantCreationInviteInfo>.Refused(
                TenantInviteRefusalCode.UsedUp, "This invite link was already used."));

        var result = await SignInOnTheApexAsync(provider, "/start?token=creation-token");

        result.IsAccessDenied.Should().BeTrue();
        VerifyNothingWrittenForTheIdentity();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_OnTheApexForAnUnseenIdentityWithAJoinLinkOfTheSoleTenant_CreatesItsSubject()
    {
        var (provider, created) = SetupCreatableFirstTimeIdentity();
        var soleId = Guid.NewGuid();
        _tenants.Setup(t => t.FindSoleTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(SoleTenant(soleId));
        _memberInviteService
            .Setup(s => s.GetInviteByTokenAsync("invite-token", soleId))
            .ReturnsAsync(ValidInvite(soleId));

        var result = await SignInOnTheApexAsync(provider, "/join?token=invite-token");

        result.Success.Should().BeTrue();
        _sessionService.Verify(
            s => s.IssueSessionAsync(created.Id, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_OnTheApexOfSeveralTenantsForAnUnseenIdentityWithAJoinLink_IsRefusedWithoutReadingTheInvite()
    {
        var (provider, _) = SetupCreatableFirstTimeIdentity();
        _tenants.Setup(t => t.FindSoleTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync((TenantRecord?)null);

        var result = await SignInOnTheApexAsync(provider, "/join?token=invite-token");

        result.IsAccessDenied.Should().BeTrue();
        VerifyNothingWrittenForTheIdentity();
        _memberInviteService.Verify(
            s => s.GetInviteByTokenAsync(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_OnTheApexForAnUnseenIdentityWithAnUnknownJoinLinkOfTheSoleTenant_IsRefusedWithoutASubject()
    {
        var (provider, _) = SetupCreatableFirstTimeIdentity();
        var soleId = Guid.NewGuid();
        _tenants.Setup(t => t.FindSoleTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(SoleTenant(soleId));

        var result = await SignInOnTheApexAsync(provider, "/join?token=invite-token");

        result.IsAccessDenied.Should().BeTrue();
        VerifyNothingWrittenForTheIdentity();
    }
}
