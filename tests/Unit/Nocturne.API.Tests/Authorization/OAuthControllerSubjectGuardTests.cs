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
/// The consent, device-approval and grant-management endpoints all act on behalf of a subject, and
/// each one refuses a caller it cannot resolve to one. The refusal, its status and its payload are
/// pinned at every endpoint behind the guard rather than at one of them, because they share a single
/// implementation: a change there reaches all of these surfaces at once.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "OAuth")]
public class OAuthControllerSubjectGuardTests
{
    private const string ClientId = "test-client-id";
    private const string RedirectUri = "org.nightscout.trio://oauth/callback";
    private const string CodeChallenge = "a-code-challenge";

    private readonly Mock<IOAuthClientService> _clientService = new();
    private readonly Mock<IOAuthGrantService> _grantService = new();

    public OAuthControllerSubjectGuardTests()
    {
        _grantService
            .Setup(s => s.GetGrantsForSubjectAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _clientService
            .Setup(s => s.GetClientAsync(ClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthClientInfo
            {
                Id = Guid.CreateVersion7(),
                ClientId = ClientId,
                DisplayName = "Trio",
            });
        _clientService
            .Setup(s => s.ValidateRedirectUriAsync(ClientId, RedirectUri, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    /// <summary>
    /// Every endpoint that resolves the caller to a subject, invoked with arguments that reach the
    /// guard. <c>GET /api/oauth/authorize</c> is absent because it answers an unauthenticated
    /// caller with a redirect to login rather than a refusal; it is covered separately below.
    /// </summary>
    public static TheoryData<string, Func<OAuthController, Task<ActionResult?>>> GuardedEndpoints =>
        new()
        {
            {
                "POST /api/oauth/authorize",
                async c => await c.ApproveConsent(new ConsentApprovalRequest
                {
                    ClientId = ClientId,
                    RedirectUri = RedirectUri,
                    Scope = Scope.GlucoseRead,
                    CodeChallenge = CodeChallenge,
                    Approved = true,
                })
            },
            {
                "POST /api/oauth/device-approve",
                async c => await c.DeviceApprove(new DeviceApprovalRequest
                {
                    UserCode = "ABCD-EFGH",
                    Approved = true,
                })
            },
            { "GET /api/oauth/grants", async c => (await c.GetGrants()).Result },
            { "DELETE /api/oauth/grants/id", async c => await c.DeleteGrant(Guid.CreateVersion7()) },
            {
                "PATCH /api/oauth/grants/id",
                async c => (await c.UpdateGrant(Guid.CreateVersion7(), new UpdateGrantRequest())).Result
            },
            {
                "POST /api/oauth/introspect",
                async c => (await c.Introspect("header.payload.signature")).Result
            },
        };

    [Theory]
    [MemberData(nameof(GuardedEndpoints))]
    public async Task An_unauthenticated_caller_is_refused(
        string endpoint, Func<OAuthController, Task<ActionResult?>> invoke)
    {
        var result = await invoke(CreateController(authContext: null));

        RefusalOf(result, endpoint).ErrorDescription.Should().Be("User is not authenticated.");
    }

    /// <summary>
    /// The public share subject is carried as an unauthenticated context with a subject id
    /// attached, so the gate has to read the flag rather than the presence of an id.
    /// </summary>
    [Theory]
    [MemberData(nameof(GuardedEndpoints))]
    public async Task An_unauthenticated_caller_carrying_a_subject_id_is_refused(
        string endpoint, Func<OAuthController, Task<ActionResult?>> invoke)
    {
        var result = await invoke(CreateController(new AuthContext
        {
            IsAuthenticated = false,
            AuthType = AuthType.None,
            SubjectId = Guid.CreateVersion7(),
        }));

        RefusalOf(result, endpoint).ErrorDescription.Should().Be("User is not authenticated.");
    }

    [Theory]
    [MemberData(nameof(GuardedEndpoints))]
    public async Task An_authenticated_caller_with_no_subject_is_refused(
        string endpoint, Func<OAuthController, Task<ActionResult?>> invoke)
    {
        var result = await invoke(CreateController(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.Guest,
            SubjectId = null,
            ActingAsSubjectId = Guid.CreateVersion7(),
        }));

        RefusalOf(result, endpoint).ErrorDescription
            .Should().Be("Could not determine authenticated user.");
    }

    /// <summary>
    /// The guard resolves the caller's own <see cref="AuthContext.SubjectId"/>, never
    /// <see cref="AuthContext.EffectiveSubjectId"/>. A follower acting on a data owner's behalf
    /// carries both, and these endpoints are the follower's own consent, devices and grants — an
    /// approval attributed to the data owner would hand the follower's client an authorization the
    /// data owner never gave.
    /// </summary>
    [Fact]
    public async Task A_follower_resolves_to_its_own_subject_not_the_owner_it_acts_for()
    {
        var callerSubjectId = Guid.CreateVersion7();
        var dataOwnerSubjectId = Guid.CreateVersion7();

        var result = await CreateController(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = callerSubjectId,
            ActingAsSubjectId = dataOwnerSubjectId,
        }).GetGrants();

        result.Result.Should().BeOfType<OkObjectResult>();
        _grantService.Verify(
            s => s.GetGrantsForSubjectAsync(callerSubjectId, It.IsAny<CancellationToken>()),
            Times.Once);
        _grantService.Verify(
            s => s.GetGrantsForSubjectAsync(
                It.Is<Guid>(id => id != callerSubjectId), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// <c>GET /api/oauth/authorize</c> is the one entry point a browser reaches without a session:
    /// an unauthenticated caller is sent to login with the OAuth parameters preserved, not refused.
    /// </summary>
    [Fact]
    public async Task The_authorize_entry_point_sends_an_unauthenticated_caller_to_login()
    {
        var result = await Authorize(CreateController(authContext: null));

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().StartWith("/auth/login?returnUrl=");
    }

    [Fact]
    public async Task The_authorize_entry_point_refuses_an_authenticated_caller_with_no_subject()
    {
        var result = await Authorize(CreateController(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.Guest,
            SubjectId = null,
            ActingAsSubjectId = Guid.CreateVersion7(),
        }));

        RefusalOf(result, "GET /api/oauth/authorize").ErrorDescription
            .Should().Be("Could not determine authenticated user.");
    }

    private static Task<ActionResult> Authorize(OAuthController controller) => controller.Authorize(
        client_id: ClientId,
        redirect_uri: RedirectUri,
        response_type: "code",
        scope: Scope.GlucoseRead,
        state: "opaque-state",
        code_challenge: CodeChallenge,
        code_challenge_method: "S256");

    private static OAuthError RefusalOf(ActionResult? result, string endpoint)
    {
        var unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>(
            "{0} refuses a caller it cannot resolve to a subject", endpoint).Subject;
        var error = unauthorized.Value.Should().BeOfType<OAuthError>().Subject;
        error.Error.Should().Be("access_denied");
        return error;
    }

    private OAuthController CreateController(AuthContext? authContext)
    {
        var httpContext = new DefaultHttpContext();
        if (authContext != null)
        {
            httpContext.Items["AuthContext"] = authContext;
        }

        return new OAuthController(
            _clientService.Object,
            _grantService.Object,
            Mock.Of<IOAuthTokenService>(),
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
}
