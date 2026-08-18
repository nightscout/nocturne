using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class PasskeyControllerTests : IDisposable
{
    /// <summary>
    /// Hands out contexts over the test's own SQLite connection, so the real
    /// <see cref="TenantMemberService"/> sees the seeded rows.
    /// </summary>
    private sealed class SharedSqliteFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<IPasskeyService> _passkeyService;
    private readonly Mock<ITotpService> _totpService;
    private readonly Mock<IRecoveryCodeService> _recoveryCodeService;
    private readonly Mock<IJwtService> _jwtService;
    private readonly Mock<ISessionService> _sessionService;
    private readonly Mock<ISubjectService> _subjectService;
    private readonly Mock<ITenantAccessor> _tenantAccessor;
    private readonly Mock<ITenantService> _tenantService;
    private readonly PasskeyController _controller;

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    public PasskeyControllerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(_dbOptions);
        _dbContext.Database.EnsureCreated();

        _passkeyService = new Mock<IPasskeyService>();
        _totpService = new Mock<ITotpService>();
        _recoveryCodeService = new Mock<IRecoveryCodeService>();
        _jwtService = new Mock<IJwtService>();
        _sessionService = new Mock<ISessionService>();
        _subjectService = new Mock<ISubjectService>();
        _tenantAccessor = new Mock<ITenantAccessor>();
        _tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);
        _tenantAccessor.Setup(t => t.IsResolved).Returns(true);

        var oidcOptions = Options.Create(new OidcOptions
        {
            Cookie = new CookieSettings
            {
                AccessTokenName = ".Nocturne.AccessToken",
                RefreshTokenName = ".Nocturne.RefreshToken",
                Secure = true,
            },
        });

        var logger = new Mock<ILogger<PasskeyController>>();

        var auditService = new Mock<IAuthAuditService>();

        _tenantService = new Mock<ITenantService>();

        _controller = new PasskeyController(
            _passkeyService.Object,
            _totpService.Object,
            _recoveryCodeService.Object,
            _jwtService.Object,
            _sessionService.Object,
            _subjectService.Object,
            auditService.Object,
            _tenantAccessor.Object,
            _tenantService.Object,
            // The real service, not a mock: the enrolment probe's cross-tenant reach and its
            // revoked-membership filtering are the properties under test, and a mock would
            // assert the mock.
            new TenantMemberService(new SharedSqliteFactory(_dbOptions)),
            _dbContext,
            new SharedSqliteFactory(_dbOptions),
            oidcOptions,
            logger.Object);

        // Set up HttpContext with response cookies
        var httpContext = new DefaultHttpContext();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds an active subject that is a member of the given tenant (this one by default),
    /// optionally holding a passkey credential.
    /// </summary>
    private async Task<Guid> SeedMemberAsync(string username, bool withPasskey = false, Guid? tenantId = null)
    {
        var resolvedTenantId = tenantId ?? _tenantId;
        await EnsureTenantAsync(resolvedTenantId);

        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = username,
            Username = username,
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = resolvedTenantId,
            SubjectId = subjectId,
        });

        if (withPasskey)
        {
            _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = subjectId,
                CredentialId = Guid.CreateVersion7().ToByteArray(),
                PublicKey = [1, 2, 3],
                CreatedAt = DateTime.UtcNow,
            });
        }

        await _dbContext.SaveChangesAsync();
        return subjectId;
    }

    private async Task EnsureTenantAsync(Guid tenantId)
    {
        if (await _dbContext.Set<TenantEntity>().AnyAsync(t => t.Id == tenantId))
            return;

        // The whole id, not a prefix: two v7 GUIDs minted in the same millisecond share their
        // leading hex digits, so a prefix collides on the unique slug.
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = tenantId,
            Slug = "t" + tenantId.ToString("N"),
            DisplayName = "Tenant",
        });
        await _dbContext.SaveChangesAsync();
    }

    /// <summary>Puts an authenticated subject on the request, as the auth middleware would.</summary>
    private void Authenticate(Guid? subjectId = null) =>
        _controller.ControllerContext.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = subjectId ?? _subjectId,
            TenantId = _tenantId,
        };

    /// <summary>Presents a recovery-session cookie that the JWT service accepts for this subject.</summary>
    private void GiveRecoverySession(Guid subjectId, params string[] permissions)
    {
        const string token = "recovery-token";
        _controller.ControllerContext.HttpContext.Request.Headers.Cookie =
            $".Nocturne.RecoverySession={token}";
        _jwtService
            .Setup(s => s.ValidateAccessToken(token))
            .Returns(JwtValidationResult.Success(new JwtClaims
            {
                SubjectId = subjectId,
                Permissions = [.. permissions],
            }));
    }

    private void StubRegistrationOptions(Guid subjectId, string username) =>
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(subjectId, username))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

    #region Passkey enrolment is bound to the caller

    [Fact]
    public async Task RegisterOptions_WhenAnonymous_ReturnsUnauthorizedWithoutMintingAChallenge()
    {
        var result = await _controller.RegisterOptions(new PasskeyRegisterOptionsRequest { Username = "testuser" });

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(401);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>()),
            Times.Never,
            "an anonymous caller must not be able to start an enrolment ceremony");
    }

    [Fact]
    public async Task RegisterComplete_WhenAnonymous_ReturnsUnauthorizedWithoutStoringACredential()
    {
        var result = await _controller.RegisterComplete(new PasskeyRegisterCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "some-token",
        });

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(401);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Never,
            "an anonymous caller must not be able to store a credential");
    }

    [Fact]
    public async Task RegisterOptions_UsesTheAuthenticatedSubject()
    {
        Authenticate();
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(_subjectId, "testuser"))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

        var result = await _controller.RegisterOptions(new PasskeyRegisterOptionsRequest { Username = "testuser" });

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PasskeyOptionsResponse>(okResult.Value);
        response.Options.Should().Contain("challenge");
        response.ChallengeToken.Should().Be("token-data");
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(_subjectId, "testuser"),
            Times.Once);
    }

    [Fact]
    public async Task RegisterComplete_BindsTheChallengeToTheAuthenticatedSubject()
    {
        var victimSubjectId = Guid.CreateVersion7();
        Authenticate();
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), _subjectId, It.IsAny<string?>()))
            .ReturnsAsync(new PasskeyCredentialResult(Guid.CreateVersion7(), _subjectId));

        var result = await _controller.RegisterComplete(new PasskeyRegisterCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "token-for-victim",
        });

        Assert.IsType<OkObjectResult>(result.Result);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), _subjectId, It.IsAny<string?>()),
            Times.Once,
            "the enrolling subject is the session's, so a challenge minted for another subject is refused");
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), victimSubjectId, It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterOptions_EmptyUsername_ReturnsBadRequest()
    {
        Authenticate();

        var result = await _controller.RegisterOptions(new PasskeyRegisterOptionsRequest { Username = "" });

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task RegisterOptions_WhenAuthenticated_BindsToTheAuthenticatedSubject()
    {
        // Adding a passkey to your own account. The username on the request is not the
        // authority — the session is — so naming someone else changes nothing.
        var callerId = await SeedMemberAsync("caller", withPasskey: true);
        var victimId = await SeedMemberAsync("victim", withPasskey: true);
        Authenticate(callerId);
        StubRegistrationOptions(callerId, "victim");

        var result = await _controller.RegisterOptions(
            new PasskeyRegisterOptionsRequest { Username = "victim" });

        Assert.IsType<OkObjectResult>(result.Result);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(callerId, "victim"), Times.Once);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(victimId, It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RegisterOptions_WithARecoverySession_BindsToTheCookieSubject()
    {
        // Re-registering after spending a recovery code: the account still holds its old
        // credential, so only the recovery session makes this allowed.
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        GiveRecoverySession(subjectId, "passkey:manage");
        StubRegistrationOptions(subjectId, "owner");

        var result = await _controller.RegisterOptions(
            new PasskeyRegisterOptionsRequest { Username = "owner" });

        Assert.IsType<OkObjectResult>(result.Result);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(subjectId, "owner"), Times.Once);
    }

    [Fact]
    public async Task RegisterOptions_WithARecoverySessionLackingPasskeyManage_IsRefused()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        GiveRecoverySession(subjectId, "glucose.read");

        var result = await _controller.RegisterOptions(
            new PasskeyRegisterOptionsRequest { Username = "owner" });

        Assert.Equal(401, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterComplete_WithARecoverySession_BindsToTheCookieSubject()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        GiveRecoverySession(subjectId, "passkey:manage");
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ReturnsAsync(new PasskeyCredentialResult(Guid.CreateVersion7(), subjectId));

        var result = await _controller.RegisterComplete(new PasskeyRegisterCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "token",
        });

        Assert.IsType<OkObjectResult>(result.Result);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), subjectId, It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task RegisterComplete_WithARecoverySession_SpendsTheCookie()
    {
        // One recovery code buys one enrolment: the credential exists now, so the cookie that
        // authorized it must not authorize a second.
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        GiveRecoverySession(subjectId, "passkey:manage");
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ReturnsAsync(new PasskeyCredentialResult(Guid.CreateVersion7(), subjectId));

        await _controller.RegisterComplete(new PasskeyRegisterCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "token",
        });

        var setCookie = _controller.ControllerContext.HttpContext.Response.Headers.SetCookie
            .Single(header => header!.StartsWith(".Nocturne.RecoverySession=", StringComparison.Ordinal));
        setCookie.Should().Contain("expires=Thu, 01 Jan 1970");
    }

    [Fact]
    public async Task RegisterComplete_NoChallengeToken_ReturnsBadRequest()
    {
        Authenticate();

        var request = new PasskeyRegisterCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "",
        };

        var result = await _controller.RegisterComplete(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    #endregion

    #region Recovery-mode enrolment

    [Fact]
    public async Task RecoveryModeOptions_WhenTenantIsNotInRecoveryMode_IsRefused()
    {
        // A tenant whose only member has a passkey is not in recovery mode.
        await SeedMemberAsync("rhys", withPasskey: true);

        var result = await _controller.RecoveryModeOptions(new PasskeyLoginOptionsRequest { Username = "rhys" });

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(400);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RecoveryModeOptions_WhenTheNamedAccountStillHasAPasskey_IsRefused()
    {
        // Recovery mode is active because of the orphan, but the named account is not the orphan.
        var withPasskey = await SeedMemberAsync("rhys", withPasskey: true);
        await SeedMemberAsync("orphan", withPasskey: false);
        _subjectService
            .Setup(s => s.CountPrimaryAuthFactorsAsync(withPasskey))
            .ReturnsAsync(1);

        var result = await _controller.RecoveryModeOptions(new PasskeyLoginOptionsRequest { Username = "rhys" });

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(400);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>()),
            Times.Never,
            "an account that can still sign in must not be enrollable without a session");
    }

    [Fact]
    public async Task RecoveryModeOptions_ForALockedOutAccount_MintsAChallengeForTheResolvedSubject()
    {
        await SeedMemberAsync("rhys", withPasskey: true);
        var orphanId = await SeedMemberAsync("orphan", withPasskey: false);
        _subjectService
            .Setup(s => s.CountPrimaryAuthFactorsAsync(orphanId))
            .ReturnsAsync(0);
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(orphanId, "orphan"))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

        var result = await _controller.RecoveryModeOptions(new PasskeyLoginOptionsRequest { Username = "orphan" });

        Assert.IsType<OkObjectResult>(result.Result);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(orphanId, "orphan"),
            Times.Once,
            "the subject comes from the server's lookup, not from the request");
    }

    [Fact]
    public async Task RecoveryModeOptions_ForAnotherTenantsMember_IsRefused()
    {
        // Subjects are global; membership is what scopes them. A locked-out subject in another
        // tenant must not be claimable from this one.
        await SeedMemberAsync("rhys", withPasskey: true);
        await SeedMemberAsync("orphan", withPasskey: false);
        var elsewhereId = await SeedMemberAsync("elsewhere", tenantId: Guid.CreateVersion7());
        _subjectService.Setup(s => s.CountPrimaryAuthFactorsAsync(elsewhereId)).ReturnsAsync(0);

        var result = await _controller.RecoveryModeOptions(new PasskeyLoginOptionsRequest { Username = "elsewhere" });

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(elsewhereId, It.IsAny<string>()),
            Times.Never);
    }

    #endregion

    #region Anonymous enrolment binds to a server-resolved subject

    [Fact]
    public async Task InviteComplete_WithAChallengeForAnotherSubject_IsRefused()
    {
        var victimId = await SeedMemberAsync("rhys", withPasskey: true);
        await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(victimId);

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "invitee",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-rhys",
            },
            inviteService.Object);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(400);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), victimId, It.IsAny<string?>()),
            Times.Never,
            "the enrolling subject comes from the server's lookup, not from the challenge token");
        inviteService.Verify(
            s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()),
            Times.Never,
            "a refused enrolment must not join anyone to the tenant");
    }

    [Fact]
    public async Task InviteComplete_ForAnAccountThatCanAlreadySignIn_IsRefused()
    {
        // Naming an existing member resolves nothing: only a subject with no sign-in method that
        // is not yet a member of this tenant can be enrolled anonymously.
        await SeedMemberAsync("rhys", withPasskey: true);
        var inviteService = StubValidInvite();

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "rhys",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-token",
            },
            inviteService.Object);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(400);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task InviteComplete_WithAChallengeForTheResolvedInvitee_Succeeds()
    {
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(inviteeId);
        _recoveryCodeService.Setup(s => s.GenerateCodesAsync(inviteeId)).ReturnsAsync(["code-1"]);
        _sessionService
            .Setup(s => s.IssueSessionAsync(inviteeId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900));

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "invitee",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-invitee",
            },
            inviteService.Object);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<PasskeyRegistrationResponse>(ok.Value).Success.Should().BeTrue();
        inviteService.Verify(s => s.AcceptInviteAsync("invite-token", inviteeId, _tenantId), Times.Once);
    }

    [Fact]
    public async Task AccessRequestComplete_WithAChallengeForAnotherSubject_IsRefused()
    {
        var victimId = await SeedMemberAsync("rhys", withPasskey: true);
        await AllowAccessRequestsAsync();
        await SeedPendingAccessRequestAsync("Sam Smith");
        StubRegistrationChallengeMintedFor(victimId);

        var result = await _controller.AccessRequestComplete(
            new AccessRequestCompleteRequest
            {
                DisplayName = "Sam Smith",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-rhys",
            },
            new Mock<IInAppNotificationService>().Object);

        var objectResult = Assert.IsType<ObjectResult>(result);
        objectResult.StatusCode.Should().Be(400);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), victimId, It.IsAny<string?>()),
            Times.Never,
            "the enrolling subject comes from the server's lookup, not from the challenge token");
    }

    [Fact]
    public async Task AccessRequestComplete_WithAChallengeForTheResolvedRequestor_Succeeds()
    {
        await AllowAccessRequestsAsync();
        var requestorId = await SeedPendingAccessRequestAsync("Sam Smith");
        StubRegistrationChallengeMintedFor(requestorId);

        var result = await _controller.AccessRequestComplete(
            new AccessRequestCompleteRequest
            {
                DisplayName = "Sam Smith",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-sam",
            },
            new Mock<IInAppNotificationService>().Object);

        Assert.IsType<OkResult>(result);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), requestorId, It.IsAny<string?>()),
            Times.Once);
    }

    /// <summary>
    /// Subjects are global; membership is what scopes them. A credential-less member of another
    /// tenant is that tenant's locked-out account, not an abandoned enrolment here, so an invite
    /// naming their username enrols a fresh subject and leaves theirs untouched.
    /// </summary>
    [Fact]
    public async Task InviteOptions_ForAnotherTenantsMember_DoesNotBindToThatSubject()
    {
        var elsewhereId = await SeedMemberAsync("elsewhere", tenantId: Guid.CreateVersion7());
        var inviteService = StubValidInvite();
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), "elsewhere"))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

        var result = await _controller.InviteOptions(
            new InviteOptionsRequest { Token = "invite-token", Username = "elsewhere", DisplayName = "Attacker" },
            inviteService.Object);

        Assert.IsType<OkObjectResult>(result.Result);
        var victim = await _dbContext.Subjects.AsNoTracking().FirstAsync(s => s.Id == elsewhereId);
        victim.Name.Should().Be("elsewhere", "another tenant's member must not be renamed by an invite here");
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(elsewhereId, It.IsAny<string>()),
            Times.Never,
            "the ceremony must not be bound to another tenant's member");
        var subjects = await _dbContext.Subjects.AsNoTracking()
            .Where(s => s.Username == "elsewhere").ToListAsync();
        subjects.Should().HaveCount(2, "the invite enrols a new subject rather than claiming theirs");
    }

    /// <summary>
    /// The completion half of the same claim: with no enrolling shell to resolve, the credential
    /// never reaches another tenant's member.
    /// </summary>
    [Fact]
    public async Task InviteComplete_ForAnotherTenantsMember_StoresNoCredential()
    {
        var elsewhereId = await SeedMemberAsync("elsewhere", tenantId: Guid.CreateVersion7());
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(elsewhereId);

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "elsewhere",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-elsewhere",
            },
            inviteService.Object);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), elsewhereId, It.IsAny<string?>()),
            Times.Never,
            "a member of any tenant is an account, not a half-finished enrolment");
        inviteService.Verify(
            s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()),
            Times.Never);
    }

    /// <summary>
    /// The enrolment probe asks each candidate separately whether it belongs to any tenant, so a
    /// candidate whose membership is in a tenant other than the resolved one is still excluded —
    /// the same answer the single anti-join gave, which is what stops one tenant enrolling a
    /// passkey onto another tenant's credential-less member.
    /// </summary>
    [Fact]
    public async Task InviteComplete_WhenTheOnlyCandidateBelongsToAnotherTenant_ResolvesNoSubject()
    {
        var elsewhereId = await SeedMemberAsync("shared-name", tenantId: Guid.CreateVersion7());
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(elsewhereId);

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "shared-name",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-elsewhere",
            },
            inviteService.Object);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), elsewhereId, It.IsAny<string?>()),
            Times.Never,
            "membership of any tenant, not just this one, disqualifies a candidate");
    }

    /// <summary>
    /// Walking candidates newest-first and taking the first with no membership must give the same
    /// answer as the newest candidate satisfying every condition at once: when the newest shares
    /// the username but belongs to a tenant, the older shell still resolves rather than the
    /// enrolment dead-ending.
    /// </summary>
    [Fact]
    public async Task InviteComplete_WhenTheNewestCandidateBelongsToATenant_ResolvesTheOlderShell()
    {
        var shell = await SeedEnrollingSubjectAsync("contested");
        var newerMember = await SeedMemberAsync("contested", tenantId: Guid.CreateVersion7());
        newerMember.CompareTo(shell).Should().BePositive("UUID v7 ids sort in creation order");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(shell);
        _recoveryCodeService.Setup(s => s.GenerateCodesAsync(shell)).ReturnsAsync(["code-1"]);
        _sessionService
            .Setup(s => s.IssueSessionAsync(shell, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900));

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "contested",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-shell",
            },
            inviteService.Object);

        Assert.IsType<OkObjectResult>(result.Result);
        inviteService.Verify(s => s.AcceptInviteAsync("invite-token", shell, _tenantId), Times.Once);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), newerMember, It.IsAny<string?>()),
            Times.Never);
    }

    /// <summary>
    /// A revoked membership does not disqualify a candidate: it carries no access, so the subject
    /// is the same empty shell as one that never had a membership. Current behaviour of the single
    /// anti-join too — the global <c>RevokedAt == null</c> filter excluded it there as well.
    /// </summary>
    [Fact]
    public async Task InviteComplete_WhenTheCandidatesOnlyMembershipIsRevoked_ResolvesThatSubject()
    {
        var revokedId = await SeedRevokedMemberAsync("returning", tenantId: Guid.CreateVersion7());
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(revokedId);
        _recoveryCodeService.Setup(s => s.GenerateCodesAsync(revokedId)).ReturnsAsync(["code-1"]);
        _sessionService
            .Setup(s => s.IssueSessionAsync(revokedId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900));

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "returning",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-returning",
            },
            inviteService.Object);

        Assert.IsType<OkObjectResult>(result.Result);
        inviteService.Verify(s => s.AcceptInviteAsync("invite-token", revokedId, _tenantId), Times.Once);
    }

    /// <summary>
    /// Duplicate subjects left behind by an older build of the options step are all credential-less
    /// shells that nobody can sign in as, so the completion resolves the newest — the one the
    /// caller's ceremony was minted against — rather than refusing the invite forever.
    /// </summary>
    [Fact]
    public async Task InviteComplete_WithDuplicateEnrollingSubjects_ResolvesTheNewest()
    {
        var (olderId, newestId) = OrderedIdPair();
        var older = await SeedEnrollingSubjectAsync("invitee", olderId);
        var newest = await SeedEnrollingSubjectAsync("invitee", newestId);
        newest.CompareTo(older).Should().BePositive("the pair must order the same way the query sorts");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(newest);
        _recoveryCodeService.Setup(s => s.GenerateCodesAsync(newest)).ReturnsAsync(["code-1"]);
        _sessionService
            .Setup(s => s.IssueSessionAsync(newest, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900));

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "invitee",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-invitee",
            },
            inviteService.Object);

        Assert.IsType<OkObjectResult>(result.Result);
        inviteService.Verify(s => s.AcceptInviteAsync("invite-token", newest, _tenantId), Times.Once);
    }

    /// <summary>
    /// Cancelling the OS prompt is the common failure, and it abandons the ceremony but not the
    /// subject the options step created. A retry has to land on that same subject: the completion
    /// step resolves the enrolling subject by username, so a second one under the same username
    /// makes the invite unfinishable.
    /// </summary>
    [Fact]
    public async Task InviteOptions_AfterAnAbandonedCeremony_ReusesTheSubject()
    {
        var inviteService = StubValidInvite();
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), "invitee"))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

        Assert.IsType<OkObjectResult>((await _controller.InviteOptions(
            new InviteOptionsRequest { Token = "invite-token", Username = "Invitee", DisplayName = "Sam" },
            inviteService.Object)).Result);
        Assert.IsType<OkObjectResult>((await _controller.InviteOptions(
            new InviteOptionsRequest { Token = "invite-token", Username = "invitee", DisplayName = "Sam Smith" },
            inviteService.Object)).Result);

        var subjects = await _dbContext.Subjects.AsNoTracking()
            .Where(s => s.Username == "invitee").ToListAsync();
        subjects.Should().ContainSingle("a retry must reuse the abandoned subject, not add another");
        subjects[0].Name.Should().Be("Sam Smith");

        StubRegistrationChallengeMintedFor(subjects[0].Id);
        _recoveryCodeService.Setup(s => s.GenerateCodesAsync(subjects[0].Id)).ReturnsAsync(["code-1"]);
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjects[0].Id, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900));

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "invitee",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-invitee",
            },
            inviteService.Object);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<PasskeyRegistrationResponse>(ok.Value).Success.Should().BeTrue();
        inviteService.Verify(s => s.AcceptInviteAsync("invite-token", subjects[0].Id, _tenantId), Times.Once);
    }

    /// <summary>
    /// The same abandoned-ceremony retry on the access-request flow. The pending subject holds no
    /// credential, so resuming it takes nothing over; the conflict is still reported for a request
    /// that actually finished registering.
    /// </summary>
    [Fact]
    public async Task AccessRequestOptions_AfterAnAbandonedCeremony_ReusesTheSubject()
    {
        await AllowAccessRequestsAsync();
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), "sam-smith"))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

        Assert.IsType<OkObjectResult>((await _controller.AccessRequestOptions(
            new AccessRequestOptionsRequest { DisplayName = "Sam Smith" })).Result);
        Assert.IsType<OkObjectResult>((await _controller.AccessRequestOptions(
            new AccessRequestOptionsRequest { DisplayName = "Sam Smith", Message = "second try" })).Result);

        var subjects = await _dbContext.Subjects.AsNoTracking()
            .Where(s => s.Name == "Sam Smith").ToListAsync();
        subjects.Should().ContainSingle("a retry must reuse the abandoned subject, not add another");
        subjects[0].AccessRequestMessage.Should().Be("second try");

        StubRegistrationChallengeMintedFor(subjects[0].Id);

        var result = await _controller.AccessRequestComplete(
            new AccessRequestCompleteRequest
            {
                DisplayName = "Sam Smith",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-sam",
            },
            new Mock<IInAppNotificationService>().Object);

        Assert.IsType<OkResult>(result);
    }

    /// <summary>
    /// A pending request that finished registering is a real request awaiting approval, not an
    /// abandoned ceremony, so a second one under the same name is still a conflict.
    /// </summary>
    [Fact]
    public async Task AccessRequestOptions_WhenTheNameHasAFinishedPendingRequest_IsAConflict()
    {
        await AllowAccessRequestsAsync();
        var requestorId = await SeedPendingAccessRequestAsync("Sam Smith");
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = requestorId,
            CredentialId = Guid.CreateVersion7().ToByteArray(),
            PublicKey = [1, 2, 3],
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.AccessRequestOptions(
            new AccessRequestOptionsRequest { DisplayName = "Sam Smith" });

        Assert.IsType<ConflictObjectResult>(result.Result);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>()),
            Times.Never);
    }

    /// <summary>
    /// Makes the passkey service behave as the real one does for a challenge token minted for
    /// <paramref name="subjectId"/>: it accepts that subject as the enrolling one and refuses
    /// every other.
    /// </summary>
    private void StubRegistrationChallengeMintedFor(Guid subjectId)
    {
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .ReturnsAsync((string _, string _, Guid _, Guid expectedSubjectId, string? _) =>
                expectedSubjectId == subjectId
                    ? new PasskeyCredentialResult(Guid.CreateVersion7(), subjectId)
                    : throw new InvalidOperationException(
                        "Registration challenge was not issued for the enrolling subject."));
    }

    private Mock<IMemberInviteService> StubValidInvite()
    {
        var inviteService = new Mock<IMemberInviteService>();
        inviteService.Setup(s => s.GetInviteByTokenAsync("invite-token", _tenantId))
            .ReturnsAsync(new MemberInviteInfo(
                Guid.CreateVersion7(), _tenantId, "Test", "Owner", [], null, null, false,
                DateTime.UtcNow.AddDays(1), null, 0, true, false, false, DateTime.UtcNow, []));
        inviteService.Setup(s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(new AcceptMemberInviteResult(true, MembershipId: Guid.CreateVersion7()));
        return inviteService;
    }

    /// <summary>
    /// Adds the subject that <c>invite/options</c> creates: active, no credentials, and not yet a
    /// member of the tenant.
    /// </summary>
    /// <summary>
    /// Seeds an active subject whose only membership — of another tenant — has been revoked, and
    /// returns the subject id. A revoked membership carries no access, so the subject is the same
    /// credential-less shell as one that never had a membership at all.
    /// </summary>
    private async Task<Guid> SeedRevokedMemberAsync(string username, Guid tenantId)
    {
        await EnsureTenantAsync(tenantId);

        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = username,
            Username = username,
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubjectId = subjectId,
            RevokedAt = DateTime.UtcNow.AddDays(-1),
        });
        await _dbContext.SaveChangesAsync();
        return subjectId;
    }

    /// <summary>
    /// Two ids that sort the same way under every representation the enrolling-subject query might
    /// order on: <see cref="Guid.CompareTo(Guid)"/>, the SQLite column these tests sort in, and the
    /// PostgreSQL <c>uuid</c> column production sorts in.
    /// </summary>
    /// <remarks>
    /// A pair of back-to-back <see cref="Guid.CreateVersion7"/> ids does not qualify.
    /// <c>Guid.CompareTo</c> compares the struct's fields, and the first two are read in
    /// little-endian order, so it byte-swaps the very timestamp bytes that make v7 sortable — two
    /// ids minted in the same millisecond then fall back to comparing random bits and order
    /// arbitrarily. Holding the whole v7 prefix fixed and varying only the trailing byte sidesteps
    /// that: the tail is the last field in the struct layout and the last byte of the serialized
    /// form, so every ordering agrees on it. That is exactly the tiebreak
    /// <c>OrderByDescending(s =&gt; s.Id)</c> resolves.
    /// </remarks>
    private static (Guid Older, Guid Newer) OrderedIdPair()
    {
        var bytes = Guid.CreateVersion7().ToByteArray();
        bytes[^1] = 0x01;
        var older = new Guid(bytes);
        bytes[^1] = 0x02;
        return (older, new Guid(bytes));
    }

    private async Task<Guid> SeedEnrollingSubjectAsync(string username, Guid? id = null)
    {
        var subjectId = id ?? Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = username,
            Username = username,
            IsActive = true,
            IsSystemSubject = false,
        });
        await _dbContext.SaveChangesAsync();
        return subjectId;
    }

    /// <summary>
    /// Adds the subject that <c>access-request/options</c> creates: pending and inactive.
    /// </summary>
    private async Task<Guid> SeedPendingAccessRequestAsync(string displayName)
    {
        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = displayName,
            Username = displayName.ToLowerInvariant().Replace(" ", "-"),
            IsActive = false,
            IsSystemSubject = false,
            ApprovalStatus = "Pending",
        });
        await _dbContext.SaveChangesAsync();
        return subjectId;
    }

    private async Task AllowAccessRequestsAsync()
    {
        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == _tenantId);
        if (tenant == null)
        {
            tenant = new TenantEntity { Id = _tenantId, Slug = "test", DisplayName = "Test" };
            _dbContext.Tenants.Add(tenant);
        }

        tenant.AllowAccessRequests = true;
        await _dbContext.SaveChangesAsync();
    }

    #endregion

    [Fact]
    public async Task LoginOptions_EmptyUsername_ReturnsBadRequest()
    {
        var request = new PasskeyLoginOptionsRequest { Username = "" };

        var result = await _controller.LoginOptions(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task LoginOptions_ValidRequest_CallsServiceAndReturnsOptionsWithToken()
    {
        _passkeyService
            .Setup(s => s.GenerateAssertionOptionsAsync("testuser", _tenantId))
            .ReturnsAsync(new PasskeyAssertionOptions("{\"challenge\":\"xyz\"}", "assertion-token"));

        var request = new PasskeyLoginOptionsRequest { Username = "testuser" };

        var result = await _controller.LoginOptions(request);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PasskeyOptionsResponse>(okResult.Value);
        Assert.Contains("challenge", response.Options);
        Assert.Equal("assertion-token", response.ChallengeToken);
        _passkeyService.Verify(s => s.GenerateAssertionOptionsAsync("testuser", _tenantId), Times.Once);
    }

    [Fact]
    public async Task DiscoverableLoginOptions_CallsServiceAndReturnsOptionsWithToken()
    {
        _passkeyService
            .Setup(s => s.GenerateDiscoverableAssertionOptionsAsync(_tenantId))
            .ReturnsAsync(new PasskeyAssertionOptions("{\"challenge\":\"disc\"}", "disc-token"));

        var result = await _controller.DiscoverableLoginOptions();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PasskeyOptionsResponse>(okResult.Value);
        Assert.Contains("challenge", response.Options);
        Assert.Equal("disc-token", response.ChallengeToken);
        _passkeyService.Verify(s => s.GenerateDiscoverableAssertionOptionsAsync(_tenantId), Times.Once);
    }

    [Fact]
    public async Task LoginComplete_NoChallengeToken_ReturnsBadRequest()
    {
        var request = new PasskeyLoginCompleteRequest { AssertionResponseJson = "{}", ChallengeToken = "" };

        var result = await _controller.LoginComplete(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    #region The passkey alone is not a session when a second factor is enrolled

    /// <summary>Stubs a passkey assertion that resolves to <paramref name="subjectId"/>.</summary>
    private void StubSuccessfulAssertion(Guid subjectId, string username) =>
        _passkeyService
            .Setup(s => s.CompleteAssertionAsync("{}", "assertion-token", _tenantId))
            .ReturnsAsync(new PasskeyAssertionResult(subjectId, username, username));

    private Task<ActionResult<PasskeyLoginCompleteResponse>> LoginCompleteAsync() =>
        _controller.LoginComplete(new PasskeyLoginCompleteRequest
        {
            AssertionResponseJson = "{}",
            ChallengeToken = "assertion-token",
        });

    [Fact]
    public async Task LoginComplete_WhenTheSubjectHasAnAuthenticator_WithholdsTheSession()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        _totpService.Setup(s => s.GetCredentialCountAsync(subjectId)).ReturnsAsync(1);
        _totpService.Setup(s => s.CreateStepUpTokenAsync(subjectId)).ReturnsAsync("step-up-token");

        var result = await LoginCompleteAsync();

        var response = Assert.IsType<PasskeyLoginCompleteResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        response.TotpRequired.Should().BeTrue();
        response.StepUpToken.Should().NotBeNullOrEmpty();
        response.AccessToken.Should().BeEmpty();
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the second factor is still outstanding, so the passkey alone must not grant access");
    }

    [Fact]
    public async Task LoginComplete_WhenTheSubjectHasNoAuthenticator_IssuesTheSession()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        _totpService.Setup(s => s.GetCredentialCountAsync(subjectId)).ReturnsAsync(0);
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900));

        var result = await LoginCompleteAsync();

        var response = Assert.IsType<PasskeyLoginCompleteResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        response.TotpRequired.Should().BeFalse();
        response.AccessToken.Should().Be("access");
        _sessionService.Verify(
            s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _totpService.Verify(s => s.CreateStepUpTokenAsync(It.IsAny<Guid>()), Times.Never);
    }

    #endregion

    [Fact]
    public async Task RecoveryVerify_EmptyFields_ReturnsBadRequest()
    {
        var request = new RecoveryVerifyRequest { Username = "", Code = "" };

        var result = await _controller.RecoveryVerify(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task RecoveryVerify_UnknownUser_ReturnsBadRequest()
    {
        var request = new RecoveryVerifyRequest { Username = "nonexistent", Code = "123456" };

        var result = await _controller.RecoveryVerify(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    #region Auth Status Endpoints

    [Fact]
    public async Task GetAuthStatus_NoCredentials_ReturnsSetupRequired()
    {
        // Arrange — tenant with no credentials (setup required)
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test",
        });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetAuthStatus();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuthStatusResponse>(okResult.Value);
        response.SetupRequired.Should().BeTrue();
        response.RecoveryMode.Should().BeFalse();
    }

    #endregion
}
