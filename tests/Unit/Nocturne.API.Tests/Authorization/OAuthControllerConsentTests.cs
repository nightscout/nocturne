using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Models.OAuth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Unit tests for the consent-approval endpoint (<c>POST /api/oauth/authorize</c>), covering the
/// order in which the client and its redirect URI are validated, and the ceiling on what the
/// approval may delegate. Both the approve and the deny path end in a redirect to
/// <c>redirect_uri</c>, so both need the URI proven to belong to the client before anything is
/// emitted; and the approved scopes are bounded by the approver's own scopes on the tenant, so a
/// consent screen asking for <c>*</c> cannot mint more than the approver holds.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "OAuth")]
public class OAuthControllerConsentTests
{
    private const string ClientId = "test-client-id";
    private const string RegisteredRedirectUri = "org.nightscout.trio://oauth/callback";
    private const string UnregisteredRedirectUri = "https://not-the-client.example/collect";

    private readonly Guid _clientEntityId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    private readonly Mock<IOAuthClientService> _clientService = new();
    private readonly Mock<IOAuthTokenService> _tokenService = new();

    /// <summary>Scopes the controller handed the token service, captured on issue.</summary>
    private readonly List<string> _issuedScopes = [];

    public OAuthControllerConsentTests()
    {
        _clientService
            .Setup(s => s.GetClientAsync(ClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthClientInfo
            {
                Id = _clientEntityId,
                ClientId = ClientId,
                DisplayName = "Trio",
            });
        _clientService
            .Setup(s => s.ValidateRedirectUriAsync(
                ClientId, RegisteredRedirectUri, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _clientService
            .Setup(s => s.ValidateRedirectUriAsync(
                ClientId, UnregisteredRedirectUri, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _tokenService
            .Setup(s => s.GenerateAuthorizationCodeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, IEnumerable<string>, string, string, bool, CancellationToken>(
                (_, _, scopes, _, _, _, _) => _issuedScopes.AddRange(scopes))
            .ReturnsAsync("the-code");
    }

    /// <param name="approverScopes">
    /// The approver's resolved scopes on the tenant, as <c>MemberScopeMiddleware</c> leaves them.
    /// Defaults to superuser, which is the tenant owner doing the approving.
    /// </param>
    private OAuthController CreateController(params string[] approverScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _subjectId,
        };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(
            approverScopes.Length > 0 ? approverScopes : [Scope.FullAccess]);

        return new OAuthController(
            _clientService.Object,
            Mock.Of<IOAuthGrantService>(),
            _tokenService.Object,
            Mock.Of<IOAuthDeviceCodeService>(),
            Mock.Of<ISubjectService>(),
            Mock.Of<IJwtService>(),
            Mock.Of<IOAuthTokenRevocationCache>(),
            NullLogger<OAuthController>.Instance
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static ConsentApprovalRequest Consent(
        bool approved, string redirectUri, string scope = Scope.GlucoseRead) => new()
    {
        ClientId = ClientId,
        RedirectUri = redirectUri,
        Scope = scope,
        CodeChallenge = "a-code-challenge",
        State = "opaque-state",
        Approved = approved,
    };

    [Fact]
    public async Task Denial_with_an_unregistered_redirect_uri_is_rejected_not_redirected()
    {
        var result = await CreateController().ApproveConsent(Consent(false, UnregisteredRedirectUri));

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().BeOfType<OAuthError>()
            .Which.Error.Should().Be("invalid_request");
    }

    [Fact]
    public async Task Approval_with_an_unregistered_redirect_uri_is_rejected()
    {
        var result = await CreateController().ApproveConsent(Consent(true, UnregisteredRedirectUri));

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Denial_with_the_registered_redirect_uri_redirects_with_access_denied()
    {
        var result = await CreateController().ApproveConsent(Consent(false, RegisteredRedirectUri));

        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        redirect.Url.Should().StartWith(RegisteredRedirectUri);
        redirect.Url.Should().Contain("error=access_denied");
        redirect.Url.Should().Contain("state=opaque-state");
    }

    [Fact]
    public async Task Approval_with_the_registered_redirect_uri_issues_a_code()
    {
        var result = await CreateController().ApproveConsent(Consent(true, RegisteredRedirectUri));

        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        redirect.Url.Should().StartWith(RegisteredRedirectUri);
        redirect.Url.Should().Contain("code=the-code");
    }

    [Fact]
    public async Task Approving_full_access_issues_only_what_the_approver_holds()
    {
        // A user cannot delegate more than they hold. Approving a consent screen that asked
        // for "*" previously minted a full-access token whatever the approver's own
        // permissions on the tenant were — turning any member into a superuser via their
        // own browser. The demo makes that reachable anonymously, since its shared visitor
        // account is a real member anyone can get a session for.
        var controller = CreateController(Scope.GlucoseRead, Scope.TreatmentsRead);

        var result = await controller.ApproveConsent(
            Consent(true, RegisteredRedirectUri, Scope.FullAccess));

        result.Should().BeOfType<RedirectResult>();
        _issuedScopes.Should().BeEquivalentTo([Scope.GlucoseRead, Scope.TreatmentsRead]);
    }

    [Fact]
    public async Task Approving_a_scope_the_approver_lacks_drops_it()
    {
        var controller = CreateController(Scope.GlucoseRead);

        var result = await controller.ApproveConsent(Consent(
            true, RegisteredRedirectUri, $"{Scope.GlucoseRead} {Scope.TreatmentsReadWrite}"));

        result.Should().BeOfType<RedirectResult>();
        _issuedScopes.Should().BeEquivalentTo([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task Approving_when_none_of_the_scopes_are_held_is_rejected()
    {
        var controller = CreateController(Scope.GlucoseRead);

        var result = await controller.ApproveConsent(
            Consent(true, RegisteredRedirectUri, Scope.TreatmentsReadWrite));

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().BeOfType<OAuthError>()
            .Which.Error.Should().Be("invalid_scope");
        _tokenService.Verify(
            s => s.GenerateAuthorizationCodeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
