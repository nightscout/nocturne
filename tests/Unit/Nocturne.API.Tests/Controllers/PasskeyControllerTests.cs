using Nocturne.API.Tests.Services.Identity;
using Microsoft.Extensions.Caching.Memory;
using Nocturne.API.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

using Nocturne.Infrastructure.Data.Repositories;

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

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<IPasskeyService> _passkeyService;
    private readonly Mock<ITotpService> _totpService;
    private readonly Mock<ITrustedDeviceService> _trustedDevices = new();
    private readonly Mock<IRecoveryCodeService> _recoveryCodeService;
    private readonly LoginCodeService _loginCodes;
    private readonly RecoverySession _recoverySession;
    private readonly Mock<ISessionService> _sessionService;
    private readonly Mock<ISubjectService> _subjectService;
    private readonly Mock<ITenantAccessor> _tenantAccessor;
    private readonly Mock<ITenantService> _tenantService;
    private readonly Mock<IInAppNotificationService> _notifications = new();
    private readonly Mock<IAuthAuditService> _requestAudit = new();
    private readonly Mock<IAuthAuditService> _auditService = new();
    private readonly Mock<IPasskeyCredentialRepository> _passkeyCredentials = new();
    private readonly Mock<ILogger<PasskeyController>> _logger = new();
    private readonly PasskeyController _controller;

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    public PasskeyControllerTests()
    {
        _db = TestDbContextFactory.CreateSqlite();

        _dbContext = _db.CreateContext();

        _passkeyService = new Mock<IPasskeyService>();
        _totpService = new Mock<ITotpService>();
        // An unanswered mock reads as "a code is required"; these members have no authenticator.
        _totpService
            .Setup(s => s.GetSecondFactorRequirementAsync(It.IsAny<Guid>(), It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.NotEnrolled);
        _recoveryCodeService = new Mock<IRecoveryCodeService>();
        _sessionService = new Mock<ISessionService>();
        _subjectService = new Mock<ISubjectService>();
        _tenantAccessor = MockTenantAccessor.Create(_tenantId);

        var oidcOptions = Options.Create(FixtureCookieOptions());

        var jwtService = new Mock<IJwtService>();
        jwtService.Setup(j => j.GenerateRefreshToken()).Returns(() => Guid.NewGuid().ToString("N"));
        _loginCodes = new LoginCodeService(jwtService.Object, new Mock<IAuthAuditService>().Object, TimeProvider.System);
        // Login codes are tenant-scoped, so the session's contexts are on the request's tenant, as
        // the request pipeline hands them out.
        _recoverySession = new RecoverySession(
            new SharedSqliteFactory(_db.Options).AsNocturneFactory(_tenantId), _loginCodes,
            new TenantMemberService(
                new TenantMembershipRepository(new SharedSqliteFactory(_db.Options).AsNocturneFactory(_tenantId), AccountLinkRoutes.Off), TimeProvider.System),
            oidcOptions, NullLogger<RecoverySession>.Instance);

        _tenantService = new Mock<ITenantService>();

        _controller = new PasskeyController(
            _passkeyService.Object,
            new SignInCompletion(
                _totpService.Object,
                new TenantMemberService(new TenantMembershipRepository((new SharedSqliteFactory(_db.Options)).AsNocturneFactory(), AccountLinkRoutes.Off), TimeProvider.System),
                _sessionService.Object,
                _subjectService.Object,
                _auditService.Object),
            _trustedDevices.Object,
            _recoveryCodeService.Object,
            _recoverySession,
            _subjectService.Object,
            _auditService.Object,
            _tenantAccessor.Object,
            _tenantService.Object,
            // The real service, not a mock: the enrolment probe's cross-tenant reach is the
            // property under test, and a mock would assert the mock.
            new TenantMemberService(new TenantMembershipRepository((new SharedSqliteFactory(_db.Options)).AsNocturneFactory(), AccountLinkRoutes.Off), TimeProvider.System),
            new SubjectRepository((new SharedSqliteFactory(_db.Options)).AsNocturneFactory(), AccountLinkRoutes.Off),
            new TenantRepository((new SharedSqliteFactory(_db.Options)).AsNocturneFactory()),
            new TenantMembershipRepository((new SharedSqliteFactory(_db.Options)).AsNocturneFactory(), AccountLinkRoutes.Off),
            (new SharedSqliteFactory(_db.Options)).AsNocturneFactory(),
            oidcOptions,
            _logger.Object,
            TimeProvider.System);

        // Set up HttpContext with response cookies
        var httpContext = new DefaultHttpContext
        {
            RequestServices = TestRequestServices.Build(clamped: false, s => s.AddActiveSubjects()),
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
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

    /// <summary>
    /// Puts an authenticated subject on the request, as the auth middleware would, signed in with
    /// <paramref name="sessionId"/>, a live session unless named otherwise.
    /// </summary>
    private void Authenticate(Guid? subjectId = null, string sessionId = LiveSession.Id) =>
        _controller.ControllerContext.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = subjectId ?? _subjectId,
            TenantId = _tenantId,
            SessionId = sessionId,
        };

    private const string EndedSession = "ended-session";

    /// <summary>Mints a code of <paramref name="purpose"/> for the subject on this tenant.</summary>
    private async Task<string> IssueCodeAsync(
        Guid subjectId, LoginCodePurpose purpose, string? email = null, Guid? tenantId = null)
    {
        await using var db = _db.CreateContext(tenantId ?? _tenantId);
        return await _loginCodes.IssueAnyAsync(db, subjectId, purpose, email ?? LoginCodeIssuing.Address);
    }

    /// <summary>Starts a recovery session for the subject and presents its cookie, as a spent recovery code would.</summary>
    private async Task<string> GiveRecoverySessionAsync(Guid subjectId)
    {
        var code = await IssueCodeAsync(subjectId, LoginCodePurpose.RecoverySession);
        PresentRecoveryCookie(code);
        return code;
    }

    private void PresentRecoveryCookie(string code) =>
        _controller.ControllerContext.HttpContext.Request.Headers.Cookie = $"{RecoverySession.CookieName}={code}";

    private async Task<bool> IsLiveAsync(string code, LoginCodePurpose purpose)
    {
        await using var db = _db.CreateContext(_tenantId);
        return await _loginCodes.FindLiveAsync(db, code, [purpose]) is not null;
    }

    private void StubRegistration(Guid subjectId, Guid? credentialId = null) =>
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.OwnAccount, It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ReturnsAsync(new PasskeyCredentialResult(credentialId ?? Guid.CreateVersion7(), subjectId));

    private Task<ActionResult<PasskeyRegisterCompleteResponse>> CompleteRegistrationAsync() =>
        _controller.RegisterComplete(new PasskeyRegisterCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "token",
        });

    private void StubRegistrationOptions(Guid subjectId, string username) =>
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(subjectId, username, PasskeyRegistrationFlow.OwnAccount))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

    #region A factor is enrolled only from a live session

    [Fact]
    public async Task RegisterOptions_FromAnEndedSessionWithNoRecoverySession_IsAnsweredAsAnAnonymousCallerAndMintsNoChallenge()
    {
        var anonymous = await _controller.RegisterOptions(new PasskeyRegisterOptionsRequest { Username = "testuser" });
        Authenticate(sessionId: EndedSession);
        StubRegistrationOptions(_subjectId, "testuser");

        var result = await _controller.RegisterOptions(new PasskeyRegisterOptionsRequest { Username = "testuser" });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.Should().BeEquivalentTo(anonymous.Result.Should().BeOfType<ObjectResult>().Subject);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterComplete_FromAnEndedSessionWithNoRecoverySession_IsAnsweredAsAnAnonymousCallerAndStoresNothing()
    {
        var anonymous = await CompleteRegistrationAsync();
        Authenticate(sessionId: EndedSession);
        StubRegistration(_subjectId);

        var result = await CompleteRegistrationAsync();

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.Should().BeEquivalentTo(anonymous.Result.Should().BeOfType<ObjectResult>().Subject);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterOptions_FromAnEndedSessionWithARecoverySession_MintsTheChallengeForTheRecoverySubject()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        var session = await GiveRecoverySessionAsync(subjectId);
        Authenticate(subjectId, EndedSession);
        StubRegistrationOptions(subjectId, "owner");

        var result = await _controller.RegisterOptions(new PasskeyRegisterOptionsRequest { Username = "owner" });

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<PasskeyOptionsResponse>()
            .Which.ChallengeToken.Should().Be("token-data");
        (await IsLiveAsync(session, LoginCodePurpose.RecoverySession)).Should().BeTrue();
    }

    [Fact]
    public async Task RegisterComplete_FromAnEndedSessionWithARecoverySession_EnrolsUnderTheRecoverySessionAndSpendsIt()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        var session = await GiveRecoverySessionAsync(subjectId);
        Authenticate(subjectId, EndedSession);
        var credentialId = Guid.CreateVersion7();
        StubRegistration(subjectId, credentialId);

        var result = await CompleteRegistrationAsync();

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<PasskeyRegisterCompleteResponse>()
            .Which.CredentialId.Should().Be(credentialId);
        (await IsLiveAsync(session, LoginCodePurpose.RecoverySession)).Should().BeFalse();
        _controller.ControllerContext.HttpContext.Response.Headers.SetCookie
            .Single(header => header!.StartsWith($"{RecoverySession.CookieName}=", StringComparison.Ordinal))
            .Should().Contain("expires=Thu, 01 Jan 1970");
    }

    [Fact]
    public async Task RegisterComplete_FromAnEndedSessionOfAnotherSubject_EnrolsOnlyTheRecoverySessionsSubject()
    {
        var recovering = await SeedMemberAsync("owner", withPasskey: true);
        var session = await GiveRecoverySessionAsync(recovering);
        Authenticate(_subjectId, EndedSession);
        StubRegistration(recovering);

        var result = await CompleteRegistrationAsync();

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<PasskeyRegisterCompleteResponse>()
            .Which.SubjectId.Should().Be(recovering);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), _subjectId, It.IsAny<string?>()),
            Times.Never);
        (await IsLiveAsync(session, LoginCodePurpose.RecoverySession)).Should().BeFalse();
    }

    [Fact]
    public async Task RegenerateRecoveryCodes_FromAnEndedSession_IsChallengedAndMakesNoCodes()
    {
        Authenticate(sessionId: EndedSession);

        var result = await _controller.RegenerateRecoveryCodes();

        result.Result.Should().BeOfType<ChallengeResult>();
        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RegenerateRecoveryCodes_FromALiveSession_ReturnsTheNewCodes()
    {
        Authenticate();
        _recoveryCodeService.Setup(s => s.GenerateCodesAsync(_subjectId)).ReturnsAsync(["one", "two"]);

        var result = await _controller.RegenerateRecoveryCodes();

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<RecoveryRegenerateResponse>()
            .Which.Codes.Should().Equal("one", "two");
    }

    [Fact]
    public async Task A_recovery_session_enrols_without_the_refusal_reading_any_signed_in_state()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        await GiveRecoverySessionAsync(subjectId);
        StubRegistrationOptions(subjectId, "owner");
        StubRegistration(subjectId);
        _controller.HttpContext.RequestServices = new ServiceCollection().BuildServiceProvider();

        var options = await _controller.RegisterOptions(new PasskeyRegisterOptionsRequest { Username = "owner" });
        var completed = await CompleteRegistrationAsync();

        options.Result.Should().BeOfType<OkObjectResult>();
        completed.Result.Should().BeOfType<OkObjectResult>();
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.OwnAccount, It.IsAny<Guid>(), subjectId, It.IsAny<string?>()),
            Times.Once);
    }

    #endregion

    #region Passkey enrolment is bound to the caller

    [Fact]
    public async Task RegisterOptions_WhenAnonymous_ReturnsUnauthorizedWithoutMintingAChallenge()
    {
        var result = await _controller.RegisterOptions(new PasskeyRegisterOptionsRequest { Username = "testuser" });

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(401);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>()),
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Never,
            "an anonymous caller must not be able to store a credential");
    }

    [Fact]
    public async Task RegisterOptions_UsesTheAuthenticatedSubject()
    {
        Authenticate();
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(_subjectId, "testuser", PasskeyRegistrationFlow.OwnAccount))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

        var result = await _controller.RegisterOptions(new PasskeyRegisterOptionsRequest { Username = "testuser" });

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PasskeyOptionsResponse>(okResult.Value);
        response.Options.Should().Contain("challenge");
        response.ChallengeToken.Should().Be("token-data");
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(_subjectId, "testuser", PasskeyRegistrationFlow.OwnAccount),
            Times.Once);
    }

    [Fact]
    public async Task RegisterComplete_BindsTheChallengeToTheAuthenticatedSubject()
    {
        var victimSubjectId = Guid.CreateVersion7();
        Authenticate();
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.OwnAccount, It.IsAny<Guid>(), _subjectId, It.IsAny<string?>()))
            .ReturnsAsync(new PasskeyCredentialResult(Guid.CreateVersion7(), _subjectId));

        var result = await _controller.RegisterComplete(new PasskeyRegisterCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "token-for-victim",
        });

        Assert.IsType<OkObjectResult>(result.Result);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), _subjectId, It.IsAny<string?>()),
            Times.Once,
            "the enrolling subject is the session's, so a challenge minted for another subject is refused");
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), victimSubjectId, It.IsAny<string?>()),
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
            s => s.GenerateRegistrationOptionsAsync(callerId, "victim", PasskeyRegistrationFlow.OwnAccount), Times.Once);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(victimId, It.IsAny<string>(), PasskeyRegistrationFlow.OwnAccount), Times.Never);
    }

    [Fact]
    public async Task RegisterOptions_WithARecoverySession_BindsToTheCookieSubject()
    {
        // Re-registering after spending a recovery code: the account still holds its old
        // credential, so only the recovery session makes this allowed.
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        await GiveRecoverySessionAsync(subjectId);
        StubRegistrationOptions(subjectId, "owner");

        var result = await _controller.RegisterOptions(
            new PasskeyRegisterOptionsRequest { Username = "owner" });

        Assert.IsType<OkObjectResult>(result.Result);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(subjectId, "owner", PasskeyRegistrationFlow.OwnAccount), Times.Once);
    }

    [Fact]
    public async Task RegisterOptions_UnderARecoverySession_DoesNotSpendIt()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        var session = await GiveRecoverySessionAsync(subjectId);
        StubRegistrationOptions(subjectId, "owner");

        await _controller.RegisterOptions(new PasskeyRegisterOptionsRequest { Username = "owner" });
        await _controller.RegisterOptions(new PasskeyRegisterOptionsRequest { Username = "owner" });

        (await IsLiveAsync(session, LoginCodePurpose.RecoverySession)).Should().BeTrue();
    }

    public static TheoryData<LoginCodePurpose> OtherPurposes =>
        [LoginCodePurpose.Login, LoginCodePurpose.Recovery, LoginCodePurpose.OperatorRecovery,
            LoginCodePurpose.EmailRecovery, LoginCodePurpose.EmailConfirm];

    [Theory]
    [MemberData(nameof(OtherPurposes))]
    public async Task RegisterOptions_WithAnotherPurposesCodeAsTheRecoveryCookie_IsRefused(LoginCodePurpose purpose)
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        var email = LoginCodeService.EmailPurposes.Contains(purpose) ? "a@example.test" : null;
        PresentRecoveryCookie(await IssueCodeAsync(subjectId, purpose, email));

        var result = await _controller.RegisterOptions(
            new PasskeyRegisterOptionsRequest { Username = "owner" });

        Assert.Equal(401, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>()),
            Times.Never);
    }

    [Theory]
    [MemberData(nameof(OtherPurposes))]
    public async Task RegisterComplete_WithAnotherPurposesCodeAsTheRecoveryCookie_StoresNothing(LoginCodePurpose purpose)
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        var email = LoginCodeService.EmailPurposes.Contains(purpose) ? "a@example.test" : null;
        var code = await IssueCodeAsync(subjectId, purpose, email);
        PresentRecoveryCookie(code);
        StubRegistration(subjectId);

        var result = await CompleteRegistrationAsync();

        Assert.Equal(401, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Never);
        (await IsLiveAsync(code, purpose)).Should().BeTrue("a cookie is never where any other code is spent");
    }

    [Fact]
    public async Task RegisterComplete_WithARecoverySession_BindsToTheCookieSubject()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        await GiveRecoverySessionAsync(subjectId);
        StubRegistration(subjectId);

        var result = await CompleteRegistrationAsync();

        Assert.IsType<OkObjectResult>(result.Result);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), subjectId, It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task RegisterComplete_WithARecoverySession_EndsItAndNamesThePasskeyItAdded()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        var session = await GiveRecoverySessionAsync(subjectId);
        var credentialId = Guid.CreateVersion7();
        StubRegistration(subjectId, credentialId);

        var result = await CompleteRegistrationAsync();

        var response = (PasskeyRegisterCompleteResponse)Assert.IsType<OkObjectResult>(result.Result).Value!;
        response.CredentialId.Should().Be(credentialId);
        (await IsLiveAsync(session, LoginCodePurpose.RecoverySession)).Should().BeFalse();
        var setCookie = _controller.ControllerContext.HttpContext.Response.Headers.SetCookie
            .Single(header => header!.StartsWith($"{RecoverySession.CookieName}=", StringComparison.Ordinal));
        setCookie.Should().Contain("expires=Thu, 01 Jan 1970");
    }

    [Fact]
    public async Task RegisterComplete_WithARecoverySession_IssuesNoSessionAndRecordsNoSignIn()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        await GiveRecoverySessionAsync(subjectId);
        StubRegistration(subjectId);

        await CompleteRegistrationAsync();

        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>()), Times.Never);
        _controller.ControllerContext.HttpContext.Response.Headers.SetCookie
            .Should().NotContain(h => h!.StartsWith(".Nocturne.AccessToken=", StringComparison.Ordinal)
                || h.StartsWith(".Nocturne.RefreshToken=", StringComparison.Ordinal));
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.Login, It.IsAny<Guid?>(), true,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task ASessionWhoseMemberWasDeactivated_CannotEnrolAPasskey()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        var session = await GiveRecoverySessionAsync(subjectId);
        StubRegistrationOptions(subjectId, "owner");
        StubRegistration(subjectId);
        (await _dbContext.Subjects.SingleAsync(s => s.Id == subjectId)).IsActive = false;
        await _dbContext.SaveChangesAsync();

        var options = await _controller.RegisterOptions(new PasskeyRegisterOptionsRequest { Username = "owner" });
        PresentRecoveryCookie(session);
        var complete = await CompleteRegistrationAsync();

        Assert.Equal(401, Assert.IsType<ObjectResult>(options.Result).StatusCode);
        Assert.Equal(401, Assert.IsType<ObjectResult>(complete.Result).StatusCode);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterComplete_ASecondEnrolmentOnTheSameSession_IsRefusedEvenWithTheCookieReplayed()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        var session = await GiveRecoverySessionAsync(subjectId);
        StubRegistration(subjectId);
        await CompleteRegistrationAsync();

        PresentRecoveryCookie(session);
        var second = await CompleteRegistrationAsync();

        Assert.Equal(401, Assert.IsType<ObjectResult>(second.Result).StatusCode);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), subjectId, It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task RegisterComplete_WhenTheAttestationIsRefused_LeavesTheSessionUsable()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        var session = await GiveRecoverySessionAsync(subjectId);
        _passkeyService
            .SetupSequence(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.OwnAccount, It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ThrowsAsync(new PasskeyRegistrationRefusedException("bad attestation"))
            .ReturnsAsync(new PasskeyCredentialResult(Guid.CreateVersion7(), subjectId));

        var refused = await CompleteRegistrationAsync();
        (await IsLiveAsync(session, LoginCodePurpose.RecoverySession)).Should().BeTrue();
        var retried = await CompleteRegistrationAsync();

        Assert.Equal(400, Assert.IsType<ObjectResult>(refused.Result).StatusCode);
        Assert.IsType<OkObjectResult>(retried.Result);
    }

    [Fact]
    public async Task RegisterComplete_WhenSignedIn_KeepsItsResponseAndSpendsNoSessionCode()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        Authenticate(subjectId);
        var credentialId = Guid.CreateVersion7();
        StubRegistration(subjectId, credentialId);

        var result = await CompleteRegistrationAsync();

        var response = (PasskeyRegisterCompleteResponse)Assert.IsType<OkObjectResult>(result.Result).Value!;
        response.CredentialId.Should().Be(credentialId);
        response.SubjectId.Should().Be(subjectId);
    }

    [Theory]
    [MemberData(nameof(RefusedRegistrations))]
    public async Task RegisterComplete_WhenSignedInAndTheRegistrationIsRefused_AnswersRegistrationFailed(Exception refusal)
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        Authenticate(subjectId);
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.OwnAccount, It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ThrowsAsync(refusal);

        var result = await CompleteRegistrationAsync();

        var problem = Assert.IsType<ObjectResult>(result.Result);
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be("Passkey registration failed");
    }

    [Fact]
    public async Task RegisterComplete_WhenSignedInAndTheStoreTimesOut_IsAnErrorAndNotARefusal()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        Authenticate(subjectId);
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.OwnAccount, It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ThrowsAsync(new TimeoutException("the database did not answer"));

        var act = () => CompleteRegistrationAsync();

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task RegisterComplete_WhenARecoverySessionEnrolsAndTheStoreTimesOut_IsAnErrorAndLeavesTheSessionUsable()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        var session = await GiveRecoverySessionAsync(subjectId);
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.OwnAccount, It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ThrowsAsync(new TimeoutException("the database did not answer"));

        var act = () => CompleteRegistrationAsync();

        await act.Should().ThrowAsync<TimeoutException>();
        (await IsLiveAsync(session, LoginCodePurpose.RecoverySession)).Should().BeTrue();
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

    private Mock<ILoginCodeService> LoginCodesRedeeming(
        Guid? subjectId, LoginCodePurpose purpose = LoginCodePurpose.Recovery)
    {
        var loginCodes = new Mock<ILoginCodeService>();
        loginCodes
            .Setup(s => s.RedeemAsync(
                It.IsAny<NocturneDbContext>(), "issued-code",
                It.Is<IReadOnlyCollection<LoginCodePurpose>>(p => p.SequenceEqual(LoginCodeService.SessionStartingPurposes)),
                It.IsAny<Func<RedeemedLoginCode, CancellationToken, Task<bool>>?>(),
                It.IsAny<Func<RedeemedLoginCode, CancellationToken, Task>?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (
                NocturneDbContext _, string _, IReadOnlyCollection<LoginCodePurpose> _,
                Func<RedeemedLoginCode, CancellationToken, Task<bool>>? admits,
                Func<RedeemedLoginCode, CancellationToken, Task>? withSpend,
                string? _, string? _, CancellationToken ct) =>
            {
                if (subjectId is not { } id)
                    return null;
                var code = new RedeemedLoginCode(id, purpose);
                if (admits is not null && !await admits(code, ct))
                    return (RedeemedLoginCode?)null;
                if (withSpend is not null)
                    await withSpend(code, ct);
                return code;
            });
        return loginCodes;
    }

    private MemberRecoveryEligibility Eligibility() =>
        new(new TenantMemberService(new TenantMembershipRepository(new SharedSqliteFactory(_db.Options).AsNocturneFactory(), AccountLinkRoutes.Off), TimeProvider.System), Delivery(configured: false));

    private Task<ActionResult<RecoveryModeRedeemResponse>> RedeemAsync(
        Mock<ILoginCodeService> loginCodes, string code = "issued-code") =>
        _controller.RecoveryModeRedeem(new RecoveryModeRedeemRequest { Code = code }, loginCodes.Object, Eligibility());

    private string? RecoveryCookieSet() =>
        _controller.ControllerContext.HttpContext.Response.Headers.SetCookie
            .SingleOrDefault(h => h!.StartsWith($"{RecoverySession.CookieName}=", StringComparison.Ordinal));

    /// <summary>The session code the response's recovery cookie carries.</summary>
    private string SessionCodeSet() =>
        RecoveryCookieSet()!.Split(';')[0][(RecoverySession.CookieName.Length + 1)..];

    [Fact]
    public async Task RecoveryModeRedeem_WithAnIssuedCode_StartsARecoverySessionForItsSubject()
    {
        await SeedMemberAsync("rhys", withPasskey: true);
        var orphanId = await SeedMemberAsync("orphan");

        var result = await RedeemAsync(LoginCodesRedeeming(orphanId));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        ((RecoveryModeRedeemResponse)ok.Value!).Username.Should().Be("orphan");
        _recoveryCodeService.Verify(r => r.InvalidateAllAsync(orphanId), Times.Once,
            "recovery codes the member may have lost stop working once someone else restores access");
        RecoveryCookieSet().Should().Contain("samesite=lax").And.Contain("httponly")
            .And.NotContain("domain=", "the session is spent on the host it was started from");
        await using var db = _db.CreateContext(_tenantId);
        (await _loginCodes.FindLiveAsync(db, SessionCodeSet(), [LoginCodePurpose.RecoverySession]))!
            .SubjectId.Should().Be(orphanId);
    }

    [Fact]
    public async Task RecoveryModeRedeem_WithACodeThatDoesNotRedeem_IsRefusedWithoutASession()
    {
        await SeedMemberAsync("orphan");

        var result = await RedeemAsync(LoginCodesRedeeming(null));

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        RecoveryCookieSet().Should().BeNull();
        _recoveryCodeService.Verify(r => r.InvalidateAllAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RecoveryModeRedeem_ForAMemberWhoHasSinceEnrolledAPasskey_IsRefused()
    {
        await SeedMemberAsync("rhys", withPasskey: true);
        var memberId = await SeedMemberAsync("orphan");
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(), SubjectId = memberId, CredentialId = [7, 7], PublicKey = [1],
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await RedeemAsync(LoginCodesRedeeming(memberId));

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        RecoveryCookieSet().Should().BeNull();
    }

    [Fact]
    public async Task RecoveryModeRedeem_AnAdministratorCode_ForAMemberWhoHasBecomeOperatorOnly_IsRefused()
    {
        await SeedMemberAsync("rhys", withPasskey: true);
        var orphanId = await SeedMemberAsync("orphan");
        await SeedMembershipAsync(orphanId, Guid.CreateVersion7());

        var result = await RedeemAsync(LoginCodesRedeeming(orphanId, LoginCodePurpose.Recovery));

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        RecoveryCookieSet().Should().BeNull();
    }

    [Fact]
    public async Task RecoveryModeRedeem_AnOperatorCode_RecoversAnOperatorOnlyMember()
    {
        await SeedMemberAsync("rhys", withPasskey: true);
        var orphanId = await SeedMemberAsync("orphan");
        await SeedMembershipAsync(orphanId, Guid.CreateVersion7());

        var result = await RedeemAsync(LoginCodesRedeeming(orphanId, LoginCodePurpose.OperatorRecovery));

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task RecoveryModeRedeem_AnOperatorCode_RecoversAMemberWhoStillHasAPasskey()
    {
        var memberId = await SeedMemberAsync("rhys", withPasskey: true);

        var result = await RedeemAsync(LoginCodesRedeeming(memberId, LoginCodePurpose.OperatorRecovery));

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task RecoveryModeRedeem_AnAdministratorCode_ForAMemberWhoStillHasAPasskey_IsRefused()
    {
        var memberId = await SeedMemberAsync("rhys", withPasskey: true);

        var result = await RedeemAsync(LoginCodesRedeeming(memberId, LoginCodePurpose.Recovery));

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        RecoveryCookieSet().Should().BeNull();
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("system")]
    [InlineData("demo")]
    [InlineData("left")]
    public async Task RecoveryModeRedeem_AnOperatorCode_ForAMemberTheOperatorMayNotRecover_IsRefused(string reason)
    {
        var memberId = await SeedMemberAsync("rhys", withPasskey: true);
        var subject = await _dbContext.Subjects.SingleAsync(s => s.Id == memberId);
        switch (reason)
        {
            case "inactive": subject.IsActive = false; break;
            case "system": subject.IsSystemSubject = true; break;
            case "demo": subject.IsDemoSubject = true; break;
            default: _dbContext.TenantMembers.RemoveRange(_dbContext.TenantMembers.Where(m => m.SubjectId == memberId)); break;
        }
        await _dbContext.SaveChangesAsync();

        var result = await RedeemAsync(LoginCodesRedeeming(memberId, LoginCodePurpose.OperatorRecovery));

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        RecoveryCookieSet().Should().BeNull();
    }

    [Fact]
    public async Task RegisterComplete_VoidsTheSubjectsOutstandingRecoveryCodes()
    {
        var subjectId = await SeedMemberAsync("orphan");
        await using (var tenantDb = _db.CreateContext(_tenantId))
        {
            tenantDb.LoginCodes.Add(new LoginCodeEntity
            {
                Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = subjectId,
                CodeHash = "outstanding", Purpose = LoginCodePurpose.Recovery,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            });
            tenantDb.LoginCodes.Add(new LoginCodeEntity
            {
                Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = subjectId,
                CodeHash = "emailed", Purpose = LoginCodePurpose.EmailRecovery, Email = "a@example.test",
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            });
            await tenantDb.SaveChangesAsync();
        }
        await GiveRecoverySessionAsync(subjectId);
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.OwnAccount, It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ReturnsAsync(new PasskeyCredentialResult(Guid.CreateVersion7(), subjectId));

        // Login codes are tenant-scoped, so this needs contexts on the request's tenant, as the
        // request pipeline hands out.
        var tenantContexts = new SharedSqliteFactory(_db.Options).AsNocturneFactory(_tenantId);
        var controller = new PasskeyController(
            _passkeyService.Object,
            new SignInCompletion(_totpService.Object, new TenantMemberService(new TenantMembershipRepository(tenantContexts, AccountLinkRoutes.Off), TimeProvider.System),
                _sessionService.Object, _subjectService.Object, new Mock<IAuthAuditService>().Object),
            _trustedDevices.Object, _recoveryCodeService.Object, _recoverySession,
            _subjectService.Object, new Mock<IAuthAuditService>().Object,
            _tenantAccessor.Object, _tenantService.Object, new TenantMemberService(new TenantMembershipRepository(tenantContexts, AccountLinkRoutes.Off), TimeProvider.System),
            new SubjectRepository(tenantContexts, AccountLinkRoutes.Off), new TenantRepository(tenantContexts), new TenantMembershipRepository(tenantContexts, AccountLinkRoutes.Off),
            tenantContexts, Options.Create(new OidcOptions()), new Mock<ILogger<PasskeyController>>().Object, TimeProvider.System)
        {
            ControllerContext = _controller.ControllerContext,
        };

        var result = await controller.RegisterComplete(new PasskeyRegisterCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "token",
        });

        result.Result.Should().BeOfType<OkObjectResult>();

        await using var check = _db.CreateContext(_tenantId);
        (await check.LoginCodes.AsNoTracking().ToListAsync())
            .Should().OnlyContain(c => c.ConsumedAt != null, "the session and every outstanding recovery code are spent");
    }

    [Fact]
    public async Task RegisterComplete_StillSucceedsWhenVoidingRecoveryCodesFails()
    {
        var subjectId = await SeedMemberAsync("orphan");
        Authenticate(subjectId);
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.OwnAccount, It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ReturnsAsync(new PasskeyCredentialResult(Guid.CreateVersion7(), subjectId));
        var failingContexts = new Mock<INocturneDbContextFactory>();
        failingContexts
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var controller = new PasskeyController(
            _passkeyService.Object,
            new SignInCompletion(_totpService.Object, new TenantMemberService(new TenantMembershipRepository(failingContexts.Object, AccountLinkRoutes.Off), TimeProvider.System),
                _sessionService.Object, _subjectService.Object, new Mock<IAuthAuditService>().Object),
            _trustedDevices.Object, _recoveryCodeService.Object,
            new RecoverySession(failingContexts.Object, _loginCodes, Mock.Of<ITenantMemberService>(), Options.Create(new OidcOptions()), NullLogger<RecoverySession>.Instance),
            _subjectService.Object, new Mock<IAuthAuditService>().Object,
            _tenantAccessor.Object, _tenantService.Object, new TenantMemberService(new TenantMembershipRepository(failingContexts.Object, AccountLinkRoutes.Off), TimeProvider.System),
            new SubjectRepository(failingContexts.Object, AccountLinkRoutes.Off), new TenantRepository(failingContexts.Object), new TenantMembershipRepository(failingContexts.Object, AccountLinkRoutes.Off),
            failingContexts.Object, Options.Create(new OidcOptions()), new Mock<ILogger<PasskeyController>>().Object, TimeProvider.System)
        {
            ControllerContext = _controller.ControllerContext,
        };

        var result = await controller.RegisterComplete(new PasskeyRegisterCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "token",
        });

        result.Result.Should().BeOfType<OkObjectResult>("the passkey was saved");
    }

    private async Task SeedMembershipAsync(Guid subjectId, Guid tenantId)
    {
        await EnsureTenantAsync(tenantId);
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = subjectId,
        });
        await _dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task RecoveryModeRedeem_WithoutACode_IsRefusedBeforeAnyRedemption()
    {
        var loginCodes = LoginCodesRedeeming(Guid.CreateVersion7());

        var result = await RedeemAsync(loginCodes, " ");

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        loginCodes.VerifyNoOtherCalls();
        RecoveryCookieSet().Should().BeNull();
    }

    [Fact]
    public void The_only_anonymous_recovery_mode_action_spends_an_issued_code()
    {
        // A username is public, so naming one must never decide whose account an anonymous caller
        // enrols a passkey onto.
        var anonymousEnrolments = typeof(PasskeyController).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).Length > 0)
            .Where(m => m.GetCustomAttributes(typeof(HttpPostAttribute), true)
                .Cast<HttpPostAttribute>()
                .Any(a => a.Template!.StartsWith("recovery-mode", StringComparison.Ordinal)))
            .ToList();

        anonymousEnrolments.Should().ContainSingle()
            .Which.Name.Should().Be(nameof(PasskeyController.RecoveryModeRedeem));
    }

    #endregion

    #region Anonymous enrolment binds to a server-resolved subject

    [Fact]
    public async Task InviteComplete_WithAChallengeForAnotherSubject_IsRefused()
    {
        var victimId = await SeedMemberAsync("rhys", withPasskey: true);
        await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(victimId, PasskeyRegistrationFlow.MemberInvite);

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "invitee",
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            inviteService.Object);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(400);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), victimId, It.IsAny<string?>()),
            Times.Never,
            "a subject that can sign in is not an enrolling subject");
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task InviteComplete_WithAChallengeForTheResolvedInvitee_Succeeds()
    {
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(inviteeId, PasskeyRegistrationFlow.MemberInvite);
        var steps = new List<string>();
        _recoveryCodeService.Setup(s => s.GenerateCodesAsync(inviteeId))
            .Callback(() => steps.Add("recovery-codes"))
            .ReturnsAsync(["code-1"]);
        _sessionService
            .Setup(s => s.IssueSessionAsync(inviteeId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => steps.Add("session"))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, inviteeId));

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "invitee",
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            inviteService.Object);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PasskeyRegistrationResponse>(ok.Value);
        response.Success.Should().BeTrue();
        response.RecoveryCodes.Should().Equal("code-1");
        steps.Should().Equal("session", "recovery-codes");
        inviteService.Verify(s => s.AcceptInviteAsync("invite-token", inviteeId, _tenantId), Times.Once);
        _subjectService.Verify(s => s.UpdateLastLoginAsync(inviteeId), Times.Once);
    }

    [Fact]
    public async Task InviteComplete_WhenTheInviteIsRefused_RemovesThePasskeyAndIssuesNoSession()
    {
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        inviteService.Setup(s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(new AcceptMemberInviteResult(false, "invite_exhausted", "This invite has no uses left."));
        var credentialId = StubRegistrationChallengeMintedFor(inviteeId, PasskeyRegistrationFlow.MemberInvite);

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "invitee",
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            inviteService.Object);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _passkeyService.Verify(
            s => s.RemoveCredentialAsync(credentialId, inviteeId, _tenantId), Times.Once,
            "a refused invite joins no one, so the passkey must not outlive it");
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InviteComplete_WhenTheSignInTimeCannotBeRecorded_LeavesAnUndeliveredSessionAndSetsNoCookie()
    {
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(inviteeId, PasskeyRegistrationFlow.MemberInvite);
        _subjectService.Setup(s => s.UpdateLastLoginAsync(inviteeId))
            .ThrowsAsync(new DbUpdateException("the update failed"));

        var complete = () => _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "invitee",
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            inviteService.Object);

        await complete.Should().ThrowAsync<DbUpdateException>();
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _controller.HttpContext.Response.Headers.SetCookie.Should().BeEmpty();
        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task TenantInviteComplete_WhenTheSignInTimeCannotBeRecorded_LeavesAnUndeliveredSessionAndSetsNoCookie()
    {
        var newOwnerId = await SeedEnrollingSubjectAsync("founder");
        StubRegistrationChallengeMintedFor(newOwnerId, PasskeyRegistrationFlow.CreationLink);
        var inviteId = Guid.CreateVersion7();
        _tenantService.Setup(s => s.ValidateSlugAsync("founder-site", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(true));
        var tenantInvites = new Mock<ITenantCreationInviteService>();
        tenantInvites.Setup(s => s.ClaimAsync("tenant-invite-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<Guid>.Ok(inviteId));
        tenantInvites
            .Setup(s => s.CreateTenantAsync(
                inviteId, "founder-site", "Founder", newOwnerId, "founder", It.IsAny<CancellationToken>()))
            .Returns(() => TenantCreatedForAsync(newOwnerId));
        _subjectService.Setup(s => s.UpdateLastLoginAsync(newOwnerId))
            .ThrowsAsync(new DbUpdateException("the update failed"));

        var complete = () => _controller.TenantInviteComplete(
            new TenantInviteCompleteRequest
            {
                Token = "tenant-invite-token",
                Slug = "founder-site",
                SiteName = "Founder",
                Username = "founder",
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            tenantInvites.Object);

        await complete.Should().ThrowAsync<DbUpdateException>();
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _controller.HttpContext.Response.Headers.SetCookie.Should().BeEmpty();
        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task AccessRequestComplete_WithAChallengeForAnotherSubject_IsRefused()
    {
        var victimId = await SeedMemberAsync("rhys", withPasskey: true);
        await AllowAccessRequestsAsync();
        await SeedPendingAccessRequestAsync("Sam Smith");
        StubRegistrationChallengeMintedFor(victimId, PasskeyRegistrationFlow.AccessRequest);

        var result = await _controller.AccessRequestComplete(
            new AccessRequestCompleteRequest
            {
                DisplayName = "Sam Smith",
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            FilingScopes(), _passkeyCredentials.Object);

        var objectResult = Assert.IsType<ObjectResult>(result);
        objectResult.StatusCode.Should().Be(400);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), victimId, It.IsAny<string?>()),
            Times.Never,
            "a subject that can sign in is not an enrolling subject");
    }

    [Fact]
    public async Task AccessRequestComplete_WithAChallengeForTheResolvedRequestor_Succeeds()
    {
        await AllowAccessRequestsAsync();
        var requestorId = await SeedPendingAccessRequestAsync("Sam Smith");
        StubRegistrationChallengeMintedFor(requestorId, PasskeyRegistrationFlow.AccessRequest);

        var result = await _controller.AccessRequestComplete(
            new AccessRequestCompleteRequest
            {
                DisplayName = "Sam Smith",
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            FilingScopes(), _passkeyCredentials.Object);

        Assert.IsType<OkResult>(result);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), requestorId, It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task AccessRequestComplete_TellsTheStandingOwnersOfTheFiledRequestOnly()
    {
        await AllowAccessRequestsAsync();
        var owner = await SeedOwnerAsync("owner");
        await SeedOwnerAsync("deactivated", isActive: false);
        await SeedOwnerAsync("public", isSystemSubject: true);
        var requestorId = await SeedPendingAccessRequestAsync("Sam Smith");
        StubRegistrationChallengeMintedFor(requestorId, PasskeyRegistrationFlow.AccessRequest);

        Assert.IsType<OkResult>(await CompleteAccessRequestAsync("Sam Smith"));

        var created = _notifications.Invocations
            .Where(i => i.Method.Name == nameof(IInAppNotificationService.CreateNotificationAsync))
            .ToList();
        created.Select(i => (string)i.Arguments[0]!).Should().Equal(owner.ToString());
        created.Select(i => (string)i.Arguments[1]!).Should().Equal("membership.requested");
    }

    [Fact]
    public async Task AccessRequestComplete_WhenTheRequestIsNotFiled_RemovesTheCredential()
    {
        await AllowAccessRequestsAsync();
        var requestorId = await SeedPendingAccessRequestAsync("Sam Smith");
        var credentialId = StubRegistrationChallengeMintedFor(requestorId, PasskeyRegistrationFlow.AccessRequest);
        var filer = new Mock<IMembershipRequestService>();
        filer.Setup(f => f.CreateRequestAsync(_tenantId, requestorId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateMembershipRequestResult(CreateMembershipRequestOutcome.Refused, "This profile is not accepting membership requests."));

        var result = await CompleteAccessRequestAsync("Sam Smith", filer.Object);

        Assert.IsType<ObjectResult>(result).StatusCode.Should().Be(400);
        _passkeyCredentials.Verify(p => p.RemoveAsync(credentialId, requestorId, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Theory]
    [MemberData(nameof(RefusedRegistrations))]
    public async Task AccessRequestComplete_WhenTheRegistrationIsRefused_AnswersRegistrationFailedAndFilesNoRequest(Exception refusal)
    {
        await AllowAccessRequestsAsync();
        var requestorId = await SeedPendingAccessRequestAsync("Sam Smith");
        StubRegistrationChallengeMintedFor(requestorId, PasskeyRegistrationFlow.AccessRequest);
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .ThrowsAsync(refusal);
        var filer = new Mock<IMembershipRequestService>();

        var result = await CompleteAccessRequestAsync("Sam Smith", filer.Object);

        var problem = Assert.IsType<ObjectResult>(result);
        problem.StatusCode.Should().Be(400);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        details.Detail.Should().Be("Passkey registration failed");
        details.Title.Should().Be("Bad Request");
        filer.VerifyNoOtherCalls();
        _passkeyCredentials.Verify(
            p => p.RemoveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task AccessRequestComplete_WhenFilingThrows_RemovesTheCredential()
    {
        await AllowAccessRequestsAsync();
        var requestorId = await SeedPendingAccessRequestAsync("Sam Smith");
        var credentialId = StubRegistrationChallengeMintedFor(requestorId, PasskeyRegistrationFlow.AccessRequest);
        var filer = new Mock<IMembershipRequestService>();
        filer.Setup(f => f.CreateRequestAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("the insert failed"));

        await FluentActions.Awaiting(() => CompleteAccessRequestAsync("Sam Smith", filer.Object))
            .Should().ThrowAsync<DbUpdateException>();

        _passkeyCredentials.Verify(p => p.RemoveAsync(credentialId, requestorId, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task AccessRequestComplete_WhenTellingTheOwnerFailsAfterTheRequestIsStored_KeepsTheCredentialAndApprovalAdmitsTheRequester()
    {
        await AllowAccessRequestsAsync();
        var owner = await SeedOwnerAsync("owner");
        var ownerRole = (await _dbContext.TenantMemberRoles.AsNoTracking().FirstAsync()).TenantRoleId;
        var requestorId = await SeedPendingAccessRequestAsync("Sam Smith");
        StubRegistrationChallengeMintedFor(requestorId, PasskeyRegistrationFlow.AccessRequest);
        _notifications
            .Setup(n => n.CreateNotificationAsync(
                owner.ToString(), "membership.requested", It.IsAny<string>(), It.IsAny<NotificationCategory?>(),
                It.IsAny<NotificationUrgency?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("the notification insert failed"));

        Assert.IsType<OkResult>(await CompleteAccessRequestAsync("Sam Smith"));

        _passkeyCredentials.Verify(
            p => p.RemoveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
        await ApproveTheOneRequestAndAssertTheRequesterIsAdmittedAsync(owner, ownerRole, requestorId);
    }

    [Fact]
    public async Task AccessRequestComplete_WhenTheVisitorDisconnectsAfterTheRequestIsStored_KeepsTheCredentialAndApprovalAdmitsTheRequester()
    {
        await AllowAccessRequestsAsync();
        var owner = await SeedOwnerAsync("owner");
        var ownerRole = (await _dbContext.TenantMemberRoles.AsNoTracking().FirstAsync()).TenantRoleId;
        var requestorId = await SeedPendingAccessRequestAsync("Sam Smith");
        StubRegistrationChallengeMintedFor(requestorId, PasskeyRegistrationFlow.AccessRequest);
        using var disconnect = new CancellationTokenSource();
        _controller.HttpContext.RequestAborted = disconnect.Token;
        _notifications
            .Setup(n => n.CreateNotificationAsync(
                owner.ToString(), "membership.requested", It.IsAny<string>(), It.IsAny<NotificationCategory?>(),
                It.IsAny<NotificationUrgency?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string _, NotificationCategory? _, NotificationUrgency? _, string? _,
                string? _, string? _, string? _, List<NotificationActionDto>? _, ResolutionConditions? _,
                Dictionary<string, object>? _, CancellationToken token) =>
            {
                disconnect.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new InAppNotificationDto());
            });

        Assert.IsType<OkResult>(await CompleteAccessRequestAsync("Sam Smith"));

        disconnect.IsCancellationRequested.Should().BeTrue();
        _passkeyCredentials.Verify(
            p => p.RemoveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
        await ApproveTheOneRequestAndAssertTheRequesterIsAdmittedAsync(owner, ownerRole, requestorId);
    }

    [Fact]
    public async Task AccessRequestComplete_WhenFilingThrowsAfterTheRequestIsStored_KeepsTheCredential()
    {
        await AllowAccessRequestsAsync();
        var requestorId = await SeedPendingAccessRequestAsync("Sam Smith");
        StubRegistrationChallengeMintedFor(requestorId, PasskeyRegistrationFlow.AccessRequest);
        var stored = MembershipRequests();
        var filer = new Mock<IMembershipRequestService>();
        filer.Setup(f => f.CreateRequestAsync(_tenantId, requestorId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid tenant, Guid subject, string? message, CancellationToken token) =>
            {
                await stored.CreateRequestAsync(tenant, subject, message, token);
                throw new DbUpdateException("the connection dropped after the commit");
            });
        filer.Setup(f => f.GetMyRequestAsync(_tenantId, requestorId, It.IsAny<CancellationToken>()))
            .Returns((Guid tenant, Guid subject, CancellationToken token) => stored.GetMyRequestAsync(tenant, subject, token));

        await FluentActions.Awaiting(() => CompleteAccessRequestAsync("Sam Smith", filer.Object))
            .Should().ThrowAsync<DbUpdateException>();

        _passkeyCredentials.Verify(
            p => p.RemoveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private async Task ApproveTheOneRequestAndAssertTheRequesterIsAdmittedAsync(Guid owner, Guid ownerRole, Guid requestorId)
    {
        var request = (await MembershipRequests().GetPendingRequestsAsync(_tenantId)).Should().ContainSingle().Which;
        request.SubjectId.Should().Be(requestorId);
        (await MembershipRequests().ApproveRequestAsync(request.Id, _tenantId, [ownerRole], owner, [Scope.FullAccess], null))
            .Success.Should().BeTrue();

        (await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == requestorId)).IsActive.Should().BeTrue();
        (await new TenantMemberService(new TenantMembershipRepository(new SharedSqliteFactory(_db.Options).AsNocturneFactory(), AccountLinkRoutes.Off), TimeProvider.System)
            .IsMemberAsync(requestorId, _tenantId)).Should().BeTrue();
    }

    [Fact]
    public async Task AccessRequestComplete_AfterAnEarlierAttemptFiledTheRequest_KeepsTheOneRequestAndTheCredential()
    {
        await AllowAccessRequestsAsync();
        var requestorId = await SeedPendingAccessRequestAsync("Sam Smith");
        StubRegistrationChallengeMintedFor(requestorId, PasskeyRegistrationFlow.AccessRequest);
        (await MembershipRequests().CreateRequestAsync(_tenantId, requestorId, "first attempt")).Success.Should().BeTrue();

        Assert.IsType<OkResult>(await CompleteAccessRequestAsync("Sam Smith"));

        (await MembershipRequests().GetPendingRequestsAsync(_tenantId)).Should().ContainSingle()
            .Which.Message.Should().Be("first attempt");
        _passkeyCredentials.Verify(
            p => p.RemoveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task AccessRequest_ApprovedByTheOwner_ActivatesTheRequesterWhoThenPassesTheMembershipGate()
    {
        await AllowAccessRequestsAsync();
        var owner = await SeedOwnerAsync("owner");
        var ownerRole = (await _dbContext.TenantMemberRoles.AsNoTracking().FirstAsync()).TenantRoleId;
        var requestorId = await RequestAccessAsync("Sam Smith", "I am the school nurse");
        var members = new TenantMemberService(new TenantMembershipRepository(new SharedSqliteFactory(_db.Options).AsNocturneFactory(), AccountLinkRoutes.Off), TimeProvider.System);
        (await members.IsMemberAsync(requestorId, _tenantId)).Should().BeFalse();

        var request = (await MembershipRequests().GetPendingRequestsAsync(_tenantId)).Should().ContainSingle().Which;
        (await MembershipRequests().ApproveRequestAsync(request.Id, _tenantId, [ownerRole], owner, [Scope.FullAccess], null))
            .Success.Should().BeTrue();

        var subject = await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == requestorId);
        subject.IsActive.Should().BeTrue();
        subject.ApprovalStatus.Should().Be("Approved");
        (await members.IsMemberAsync(requestorId, _tenantId)).Should().BeTrue();
        _requestAudit.Verify(a => a.LogAsync(
            AuthAuditEventType.SubjectUpdated, requestorId, true, It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.Is<string?>(d => d!.Contains("Approved")), It.IsAny<Guid?>(),
            It.IsAny<AuthAuditActor?>(), _tenantId), Times.Once());
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
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), "elsewhere", PasskeyRegistrationFlow.MemberInvite))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

        var result = await _controller.InviteOptions(
            new InviteOptionsRequest { Token = "invite-token", Username = "elsewhere", DisplayName = "Attacker" },
            inviteService.Object);

        Assert.IsType<OkObjectResult>(result.Result);
        var victim = await _dbContext.Subjects.AsNoTracking().FirstAsync(s => s.Id == elsewhereId);
        victim.Name.Should().Be("elsewhere", "another tenant's member must not be renamed by an invite here");
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(elsewhereId, It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>()),
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
        StubRegistrationChallengeMintedFor(elsewhereId, PasskeyRegistrationFlow.MemberInvite);

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "elsewhere",
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            inviteService.Object);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), elsewhereId, It.IsAny<string?>()),
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
        StubRegistrationChallengeMintedFor(elsewhereId, PasskeyRegistrationFlow.MemberInvite);

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "shared-name",
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            inviteService.Object);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), elsewhereId, It.IsAny<string?>()),
            Times.Never,
            "membership of any tenant, not just this one, disqualifies a candidate");
    }

    /// <summary>
    /// A challenge names one subject. Once that subject has joined a tenant it is an account, not an
    /// enrolment, whatever other subject holds the same username.
    /// </summary>
    [Fact]
    public async Task InviteComplete_WhenTheChallengesSubjectHasJoinedATenant_IsRefused()
    {
        await SeedEnrollingSubjectAsync("contested");
        var joined = await SeedEnrollingSubjectAsync("contested");
        await SeedMembershipAsync(joined, Guid.CreateVersion7());
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(joined, PasskeyRegistrationFlow.MemberInvite);

        var result = await InviteCompleteAsync("contested", inviteService);

        AssertRegistrationFailed(result.Result);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Never);
    }

    /// <summary>
    /// Two enrolments under one username each hold their own challenge, so each completes as the
    /// subject its own options step created, older or newer.
    /// </summary>
    [Fact]
    public async Task InviteComplete_WithTwoEnrollingSubjectsUnderOneUsername_CompletesAsTheOneTheChallengeNames()
    {
        var (olderId, newestId) = OrderedIdPair();
        var older = await SeedEnrollingSubjectAsync("invitee", olderId);
        var newest = await SeedEnrollingSubjectAsync("invitee", newestId);
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(older, PasskeyRegistrationFlow.MemberInvite);
        StubSignInFor(older);

        var result = await InviteCompleteAsync("invitee", inviteService);

        Assert.IsType<OkObjectResult>(result.Result);
        inviteService.Verify(s => s.AcceptInviteAsync("invite-token", older, _tenantId), Times.Once);
        inviteService.Verify(s => s.AcceptInviteAsync(It.IsAny<string>(), newest, It.IsAny<Guid>()), Times.Never);
    }

    /// <summary>
    /// A challenge whose subject holds another username was minted for another enrolment.
    /// </summary>
    [Fact]
    public async Task InviteComplete_WithAChallengeForAnEnrolmentUnderAnotherUsername_IsRefused()
    {
        var other = await SeedEnrollingSubjectAsync("someone-else");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(other, PasskeyRegistrationFlow.MemberInvite);

        var result = await InviteCompleteAsync("invitee", inviteService);

        AssertRegistrationFailed(result.Result);
        inviteService.Verify(
            s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    /// <summary>
    /// A token that is not an unexpired registration challenge names no subject.
    /// </summary>
    [Fact]
    public async Task InviteComplete_WithAChallengeThatNamesNoSubject_IsRefused()
    {
        await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();

        var result = await InviteCompleteAsync("invitee", inviteService);

        AssertRegistrationFailed(result.Result);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Never);
    }

    /// <summary>
    /// Cancelling the OS prompt abandons the ceremony and leaves the subject the options step
    /// created. Starting again creates a second subject under the same username, leaves the first
    /// as it was, and completes as the second; the first holds no membership, so a member lookup by
    /// the username finds only the second.
    /// </summary>
    [Fact]
    public async Task InviteOptions_AfterAnAbandonedCeremony_CreatesASecondSubjectThatCompletes()
    {
        var inviteService = StubValidInvite();
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), "invitee", PasskeyRegistrationFlow.MemberInvite))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

        Assert.IsType<OkObjectResult>((await _controller.InviteOptions(
            new InviteOptionsRequest { Token = "invite-token", Username = "Invitee", DisplayName = "Sam" },
            inviteService.Object)).Result);
        var abandoned = await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Username == "invitee");
        Assert.IsType<OkObjectResult>((await _controller.InviteOptions(
            new InviteOptionsRequest { Token = "invite-token", Username = "invitee", DisplayName = "Sam Smith" },
            inviteService.Object)).Result);

        var retry = await _dbContext.Subjects.AsNoTracking()
            .SingleAsync(s => s.Username == "invitee" && s.Id != abandoned.Id);
        retry.Name.Should().Be("Sam Smith");
        (await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == abandoned.Id))
            .Should().BeEquivalentTo(abandoned, "the abandoned subject is left as it was");

        StubRegistrationChallengeMintedFor(retry.Id, PasskeyRegistrationFlow.MemberInvite);
        StubSignInFor(retry.Id);
        inviteService.Setup(s => s.AcceptInviteAsync("invite-token", retry.Id, _tenantId))
            .Returns(async () =>
            {
                await SeedMembershipAsync(retry.Id, _tenantId);
                return new AcceptMemberInviteResult(true, MembershipId: Guid.CreateVersion7());
            });

        var result = await InviteCompleteAsync("invitee", inviteService);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<PasskeyRegistrationResponse>(ok.Value).Success.Should().BeTrue();
        (await new SubjectRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off)
                .FindMemberByUsernameAsync(_tenantId, "invitee", activeOnly: true, DateTime.UtcNow, CancellationToken.None))
            .Should().NotBeNull().And.Subject.As<SubjectRecord>().Id.Should().Be(retry.Id);
    }

    /// <summary>
    /// The creation link's retry: a second subject under the same username, which completes and
    /// owns the new tenant.
    /// </summary>
    [Fact]
    public async Task TenantInviteOptions_AfterAnAbandonedCeremony_CreatesASecondSubjectThatCompletes()
    {
        var tenantInvites = new Mock<ITenantCreationInviteService>();
        tenantInvites.Setup(s => s.GetInfoAsync("tenant-invite-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<TenantCreationInviteInfo>.Ok(
                new TenantCreationInviteInfo("example.test", "Admin", DateTime.UtcNow.AddDays(1))));
        _tenantService.Setup(s => s.ValidateSlugAsync("founder-site", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(true));
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), "founder", PasskeyRegistrationFlow.CreationLink))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));
        var options = new TenantInviteOptionsRequest
        {
            Token = "tenant-invite-token", Slug = "founder-site", SiteName = "Founder", Username = "founder", DisplayName = "Founder",
        };

        (await _controller.TenantInviteOptions(options, tenantInvites.Object)).Should().BeOfType<OkObjectResult>();
        var abandoned = await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Username == "founder");
        (await _controller.TenantInviteOptions(options, tenantInvites.Object)).Should().BeOfType<OkObjectResult>();
        var retry = await _dbContext.Subjects.AsNoTracking()
            .SingleAsync(s => s.Username == "founder" && s.Id != abandoned.Id);

        StubRegistrationChallengeMintedFor(retry.Id, PasskeyRegistrationFlow.CreationLink);
        StubSignInFor(retry.Id);
        var inviteId = Guid.CreateVersion7();
        tenantInvites.Setup(s => s.ClaimAsync("tenant-invite-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<Guid>.Ok(inviteId));
        tenantInvites
            .Setup(s => s.CreateTenantAsync(inviteId, "founder-site", "Founder", retry.Id, "founder", It.IsAny<CancellationToken>()))
            .Returns(() => TenantCreatedForAsync(retry.Id));

        var result = await _controller.TenantInviteComplete(
            new TenantInviteCompleteRequest
            {
                Token = "tenant-invite-token",
                Slug = "founder-site",
                SiteName = "Founder",
                Username = "founder",
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            tenantInvites.Object);

        result.Should().BeOfType<OkObjectResult>();
        tenantInvites.Verify(
            s => s.CreateTenantAsync(inviteId, "founder-site", "Founder", retry.Id, "founder", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The access-request retry. The abandoned request never registered a passkey, so it is not a
    /// conflict, the owners are not shown it, and the retry files the request as its own subject.
    /// </summary>
    [Fact]
    public async Task AccessRequestOptions_AfterAnAbandonedCeremony_CreatesASecondSubjectThatFilesTheRequest()
    {
        await AllowAccessRequestsAsync();
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), "sam-smith", PasskeyRegistrationFlow.AccessRequest))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

        Assert.IsType<OkObjectResult>((await _controller.AccessRequestOptions(
            new AccessRequestOptionsRequest { DisplayName = "Sam Smith" })).Result);
        var abandoned = await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Name == "Sam Smith");
        (await new SubjectRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off).ListPendingAccessRequestsAsync(CancellationToken.None))
            .Should().BeEmpty("a request that never registered a passkey is not one an owner can approve");

        Assert.IsType<OkObjectResult>((await _controller.AccessRequestOptions(
            new AccessRequestOptionsRequest { DisplayName = "Sam Smith", Message = "second try" })).Result);
        var retry = await _dbContext.Subjects.AsNoTracking()
            .SingleAsync(s => s.Name == "Sam Smith" && s.Id != abandoned.Id);
        retry.AccessRequestMessage.Should().Be("second try");
        (await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == abandoned.Id))
            .Should().BeEquivalentTo(abandoned, "the abandoned subject is left as it was");

        StubRegistrationChallengeMintedFor(retry.Id, PasskeyRegistrationFlow.AccessRequest);

        Assert.IsType<OkResult>(await CompleteAccessRequestAsync("Sam Smith"));
        (await MembershipRequests().GetPendingRequestsAsync(_tenantId)).Should().ContainSingle()
            .Which.SubjectId.Should().Be(retry.Id);
        (await new SubjectRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off).ListPendingAccessRequestsAsync(CancellationToken.None))
            .Select(s => s.Id).Should().Equal(retry.Id);
    }

    /// <summary>
    /// A challenge an invite enrolment minted names another flow, and its subject is not of the
    /// kind the access request options step creates (inactive and pending).
    /// </summary>
    [Fact]
    public async Task AccessRequestComplete_WithAChallengeForAnInviteEnrolment_IsRefused()
    {
        await AllowAccessRequestsAsync();
        var invitee = await SeedEnrollingSubjectAsync("Sam Smith");
        StubRegistrationChallengeMintedFor(invitee, PasskeyRegistrationFlow.MemberInvite);

        var result = await CompleteAccessRequestAsync("Sam Smith");

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Never);
    }

    public static TheoryData<PasskeyRegistrationFlow> FlowsOtherThan(PasskeyRegistrationFlow flow) =>
        new(Enum.GetValues<PasskeyRegistrationFlow>().Where(f => f != flow));

    /// <summary>
    /// The subject is one the invite options step would leave, so only the challenge's flow
    /// refuses it.
    /// </summary>
    [Theory]
    [MemberData(nameof(FlowsOtherThan), PasskeyRegistrationFlow.MemberInvite)]
    public async Task InviteComplete_WithAChallengeMintedForAnotherFlow_IsRefused(PasskeyRegistrationFlow minted)
    {
        var subject = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(subject, minted);

        var result = await InviteCompleteAsync("invitee", inviteService);

        AssertRegistrationFailed(result.Result);
        AssertNothingEnrolled(subject);
        inviteService.Verify(
            s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    /// <summary>
    /// A challenge <see cref="PasskeyController.RegisterOptions"/> minted, for a signed-in account or
    /// under a recovery session, enrols only through <see cref="PasskeyController.RegisterComplete"/>.
    /// </summary>
    [Fact]
    public async Task InviteComplete_WithAnOwnAccountChallenge_IsRefused()
    {
        var subject = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(subject, PasskeyRegistrationFlow.OwnAccount);

        var result = await InviteCompleteAsync("invitee", inviteService);

        AssertRegistrationFailed(result.Result);
        AssertNothingEnrolled(subject);
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [MemberData(nameof(FlowsOtherThan), PasskeyRegistrationFlow.CreationLink)]
    public async Task TenantInviteComplete_WithAChallengeMintedForAnotherFlow_IsRefused(PasskeyRegistrationFlow minted)
    {
        var subject = await SeedEnrollingSubjectAsync("founder");
        StubRegistrationChallengeMintedFor(subject, minted);
        var tenantInvites = new Mock<ITenantCreationInviteService>();

        var result = await _controller.TenantInviteComplete(
            new TenantInviteCompleteRequest
            {
                Token = "tenant-invite-token",
                Slug = "founder-site",
                SiteName = "Founder",
                Username = "founder",
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            tenantInvites.Object);

        AssertRegistrationFailed(result);
        AssertNothingEnrolled(subject);
        tenantInvites.Verify(s => s.ClaimAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(FlowsOtherThan), PasskeyRegistrationFlow.AccessRequest)]
    public async Task AccessRequestComplete_WithAChallengeMintedForAnotherFlow_IsRefused(PasskeyRegistrationFlow minted)
    {
        await AllowAccessRequestsAsync();
        var subject = await SeedPendingAccessRequestAsync("Sam Smith");
        StubRegistrationChallengeMintedFor(subject, minted);

        var result = await CompleteAccessRequestAsync("Sam Smith");

        AssertRegistrationFailed(result);
        AssertNothingEnrolled(subject);
        (await MembershipRequests().GetPendingRequestsAsync(_tenantId)).Should().BeEmpty();
    }

    private void AssertNothingEnrolled(Guid subjectId)
    {
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Never);
        using var db = _db.CreateContext();
        db.PasskeyCredentials.Any(c => c.SubjectId == subjectId).Should().BeFalse();
    }

    [Fact]
    public async Task AccessRequest_ByAVisitorWithNoMembership_IsListedForTheOwnerAndThePlatformAdminsWithItsMessage()
    {
        await AllowAccessRequestsAsync();
        var otherTenant = Guid.CreateVersion7();
        await EnsureTenantAsync(otherTenant);
        var requestorId = await RequestAccessAsync("Sam Smith", "  I am the school nurse  ");

        (await MembershipRequests().GetPendingRequestsAsync(_tenantId)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { SubjectId = requestorId, SubjectName = "Sam Smith", Message = "I am the school nurse" });
        (await MembershipRequests().GetPendingRequestsAsync(otherTenant)).Should().BeEmpty();

        var adminList = new AccessRequestController(
            new TenantMembershipRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off),
            new SubjectRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off),
            Mock.Of<ISubjectDeletion>(),
            new Mock<IAccessRequestService>().Object,
            _tenantAccessor.Object,
            new Mock<IInAppNotificationService>().Object,
            Mock.Of<ILogger<AccessRequestController>>());
        var listed = Assert.IsType<OkObjectResult>((await adminList.GetPendingRequests(CancellationToken.None)).Result);

        Assert.IsAssignableFrom<IEnumerable<AccessRequestDto>>(listed.Value).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { SubjectId = requestorId, Name = "Sam Smith", Message = "I am the school nurse" });
        var stored = await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Id == requestorId);
        stored.IsActive.Should().BeFalse();
        (await _dbContext.TenantMembers.AnyAsync(m => m.SubjectId == requestorId)).Should().BeFalse();
    }

    /// <summary>
    /// A pending request that finished registering is a real request awaiting approval, not an
    /// abandoned ceremony, so a second one under the same name is still a conflict.
    /// </summary>
    [Fact]
    public async Task AccessRequestOptions_WhenTheNameHasAFinishedPendingRequest_IsAConflict()
    {
        await AllowAccessRequestsAsync();
        await SeedFinishedAccessRequestAsync("Sam Smith", _tenantId);

        var result = await _controller.AccessRequestOptions(
            new AccessRequestOptionsRequest { DisplayName = "Sam Smith" });

        Assert.IsType<ConflictObjectResult>(result.Result);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>()),
            Times.Never);
    }

    /// <summary>
    /// A request a visitor filed on another tenant is not a request on this one, so the name is free
    /// here and the visitor of this tenant learns nothing of the other.
    /// </summary>
    [Fact]
    public async Task AccessRequestOptions_WhenTheNameIsPendingOnAnotherTenant_IsNotAConflict()
    {
        await AllowAccessRequestsAsync();
        var otherTenantId = Guid.CreateVersion7();
        _dbContext.Tenants.Add(new TenantEntity { Id = otherTenantId, Slug = "other", DisplayName = "Other" });
        await _dbContext.SaveChangesAsync();
        await SeedFinishedAccessRequestAsync("Sam Smith", otherTenantId);
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), PasskeyRegistrationFlow.AccessRequest))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

        var result = await _controller.AccessRequestOptions(
            new AccessRequestOptionsRequest { DisplayName = "Sam Smith" });

        Assert.IsType<OkObjectResult>(result.Result);
    }

    /// <summary>A pending subject with a passkey and a pending request to join <paramref name="tenantId"/>.</summary>
    private async Task SeedFinishedAccessRequestAsync(string displayName, Guid tenantId)
    {
        var requestorId = await SeedPendingAccessRequestAsync(displayName);
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = requestorId,
            CredentialId = Guid.CreateVersion7().ToByteArray(),
            PublicKey = [1, 2, 3],
            CreatedAt = DateTime.UtcNow,
        });
        _dbContext.MembershipRequests.Add(new MembershipRequestEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubjectId = requestorId,
            Status = Nocturne.Core.Models.Identity.MembershipRequestRecord.Pending,
        });
        await _dbContext.SaveChangesAsync();
    }

    /// <summary>The registration challenge token every enrolment test presents.</summary>
    private const string EnrolmentChallenge = "challenge";

    /// <summary>
    /// Makes the passkey service behave as the real one does for <see cref="EnrolmentChallenge"/>
    /// minted by <paramref name="flow"/> for <paramref name="subjectId"/>: it accepts that subject
    /// as the enrolling one in that flow, and refuses every other subject, token and flow.
    /// </summary>
    /// <returns>The id of the credential the stub reports stored.</returns>
    private Guid StubRegistrationChallengeMintedFor(Guid subjectId, PasskeyRegistrationFlow flow)
    {
        var credentialId = Guid.CreateVersion7();
        _passkeyService.Setup(s => s.FindRegistrationChallengeSubject(EnrolmentChallenge, flow)).Returns(subjectId);
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .ReturnsAsync((string _, string challengeToken, PasskeyRegistrationFlow redeemedIn, Guid _, Guid expectedSubjectId, string? _) =>
            {
                if (challengeToken != EnrolmentChallenge || redeemedIn != flow)
                    throw new InvalidOperationException("Registration challenge was issued for a different flow.");
                if (expectedSubjectId != subjectId)
                    throw new InvalidOperationException("Registration challenge was not issued for the enrolling subject.");

                using var db = _db.CreateContext();
                db.PasskeyCredentials.Add(new PasskeyCredentialEntity
                {
                    Id = credentialId,
                    SubjectId = subjectId,
                    CredentialId = credentialId.ToByteArray(),
                    PublicKey = [1, 2, 3],
                    CreatedAt = DateTime.UtcNow,
                });
                db.SaveChanges();
                return new PasskeyCredentialResult(credentialId, subjectId);
            });
        return credentialId;
    }

    private void StubSignInFor(Guid subjectId)
    {
        _recoveryCodeService.Setup(s => s.GenerateCodesAsync(subjectId)).ReturnsAsync(["code-1"]);
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, subjectId));
    }

    private Task<ActionResult<PasskeyRegistrationResponse>> InviteCompleteAsync(
        string username, Mock<IMemberInviteService> inviteService) =>
        _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = username,
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            inviteService.Object);

    /// <summary>The answer the invite and link flows give a registration that failed.</summary>
    private static void AssertRegistrationFailed(IActionResult? result)
    {
        var problem = Assert.IsType<ObjectResult>(result);
        problem.StatusCode.Should().Be(400);
        Assert.IsType<ProblemDetails>(problem.Value).Detail.Should().Be("Passkey registration failed");
    }

    private Mock<IMemberInviteService> StubValidInvite()
    {
        var inviteService = new Mock<IMemberInviteService>();
        inviteService.Setup(s => s.GetInviteByTokenAsync("invite-token", _tenantId))
            .ReturnsAsync(new MemberInviteInfo(
                Guid.CreateVersion7(), _tenantId, "Test", "Owner", [], null, null, false,
                DateTime.UtcNow.AddDays(1), null, 0, true, false, false, DateTime.UtcNow, [], [], [], null));
        inviteService.Setup(s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns(async (string _, Guid subjectId, Guid tenantId) =>
            {
                await SeedMembershipAsync(subjectId, tenantId);
                return new AcceptMemberInviteResult(true, MembershipId: Guid.CreateVersion7());
            });
        return inviteService;
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

    private async Task<Guid> SeedOwnerAsync(
        string username,
        bool isActive = true,
        bool isSystemSubject = false)
    {
        await EnsureTenantAsync(_tenantId);
        return await TestDatabaseSeeder.SeedMemberAsync(
            _dbContext, _tenantId, name: username,
            isActive: isActive, isSystemSubject: isSystemSubject);
    }

    /// <summary>Runs the options and complete steps as the dialog does; returns the requester.</summary>
    private async Task<Guid> RequestAccessAsync(string displayName, string message)
    {
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), PasskeyRegistrationFlow.AccessRequest))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));
        Assert.IsType<OkObjectResult>((await _controller.AccessRequestOptions(
            new AccessRequestOptionsRequest { DisplayName = displayName, Message = message })).Result);
        var requestorId = (await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Name == displayName)).Id;
        StubRegistrationChallengeMintedFor(requestorId, PasskeyRegistrationFlow.AccessRequest);
        Assert.IsType<OkResult>(await CompleteAccessRequestAsync(displayName));
        return requestorId;
    }

    private Task<ActionResult> CompleteAccessRequestAsync(string displayName, IMembershipRequestService? filer = null) =>
        _controller.AccessRequestComplete(
            new AccessRequestCompleteRequest
            {
                DisplayName = displayName,
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            FilingScopes(filer), _passkeyCredentials.Object);

    /// <summary>The service owners decide requests through, over this test's database.</summary>
    private MembershipRequestService MembershipRequests()
    {
        var contexts = new SharedSqliteFactory(_db.Options).AsNocturneFactory();
        return new MembershipRequestService(
            new TenantRepository(contexts),
            new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
            new SubjectRepository(contexts, AccountLinkRoutes.Off),
            contexts,
            new TenantRoleService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System, Moq.Mock.Of<Nocturne.Core.Contracts.Identity.IMemberInviteRepository>(), new Nocturne.Infrastructure.Data.Repositories.TeamRepository(contexts), contexts, new Nocturne.API.Services.Auth.GrantRevocationService(new Nocturne.API.Services.Auth.GuestSessionCacheService(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())))),
            _notifications.Object,
            _requestAudit.Object,
            TimeProvider.System,
            NullLogger<MembershipRequestService>.Instance, AccountLinkRoutes.Off,
            new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))), InertTeamStaff.Announcer());
    }

    private sealed class SettableTenantAccessor : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;
        public void SetTenant(TenantContext context) => Context = context;
    }

    /// <summary>
    /// The scopes the complete step files its request in. Each starts unresolved, as a scope opened
    /// in code does, so the step must pin the tenant itself.
    /// </summary>
    private IServiceScopeFactory FilingScopes(IMembershipRequestService? filer = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor, SettableTenantAccessor>();
        services.AddScoped(_ => filer ?? MembershipRequests());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
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

    private Task<ActionResult<PasskeyLoginCompleteResponse>> LoginCompleteAsync(bool showsRecoveryCodes = false) =>
        _controller.LoginComplete(new PasskeyLoginCompleteRequest
        {
            AssertionResponseJson = "{}",
            ChallengeToken = "assertion-token",
            ShowsRecoveryCodes = showsRecoveryCodes,
        });

    [Fact]
    public async Task LoginComplete_WhenTheSubjectHasAnAuthenticator_WithholdsTheSession()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        _totpService
            .Setup(s => s.GetSecondFactorRequirementAsync(subjectId, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        _totpService.Setup(s => s.CreateStepUpTokenAsync(subjectId, _tenantId, false)).ReturnsAsync("step-up-token");

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

        _controller.HttpContext.Response.Headers.SetCookie.ToString()
            .Should().Contain($"{SessionCookieExtensions.LastSignInCookieName}=passkey",
                "the authenticator step that follows does not know which primary factor preceded it");
    }

    [Fact]
    public async Task LoginComplete_WhenTheSubjectHasNoAuthenticator_IssuesTheSession()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        _totpService
            .Setup(s => s.GetSecondFactorRequirementAsync(subjectId, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.NotEnrolled);
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, subjectId));

        var result = await LoginCompleteAsync();

        var response = Assert.IsType<PasskeyLoginCompleteResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        response.TotpRequired.Should().BeFalse();
        response.AccessToken.Should().Be("access");
        _sessionService.Verify(
            s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _totpService.Verify(s => s.CreateStepUpTokenAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// A passkey sign-in over a session redeemed from a login code ends that session, so its refresh
    /// token does not stay live beside the person's own sign-in; over an ordinary one it ends nothing.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginComplete_OverAHandedOffSession_EndsIt(bool handedOff)
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, subjectId));
        var refreshTokens = new Mock<IRefreshTokenService>();
        refreshTokens.Setup(r => r.FindOwnerAsync("presented"))
            .ReturnsAsync(new RefreshTokenSession(subjectId, "previous-session", handedOff));
        var audit = new Mock<IAuthAuditService>();
        _controller.HttpContext.RequestServices = new ServiceCollection()
            .AddSingleton(refreshTokens.Object).AddSingleton(audit.Object).BuildServiceProvider();
        _controller.HttpContext.Request.Headers.Cookie = ".Nocturne.RefreshToken=presented";

        await LoginCompleteAsync();

        refreshTokens.Verify(
            r => r.RevokeSessionForSubjectAsync(subjectId, "previous-session", It.IsAny<string>()),
            handedOff ? Times.Once() : Times.Never());
        audit.Verify(
            a => a.LogAsync(AuthAuditEventType.Logout, subjectId, true, It.IsAny<string?>(), It.IsAny<string?>(),
                null, It.Is<string?>(d => d != null && d.Contains("replaced_by_sign_in")), null, null, null),
            handedOff ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task LoginComplete_WhenItIssuesTheSession_RecordsTheSignInTime()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, subjectId));

        await LoginCompleteAsync();

        _subjectService.Verify(s => s.UpdateLastLoginAsync(subjectId), Times.Once);
    }

    [Fact]
    public async Task LoginComplete_WhenACodeIsStillRequired_LeavesTheSignInTimeAlone()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        _totpService
            .Setup(s => s.GetSecondFactorRequirementAsync(subjectId, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        _totpService.Setup(s => s.CreateStepUpTokenAsync(subjectId, _tenantId, false)).ReturnsAsync("step-up-token");

        await LoginCompleteAsync();

        _subjectService.Verify(
            s => s.UpdateLastLoginAsync(It.IsAny<Guid>()), Times.Never,
            "the sign-in completes at the code step, which records the time itself");
    }

    [Fact]
    public async Task LoginComplete_WhenItIssuesTheSession_ThenAnswersWithTheFirstRecoveryCodes()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        var steps = new List<string>();
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => steps.Add("session"))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, subjectId));
        _recoveryCodeService.Setup(s => s.GenerateFirstCodesAsync(subjectId))
            .Callback(() => steps.Add("first-codes"))
            .ReturnsAsync(["code-1", "code-2"]);

        var result = await LoginCompleteAsync(showsRecoveryCodes: true);

        var response = Assert.IsType<PasskeyLoginCompleteResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        response.RecoveryCodes.Should().Equal("code-1", "code-2");
        steps.Should().Equal("session", "first-codes");
        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task LoginComplete_ForAMemberWhoHasHeldRecoveryCodes_AnswersWithNone()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, subjectId));
        _recoveryCodeService.Setup(s => s.GenerateFirstCodesAsync(subjectId)).ReturnsAsync((List<string>?)null);

        var result = await LoginCompleteAsync(showsRecoveryCodes: true);

        Assert.IsType<PasskeyLoginCompleteResponse>(Assert.IsType<OkObjectResult>(result.Result).Value)
            .RecoveryCodes.Should().BeNull();
    }

    [Fact]
    public async Task LoginComplete_ForACallerThatDoesNotShowRecoveryCodes_MakesNone()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, subjectId));
        _recoveryCodeService.Setup(s => s.GenerateFirstCodesAsync(subjectId)).ReturnsAsync(["code-1"]);

        var result = await LoginCompleteAsync(showsRecoveryCodes: false);

        var response = Assert.IsType<PasskeyLoginCompleteResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        response.AccessToken.Should().Be("access");
        response.RecoveryCodes.Should().BeNull();
        _recoveryCodeService.Verify(s => s.GenerateFirstCodesAsync(It.IsAny<Guid>()), Times.Never);
        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task LoginComplete_WhenACodeIsStillRequired_MakesNoRecoveryCodes()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        _totpService
            .Setup(s => s.GetSecondFactorRequirementAsync(subjectId, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        _totpService.Setup(s => s.CreateStepUpTokenAsync(subjectId, _tenantId, false)).ReturnsAsync("step-up-token");

        await LoginCompleteAsync(showsRecoveryCodes: true);

        _recoveryCodeService.Verify(s => s.GenerateFirstCodesAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task LoginComplete_ForAPasskeyHolderWhoIsNotAMember_MakesNoRecoveryCodes()
    {
        var subjectId = Guid.CreateVersion7();
        StubSuccessfulAssertion(subjectId, "outsider");

        var result = await LoginCompleteAsync(showsRecoveryCodes: true);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _recoveryCodeService.Verify(s => s.GenerateFirstCodesAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task LoginComplete_WhenTheSessionCannotBeIssued_MakesNoRecoveryCodes()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("the session was not stored"));

        var act = () => LoginCompleteAsync(showsRecoveryCodes: true);

        await act.Should().ThrowAsync<DbUpdateException>();
        _recoveryCodeService.Verify(s => s.GenerateFirstCodesAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task LoginComplete_FromATrustedDevice_IssuesTheSessionWithoutACode()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        _controller.HttpContext.Request.Headers.Cookie =
            $"{SessionCookieExtensions.TrustedDeviceCookieName}=device-token";
        _totpService
            .Setup(s => s.GetSecondFactorRequirementAsync(subjectId, "device-token"))
            .ReturnsAsync(SecondFactorRequirement.SatisfiedByTrustedDevice);
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, subjectId));

        var result = await LoginCompleteAsync();

        var response = Assert.IsType<PasskeyLoginCompleteResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        response.TotpRequired.Should().BeFalse();
        response.AccessToken.Should().Be("access");
        _totpService.Verify(s => s.CreateStepUpTokenAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task LoginComplete_ChecksTheDeviceCookieAgainstTheSubjectThePasskeyNamed()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        _controller.HttpContext.Request.Headers.Cookie =
            $"{SessionCookieExtensions.TrustedDeviceCookieName}=device-token";
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, subjectId));

        await LoginCompleteAsync();

        _totpService.Verify(
            s => s.GetSecondFactorRequirementAsync(subjectId, "device-token"),
            Times.Once,
            "the cookie is checked against the subject the assertion verified, never one the cookie names");
    }

    #endregion

    #region Invite acceptance and the creation link finish through the sign-in completion

    private const string InviteRow = """{"method":"passkey","enrolment":"invite"}""";
    private const string CreationLinkRow = """{"method":"passkey","enrolment":"tenant_invite"}""";
    private readonly Guid _creationLinkId = Guid.CreateVersion7();

    public static TheoryData<Exception> RefusedRegistrations() => new()
    {
        new InvalidOperationException("Registration challenge was not issued for the enrolling subject."),
        new System.Text.Json.JsonException("not an attestation"),
        new PasskeyRegistrationRefusedException(
            "The attestation was refused.", new System.Formats.Asn1.AsnContentException("Must be a primitive")),
    };

    /// <summary>What <see cref="ITenantCreationInviteService.CreateTenantAsync"/> leaves: the tenant, with the owner's membership made in the same transaction.</summary>
    private async Task<TenantInviteOutcome<TenantCreationInviteRedeemed>> TenantCreatedForAsync(
        Guid ownerId, Guid? tenantId = null)
    {
        var created = tenantId ?? Guid.CreateVersion7();
        await SeedMembershipAsync(ownerId, created);
        return TenantInviteOutcome<TenantCreationInviteRedeemed>.Ok(
            new TenantCreationInviteRedeemed(created, "founder-site", "Founder", "https://founder-site.example"));
    }

    /// <summary>A creation link whose claim succeeds and whose creation does what <paramref name="creates"/> does.</summary>
    private Mock<ITenantCreationInviteService> StubCreationLink(
        Guid ownerId, Func<Task<TenantInviteOutcome<TenantCreationInviteRedeemed>>>? creates = null)
    {
        _tenantService.Setup(s => s.ValidateSlugAsync("founder-site", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(true));
        var invites = new Mock<ITenantCreationInviteService>();
        invites.Setup(s => s.ClaimAsync("tenant-invite-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<Guid>.Ok(_creationLinkId));
        invites
            .Setup(s => s.CreateTenantAsync(
                _creationLinkId, "founder-site", "Founder", ownerId, "founder", It.IsAny<CancellationToken>()))
            .Returns(creates ?? (() => TenantCreatedForAsync(ownerId)));
        return invites;
    }

    private Task<IActionResult> TenantInviteCompleteAsync(Mock<ITenantCreationInviteService> invites) =>
        _controller.TenantInviteComplete(
            new TenantInviteCompleteRequest
            {
                Token = "tenant-invite-token",
                Slug = "founder-site",
                SiteName = "Founder",
                Username = "founder",
                AttestationResponseJson = "{}",
                ChallengeToken = EnrolmentChallenge,
            },
            invites.Object);

    private void RecordSignInSteps(Guid subjectId, List<string> steps)
    {
        _recoveryCodeService.Setup(s => s.GenerateCodesAsync(subjectId))
            .Callback(() => steps.Add("codes"))
            .ReturnsAsync(["code-1"]);
        StubSession(subjectId, steps);
        _subjectService.Setup(s => s.UpdateLastLoginAsync(subjectId))
            .Callback(() => steps.Add("last-login"))
            .Returns(Task.CompletedTask);
    }

    private async Task<bool> HoldsPasskeyAsync(Guid subjectId) =>
        await _dbContext.PasskeyCredentials.AsNoTracking().AnyAsync(c => c.SubjectId == subjectId);

    private async Task<bool> IsMemberAsync(Guid subjectId, Guid tenantId) =>
        await _dbContext.TenantMembers.AsNoTracking().AnyAsync(m => m.SubjectId == subjectId && m.TenantId == tenantId);

    private string SetCookies() => _controller.HttpContext.Response.Headers.SetCookie.ToString();

    [Fact]
    public async Task InviteComplete_SignsTheInviteeInAfterTheInviteIsAccepted_WithTheInviteRowAndTheSignInHint()
    {
        FromTheTestClient();
        var steps = new List<string>();
        var rows = RecordRows(_auditService, steps);
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        inviteService.Setup(s => s.AcceptInviteAsync("invite-token", inviteeId, _tenantId))
            .Returns(async () =>
            {
                steps.Add("accept");
                await SeedMembershipAsync(inviteeId, _tenantId);
                return new AcceptMemberInviteResult(true, MembershipId: Guid.CreateVersion7());
            });
        StubRegistrationChallengeMintedFor(inviteeId, PasskeyRegistrationFlow.MemberInvite);
        RecordSignInSteps(inviteeId, steps);

        var result = await InviteCompleteAsync("invitee", inviteService);

        Assert.IsType<PasskeyRegistrationResponse>(Assert.IsType<OkObjectResult>(result.Result).Value)
            .Should().BeEquivalentTo(new PasskeyRegistrationResponse
            {
                Success = true, RecoveryCodes = ["code-1"], AccessToken = "access", RefreshToken = "refresh", ExpiresIn = 900,
            });
        steps.Should().Equal("accept", "session", "last-login", $"audit:{AuthAuditEventType.Login}", "codes");
        _totpService.Verify(s => s.GetSecondFactorRequirementAsync(inviteeId, null), Times.Once);
        var invitePasskey = new SessionContext(DeviceDescription: "Invite Passkey", IpAddress: ClientIp, UserAgent: ClientAgent);
        _sessionService.Verify(s => s.IssueSessionAsync(inviteeId, invitePasskey, It.IsAny<CancellationToken>()));
        rows.Should().ContainSingle().Which.Should().Be(
            new AuditRow(AuthAuditEventType.Login, inviteeId, true, ClientIp, ClientAgent, null, InviteRow));
        SetCookies().Should().Contain(".Nocturne.AccessToken=access")
            .And.Contain(".Nocturne.RefreshToken=refresh")
            .And.Contain($"{SessionCookieExtensions.LastSignInCookieName}=passkey");
    }

    [Fact]
    public async Task TenantInviteComplete_SignsTheOwnerInAsAMemberOfTheNewTenant_WithTheCreationLinkRowAndTheSignInHint()
    {
        FromTheTestClient();
        var steps = new List<string>();
        var rows = RecordRows(_auditService, steps);
        var ownerId = await SeedEnrollingSubjectAsync("founder");
        StubRegistrationChallengeMintedFor(ownerId, PasskeyRegistrationFlow.CreationLink);
        var tenantId = Guid.CreateVersion7();
        var invites = StubCreationLink(ownerId, async () =>
        {
            steps.Add("create");
            return await TenantCreatedForAsync(ownerId, tenantId);
        });
        RecordSignInSteps(ownerId, steps);

        var result = await TenantInviteCompleteAsync(invites);

        var response = Assert.IsType<TenantInviteRegistrationResponse>(Assert.IsType<OkObjectResult>(result).Value);
        response.AccessToken.Should().Be("access");
        response.RefreshToken.Should().Be("refresh");
        response.RecoveryCodes.Should().Equal("code-1");
        response.Tenant!.TenantId.Should().Be(tenantId);
        response.TenantRefusal.Should().BeNull();
        steps.Should().Equal("create", "session", "last-login", $"audit:{AuthAuditEventType.Login}", "codes");
        _totpService.Verify(s => s.GetSecondFactorRequirementAsync(ownerId, null), Times.Once);
        var invitePasskey = new SessionContext(DeviceDescription: "Invite Passkey", IpAddress: ClientIp, UserAgent: ClientAgent);
        _sessionService.Verify(s => s.IssueSessionAsync(ownerId, invitePasskey, It.IsAny<CancellationToken>()));
        rows.Should().ContainSingle().Which.Should().Be(
            new AuditRow(AuthAuditEventType.Login, ownerId, true, ClientIp, ClientAgent, null, CreationLinkRow));
        SetCookies().Should().Contain(".Nocturne.AccessToken=access")
            .And.Contain($"{SessionCookieExtensions.LastSignInCookieName}=passkey");
    }

    [Fact]
    public async Task TenantInviteComplete_WhenTheNewTenantHoldsNoMembershipForTheOwner_IsRefusedWithTheRowAndNoSession()
    {
        FromTheTestClient();
        var rows = RecordRows(_auditService);
        var ownerId = await SeedEnrollingSubjectAsync("founder");
        StubRegistrationChallengeMintedFor(ownerId, PasskeyRegistrationFlow.CreationLink);
        var invites = StubCreationLink(ownerId, () => Task.FromResult(TenantInviteOutcome<TenantCreationInviteRedeemed>.Ok(
            new TenantCreationInviteRedeemed(Guid.CreateVersion7(), "founder-site", "Founder", "https://founder-site.example"))));
        StubSignInFor(ownerId);

        var result = await TenantInviteCompleteAsync(invites);

        AssertRegistrationFailed(result);
        rows.Should().ContainSingle().Which.Should().Be(new AuditRow(
            AuthAuditEventType.FailedAuth, ownerId, false, ClientIp, ClientAgent, null,
            """{"method":"passkey","enrolment":"tenant_invite","reason":"not_a_member"}"""));
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        SetCookies().Should().BeEmpty();
    }

    [Fact]
    public async Task TenantInviteComplete_WhenTheTenantIsRefused_SignsTheAccountInWithNoMembershipToCheck()
    {
        FromTheTestClient();
        var rows = RecordRows(_auditService);
        var ownerId = await SeedEnrollingSubjectAsync("founder");
        StubRegistrationChallengeMintedFor(ownerId, PasskeyRegistrationFlow.CreationLink);
        var invites = StubCreationLink(ownerId, () => Task.FromResult(
            TenantInviteOutcome<TenantCreationInviteRedeemed>.Refused(TenantInviteRefusalCode.InvalidSlug, "This name is already taken")));
        StubSignInFor(ownerId);

        var result = await TenantInviteCompleteAsync(invites);

        var response = Assert.IsType<TenantInviteRegistrationResponse>(Assert.IsType<OkObjectResult>(result).Value);
        response.AccessToken.Should().Be("access");
        response.Tenant.Should().BeNull();
        response.TenantRefusal.Should().Be("This name is already taken");
        rows.Should().ContainSingle().Which.Details.Should().Be(CreationLinkRow);
        (await _dbContext.TenantMembers.AsNoTracking().AnyAsync(m => m.SubjectId == ownerId))
            .Should().BeFalse("the account is a member nowhere, and is signed in all the same");
    }

    [Fact]
    public async Task InviteComplete_WhenTheMembershipIsGoneBeforeTheSignIn_SaysTheInviteWasAccepted_WithTheInviteRowAndNoSession()
    {
        FromTheTestClient();
        var rows = RecordRows(_auditService);
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        inviteService.Setup(s => s.AcceptInviteAsync("invite-token", inviteeId, _tenantId))
            .ReturnsAsync(new AcceptMemberInviteResult(true, MembershipId: Guid.CreateVersion7()));
        StubRegistrationChallengeMintedFor(inviteeId, PasskeyRegistrationFlow.MemberInvite);
        StubSignInFor(inviteeId);

        var result = await InviteCompleteAsync("invitee", inviteService);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        problem.StatusCode.Should().Be(400);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        details.Title.Should().Be("Invite Error");
        details.Detail.Should().Be("The invite was accepted, but the account is no longer a member.");
        rows.Should().ContainSingle().Which.Should().Be(new AuditRow(
            AuthAuditEventType.FailedAuth, inviteeId, false, ClientIp, ClientAgent, null,
            """{"method":"passkey","enrolment":"invite","reason":"not_a_member"}"""));
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        SetCookies().Should().BeEmpty();
    }

    [Fact]
    public async Task InviteComplete_WhenACodeIsOwed_ShowsTheRecoveryCodesAndIssuesNoSession()
    {
        FromTheTestClient();
        var rows = RecordRows(_auditService);
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(inviteeId, PasskeyRegistrationFlow.MemberInvite);
        StubSignInFor(inviteeId);
        _totpService.Setup(s => s.GetSecondFactorRequirementAsync(inviteeId, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        _totpService.Setup(s => s.CreateStepUpTokenAsync(inviteeId, _tenantId, false)).ReturnsAsync("step-up-token");

        var result = await InviteCompleteAsync("invitee", inviteService);

        var response = Assert.IsType<PasskeyRegistrationResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        response.RecoveryCodes.Should().Equal("code-1");
        response.AccessToken.Should().BeEmpty();
        response.RefreshToken.Should().BeNull();
        _totpService.Verify(s => s.CreateStepUpTokenAsync(inviteeId, _tenantId, false), Times.Once);
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        rows.Should().ContainSingle().Which.Should().Be(new AuditRow(
            AuthAuditEventType.Login, inviteeId, false, ClientIp, ClientAgent, null,
            """{"method":"passkey","enrolment":"invite","secondFactorPending":true}"""));
        SetCookies().Should().Contain($"{SessionCookieExtensions.LastSignInCookieName}=passkey")
            .And.NotContain(".Nocturne.AccessToken");
        (await HoldsPasskeyAsync(inviteeId) && await IsMemberAsync(inviteeId, _tenantId))
            .Should().BeTrue("the invitee signs in with the passkey and the code");
    }

    [Fact]
    public async Task TenantInviteComplete_WhenACodeIsOwed_ShowsTheRecoveryCodesAndTheTenantAndIssuesNoSession()
    {
        var rows = RecordRows(_auditService);
        var ownerId = await SeedEnrollingSubjectAsync("founder");
        StubRegistrationChallengeMintedFor(ownerId, PasskeyRegistrationFlow.CreationLink);
        var tenantId = Guid.CreateVersion7();
        var invites = StubCreationLink(ownerId, () => TenantCreatedForAsync(ownerId, tenantId));
        StubSignInFor(ownerId);
        _totpService.Setup(s => s.GetSecondFactorRequirementAsync(ownerId, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        _totpService.Setup(s => s.CreateStepUpTokenAsync(ownerId, tenantId, false)).ReturnsAsync("step-up-token");

        var result = await TenantInviteCompleteAsync(invites);

        var response = Assert.IsType<TenantInviteRegistrationResponse>(Assert.IsType<OkObjectResult>(result).Value);
        response.RecoveryCodes.Should().Equal("code-1");
        response.AccessToken.Should().BeNullOrEmpty();
        response.Tenant!.TenantId.Should().Be(tenantId);
        _totpService.Verify(s => s.CreateStepUpTokenAsync(ownerId, tenantId, false), Times.Once);
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        rows.Should().ContainSingle().Which.Details.Should()
            .Be("""{"method":"passkey","enrolment":"tenant_invite","secondFactorPending":true}""");
    }

    [Theory]
    [MemberData(nameof(RefusedRegistrations))]
    public async Task InviteComplete_WhenTheRegistrationIsRefused_AnswersRegistrationFailedAndSignsNoOneIn(Exception refusal)
    {
        var rows = RecordRows(_auditService);
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(inviteeId, PasskeyRegistrationFlow.MemberInvite);
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .ThrowsAsync(refusal);

        var result = await InviteCompleteAsync("invitee", inviteService);

        AssertRegistrationFailed(result.Result);
        inviteService.Verify(s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        rows.Should().BeEmpty();
        SetCookies().Should().BeEmpty();
    }

    [Fact]
    public async Task InviteComplete_WhenTheRegistrationIsRefused_LogsAWarningWithTheRefusalAndTheFlow()
    {
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(inviteeId, PasskeyRegistrationFlow.MemberInvite);
        var refusal = new PasskeyRegistrationRefusedException("The attestation was refused.");
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .ThrowsAsync(refusal);

        await InviteCompleteAsync("invitee", inviteService);

        _logger.Verify(
            l => l.Log(
                LogLevel.Warning, It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString() == "Passkey registration for MemberInvite was refused"),
                refusal, It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task InviteComplete_WhenTheRegistrationFailsForAnotherReason_IsNotAnsweredAsARefusal()
    {
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(inviteeId, PasskeyRegistrationFlow.MemberInvite);
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .ThrowsAsync(new TimeoutException("the database did not answer"));

        var complete = () => InviteCompleteAsync("invitee", inviteService);

        await complete.Should().ThrowAsync<TimeoutException>();
        inviteService.Verify(s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(RefusedRegistrations))]
    public async Task TenantInviteComplete_WhenTheRegistrationIsRefused_ReleasesTheClaimAndSignsNoOneIn(Exception refusal)
    {
        var rows = RecordRows(_auditService);
        var ownerId = await SeedEnrollingSubjectAsync("founder");
        StubRegistrationChallengeMintedFor(ownerId, PasskeyRegistrationFlow.CreationLink);
        var invites = StubCreationLink(ownerId);
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .ThrowsAsync(refusal);

        var result = await TenantInviteCompleteAsync(invites);

        AssertRegistrationFailed(result);
        invites.Verify(s => s.ReleaseAsync(_creationLinkId), Times.Once);
        invites.Verify(
            s => s.CreateTenantAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        rows.Should().BeEmpty();
        SetCookies().Should().BeEmpty();
    }

    [Fact]
    public async Task TenantInviteComplete_WhenTheRegistrationFailsForAnotherReason_ReleasesTheClaimAndIsNotAnsweredAsARefusal()
    {
        var ownerId = await SeedEnrollingSubjectAsync("founder");
        StubRegistrationChallengeMintedFor(ownerId, PasskeyRegistrationFlow.CreationLink);
        var invites = StubCreationLink(ownerId);
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .ThrowsAsync(new TimeoutException("the database did not answer"));

        var complete = () => TenantInviteCompleteAsync(invites);

        await complete.Should().ThrowAsync<TimeoutException>();
        invites.Verify(s => s.ReleaseAsync(_creationLinkId), Times.Once);
    }

    [Fact]
    public async Task InviteComplete_WhenTheInviteIsRefused_WritesNoSignInRowAndSetsNoCookie()
    {
        var rows = RecordRows(_auditService);
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        inviteService.Setup(s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(new AcceptMemberInviteResult(false, "invite_exhausted", "This invite has no uses left."));
        var credentialId = StubRegistrationChallengeMintedFor(inviteeId, PasskeyRegistrationFlow.MemberInvite);

        var result = await InviteCompleteAsync("invitee", inviteService);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        problem.StatusCode.Should().Be(400);
        Assert.IsType<ProblemDetails>(problem.Value).Detail.Should().Be("This invite has no uses left.");
        _passkeyService.Verify(s => s.RemoveCredentialAsync(credentialId, inviteeId, _tenantId), Times.Once);
        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(It.IsAny<Guid>()), Times.Never);
        _subjectService.Verify(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()), Times.Never);
        rows.Should().BeEmpty();
        SetCookies().Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InviteComplete_OverAHandedOffSession_EndsIt(bool handedOff)
    {
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(inviteeId, PasskeyRegistrationFlow.MemberInvite);
        StubSignInFor(inviteeId);
        var refreshTokens = new Mock<IRefreshTokenService>();
        var handedOffHolder = Guid.CreateVersion7();
        refreshTokens.Setup(r => r.FindOwnerAsync("presented"))
            .ReturnsAsync(new RefreshTokenSession(handedOffHolder, "previous-session", handedOff));
        var audit = new Mock<IAuthAuditService>();
        _controller.HttpContext.RequestServices = new ServiceCollection()
            .AddSingleton(refreshTokens.Object).AddSingleton(audit.Object).BuildServiceProvider();
        _controller.HttpContext.Request.Headers.Cookie = ".Nocturne.RefreshToken=presented";

        Assert.IsType<OkObjectResult>((await InviteCompleteAsync("invitee", inviteService)).Result);

        refreshTokens.Verify(
            r => r.RevokeSessionForSubjectAsync(handedOffHolder, "previous-session", It.IsAny<string>()),
            handedOff ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TenantInviteComplete_OverAHandedOffSession_EndsIt(bool handedOff)
    {
        var ownerId = await SeedEnrollingSubjectAsync("founder");
        StubRegistrationChallengeMintedFor(ownerId, PasskeyRegistrationFlow.CreationLink);
        var invites = StubCreationLink(ownerId);
        StubSignInFor(ownerId);
        var refreshTokens = new Mock<IRefreshTokenService>();
        var handedOffHolder = Guid.CreateVersion7();
        refreshTokens.Setup(r => r.FindOwnerAsync("presented"))
            .ReturnsAsync(new RefreshTokenSession(handedOffHolder, "previous-session", handedOff));
        var audit = new Mock<IAuthAuditService>();
        _controller.HttpContext.RequestServices = new ServiceCollection()
            .AddSingleton(refreshTokens.Object).AddSingleton(audit.Object).BuildServiceProvider();
        _controller.HttpContext.Request.Headers.Cookie = ".Nocturne.RefreshToken=presented";

        (await TenantInviteCompleteAsync(invites)).Should().BeOfType<OkObjectResult>();

        refreshTokens.Verify(
            r => r.RevokeSessionForSubjectAsync(handedOffHolder, "previous-session", It.IsAny<string>()),
            handedOff ? Times.Once() : Times.Never());
    }

    private static readonly string[] InviteSteps = ["accept", "session", "last-login", "login row", "codes"];

    public static TheoryData<string> InviteFailurePoints() => new(InviteSteps);

    /// <summary>
    /// Each step after the proof that can fail, and what it leaves. Past the acceptance the invitee
    /// holds a passkey and a membership, so they sign in on the login page.
    /// </summary>
    [Theory]
    [MemberData(nameof(InviteFailurePoints))]
    public async Task InviteComplete_WhenAStepAfterTheProofFails_SetsNoCookieAndLeavesWhatTheStepsBeforeItWrote(string failing)
    {
        var rows = RecordRows(_auditService);
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(inviteeId, PasskeyRegistrationFlow.MemberInvite);
        StubSignInFor(inviteeId);
        var down = new DbUpdateException("down");
        switch (failing)
        {
            case "accept":
                inviteService.Setup(s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
                    .ThrowsAsync(down);
                break;
            case "codes":
                _recoveryCodeService.Setup(s => s.GenerateCodesAsync(inviteeId)).ThrowsAsync(down);
                break;
            case "session":
                _sessionService
                    .Setup(s => s.IssueSessionAsync(inviteeId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(down);
                break;
            case "last-login":
                _subjectService.Setup(s => s.UpdateLastLoginAsync(inviteeId)).ThrowsAsync(down);
                break;
            case "login row":
                _auditService.Setup(a => a.LogAsync(
                        AuthAuditEventType.Login, It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                        It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()))
                    .ThrowsAsync(down);
                break;
        }

        var complete = () => InviteCompleteAsync("invitee", inviteService);

        await complete.Should().ThrowAsync<DbUpdateException>();
        SetCookies().Should().BeEmpty();
        var reached = Array.IndexOf(InviteSteps, failing);
        rows.Should().HaveCount(reached >= 4 ? 1 : 0, "only a failure after the sign-in leaves its login row");
        (await HoldsPasskeyAsync(inviteeId)).Should().BeTrue();
        (await IsMemberAsync(inviteeId, _tenantId)).Should().Be(failing != "accept");
        _sessionService.Verify(
            s => s.IssueSessionAsync(inviteeId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            reached >= 1 ? Times.Once() : Times.Never());
        _subjectService.Verify(s => s.UpdateLastLoginAsync(inviteeId), reached >= 2 ? Times.Once() : Times.Never());
        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(inviteeId), reached >= 4 ? Times.Once() : Times.Never());
    }

    private static readonly string[] CreationLinkSteps = ["session", "last-login", "login row", "codes"];

    public static TheoryData<string> CreationLinkFailurePoints() => new(CreationLinkSteps);

    /// <summary>
    /// Each step after the tenant is created that can fail, and what it leaves. The owner holds a
    /// passkey and the new tenant's owner membership, so they sign in on that tenant's login page.
    /// </summary>
    [Theory]
    [MemberData(nameof(CreationLinkFailurePoints))]
    public async Task TenantInviteComplete_WhenAStepAfterTheTenantFails_SetsNoCookieAndLeavesAnOwnerWhoSignsInWithThePasskey(string failing)
    {
        var rows = RecordRows(_auditService);
        var ownerId = await SeedEnrollingSubjectAsync("founder");
        StubRegistrationChallengeMintedFor(ownerId, PasskeyRegistrationFlow.CreationLink);
        var tenantId = Guid.CreateVersion7();
        var invites = StubCreationLink(ownerId, () => TenantCreatedForAsync(ownerId, tenantId));
        StubSignInFor(ownerId);
        var down = new DbUpdateException("down");
        switch (failing)
        {
            case "codes":
                _recoveryCodeService.Setup(s => s.GenerateCodesAsync(ownerId)).ThrowsAsync(down);
                break;
            case "session":
                _sessionService
                    .Setup(s => s.IssueSessionAsync(ownerId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(down);
                break;
            case "last-login":
                _subjectService.Setup(s => s.UpdateLastLoginAsync(ownerId)).ThrowsAsync(down);
                break;
            case "login row":
                _auditService.Setup(a => a.LogAsync(
                        AuthAuditEventType.Login, It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                        It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()))
                    .ThrowsAsync(down);
                break;
        }

        var complete = () => TenantInviteCompleteAsync(invites);

        await complete.Should().ThrowAsync<DbUpdateException>();
        SetCookies().Should().BeEmpty();
        var reached = Array.IndexOf(CreationLinkSteps, failing);
        rows.Should().HaveCount(reached >= 3 ? 1 : 0, "only a failure after the sign-in leaves its login row");
        (await HoldsPasskeyAsync(ownerId) && await IsMemberAsync(ownerId, tenantId)).Should().BeTrue();
        invites.Verify(s => s.ReleaseAsync(It.IsAny<Guid>()), Times.Never, "the tenant was made, so the link stays used");
        _sessionService.Verify(
            s => s.IssueSessionAsync(ownerId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once());
        _subjectService.Verify(s => s.UpdateLastLoginAsync(ownerId), reached >= 1 ? Times.Once() : Times.Never());
        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(ownerId), reached >= 3 ? Times.Once() : Times.Never());
    }

    #endregion

    #region Passkey login finishes through the sign-in completion

    private sealed record AuditRow(
        string Type, Guid? Subject, bool Success, string? Ip, string? Agent, string? Error, string? Details);

    private const string ClientIp = "203.0.113.7";
    private const string ClientAgent = "agent/1";

    /// <summary>Records every row written through <paramref name="audit"/>, in order, and names each in <paramref name="steps"/>.</summary>
    private static List<AuditRow> RecordRows(Mock<IAuthAuditService> audit, List<string>? steps = null)
    {
        var rows = new List<AuditRow>();
        audit.Setup(a => a.LogAsync(
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()))
            .Callback((string type, Guid? subject, bool success, string? ip, string? agent, string? error, string? details,
                Guid? _, AuthAuditActor? _, Guid? _) =>
            {
                rows.Add(new AuditRow(type, subject, success, ip, agent, error, details));
                steps?.Add($"audit:{type}");
            })
            .Returns(Task.CompletedTask);
        return rows;
    }

    private void FromTheTestClient()
    {
        _controller.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ClientIp);
        _controller.HttpContext.Request.Headers.UserAgent = ClientAgent;
    }

    private void StubSession(Guid subjectId, List<string>? steps = null) =>
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => steps?.Add("session"))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, subjectId));

    /// <summary>The cookie settings the fixture builds the controller with.</summary>
    private static OidcOptions FixtureCookieOptions() => new()
    {
        Cookie = new CookieSettings
        {
            AccessTokenName = ".Nocturne.AccessToken",
            RefreshTokenName = ".Nocturne.RefreshToken",
            Secure = true,
        },
    };

    public static TheoryData<Exception> RefusedAssertions() => new()
    {
        new InvalidOperationException("Credential not found."),
        new InvalidOperationException("Challenge token has already been presented. Please restart the authentication flow."),
        new Fido2NetLib.Fido2VerificationException("Invalid signature"),
        new System.Text.Json.JsonException("not an assertion"),
    };

    [Theory]
    [MemberData(nameof(RefusedAssertions))]
    public async Task LoginComplete_WhenTheAssertionIsRefused_AnswersBadRequestWithTheRowItAlwaysHas(Exception refusal)
    {
        FromTheTestClient();
        var rows = RecordRows(_auditService);
        _passkeyService
            .Setup(s => s.CompleteAssertionAsync("{}", "assertion-token", _tenantId))
            .ThrowsAsync(refusal);

        var result = await LoginCompleteAsync();

        var problem = Assert.IsType<ObjectResult>(result.Result);
        problem.StatusCode.Should().Be(400);
        Assert.IsType<ProblemDetails>(problem.Value).Detail.Should().Be("Passkey authentication failed");
        rows.Should().Equal(new AuditRow(
            AuthAuditEventType.FailedAuth, null, false, ClientIp, ClientAgent, refusal.Message, "{\"method\":\"passkey\"}"));
        _totpService.Verify(s => s.GetSecondFactorRequirementAsync(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
        _controller.HttpContext.Response.Headers.SetCookie.Should().BeEmpty();
    }

    /// <summary>
    /// Only a refused assertion is answered as one. A proof that cannot be checked, here because a
    /// read timed out, is an error and records no failed sign-in.
    /// </summary>
    [Fact]
    public async Task LoginComplete_WhenTheProofFailsForAnotherReason_IsAnErrorAndNotARefusal()
    {
        var rows = RecordRows(_auditService);
        _passkeyService
            .Setup(s => s.CompleteAssertionAsync("{}", "assertion-token", _tenantId))
            .ThrowsAsync(new TimeoutException("the credential read timed out"));

        var act = () => LoginCompleteAsync();

        await act.Should().ThrowAsync<TimeoutException>();
        rows.Should().BeEmpty();
    }

    /// <summary>
    /// The assertion checks membership before the signature. A membership that ends after that
    /// check is refused by the sign-in completion with the answer and the row the assertion gives a
    /// non-member, and no second row.
    /// </summary>
    [Fact]
    public async Task LoginComplete_WhenTheMembershipEndsAfterTheAssertion_IsRefusedAsTheAssertionRefusesANonMember()
    {
        FromTheTestClient();
        var rows = RecordRows(_auditService);
        var formerMember = Guid.CreateVersion7();
        StubSuccessfulAssertion(formerMember, "rhys");
        _totpService
            .Setup(s => s.GetSecondFactorRequirementAsync(formerMember, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);

        var result = await LoginCompleteAsync();

        var problem = Assert.IsType<ObjectResult>(result.Result);
        problem.StatusCode.Should().Be(400);
        Assert.IsType<ProblemDetails>(problem.Value).Detail.Should().Be("Passkey authentication failed");
        rows.Should().Equal(new AuditRow(
            AuthAuditEventType.FailedAuth, null, false, ClientIp, ClientAgent,
            "Credential owner is not a member of this tenant.", "{\"method\":\"passkey\"}"));
        _totpService.Verify(s => s.CreateStepUpTokenAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>()), Times.Never);
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _controller.HttpContext.Response.Headers.SetCookie.Should().BeEmpty();
    }

    /// <summary>
    /// On a host that resolves no tenant the request's tenant id is empty. The assertion refuses
    /// there, and the sign-in completion admits no one either, a member of another tenant included.
    /// </summary>
    [Fact]
    public async Task LoginComplete_OnAHostWithNoTenant_AdmitsNoOne()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        _tenantAccessor.Setup(a => a.TenantId).Returns(Guid.Empty);
        _passkeyService
            .Setup(s => s.CompleteAssertionAsync("{}", "assertion-token", Guid.Empty))
            .ReturnsAsync(new PasskeyAssertionResult(subjectId, "rhys", "rhys"));
        StubSession(subjectId);

        var result = await LoginCompleteAsync();

        Assert.IsType<ObjectResult>(result.Result).StatusCode.Should().Be(400);
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public static TheoryData<SecondFactorRequirement, bool, string> SignInRows() => new()
    {
        { SecondFactorRequirement.NotEnrolled, true, "{\"method\":\"passkey\"}" },
        { SecondFactorRequirement.SatisfiedByTrustedDevice, true, "{\"method\":\"passkey\",\"secondFactor\":\"trusted_device\"}" },
        { SecondFactorRequirement.CodeRequired, false, "{\"method\":\"passkey\",\"secondFactorPending\":true}" },
    };

    [Theory]
    [MemberData(nameof(SignInRows))]
    public async Task LoginComplete_RecordsTheSameLoginRowAsBefore(
        SecondFactorRequirement gate, bool success, string details)
    {
        FromTheTestClient();
        var rows = RecordRows(_auditService);
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        StubSession(subjectId);
        _totpService
            .Setup(s => s.GetSecondFactorRequirementAsync(subjectId, It.IsAny<string?>()))
            .ReturnsAsync(gate);
        _totpService.Setup(s => s.CreateStepUpTokenAsync(subjectId, _tenantId, false)).ReturnsAsync("step-up-token");

        await LoginCompleteAsync();

        rows.Should().Equal(new AuditRow(AuthAuditEventType.Login, subjectId, success, ClientIp, ClientAgent, null, details));
        _sessionService.Verify(s => s.IssueSessionAsync(
                subjectId, new SessionContext(null, "Passkey", ClientIp, ClientAgent, null),
                It.IsAny<CancellationToken>()),
            success ? Times.Once() : Times.Never());
    }

    /// <summary>
    /// The session, then the sign-in time, then the <c>Login</c> row; the handed-off session the
    /// sign-in replaces is ended after them, so its <c>Logout</c> row follows the <c>Login</c> row.
    /// </summary>
    [Fact]
    public async Task LoginComplete_OverAHandedOffSession_RecordsTheSignInBeforeEndingTheReplacedSession()
    {
        var steps = new List<string>();
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        StubSession(subjectId, steps);
        _subjectService.Setup(s => s.UpdateLastLoginAsync(subjectId))
            .Callback(() => steps.Add("last-login")).Returns(Task.CompletedTask);
        RecordRows(_auditService, steps);
        var refreshTokens = new Mock<IRefreshTokenService>();
        refreshTokens.Setup(r => r.FindOwnerAsync("presented"))
            .ReturnsAsync(new RefreshTokenSession(subjectId, "previous-session", true));
        refreshTokens.Setup(r => r.RevokeSessionForSubjectAsync(subjectId, "previous-session", It.IsAny<string>()))
            .Callback(() => steps.Add("end-handed-off"));
        _controller.HttpContext.RequestServices = new ServiceCollection()
            .AddSingleton(refreshTokens.Object).AddSingleton(_auditService.Object).BuildServiceProvider();
        _controller.HttpContext.Request.Headers.Cookie = ".Nocturne.RefreshToken=presented";

        await LoginCompleteAsync();

        steps.Should().Equal(
            "session", "last-login", $"audit:{AuthAuditEventType.Login}",
            "end-handed-off", $"audit:{AuthAuditEventType.Logout}");
    }

    /// <summary>
    /// A failed sign-in time write is an error after the session row is stored: no cookie carries
    /// that session, no row records the sign-in or a failure, and the handed-off session stays live.
    /// </summary>
    [Fact]
    public async Task LoginComplete_WhenTheSignInTimeCannotBeRecorded_IsAnErrorThatSetsNoCookieAndEndsNothing()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        StubSession(subjectId);
        var rows = RecordRows(_auditService);
        _subjectService.Setup(s => s.UpdateLastLoginAsync(subjectId))
            .ThrowsAsync(new DbUpdateException("the update failed"));
        var refreshTokens = new Mock<IRefreshTokenService>();
        _controller.HttpContext.RequestServices = new ServiceCollection()
            .AddSingleton(refreshTokens.Object).AddSingleton(_auditService.Object).BuildServiceProvider();
        _controller.HttpContext.Request.Headers.Cookie = ".Nocturne.RefreshToken=presented";

        var act = () => LoginCompleteAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
        _sessionService.Verify(
            s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once, "the refresh token it stored is never delivered");
        _controller.HttpContext.Response.Headers.SetCookie.Should().BeEmpty();
        rows.Should().BeEmpty();
        refreshTokens.Verify(r => r.FindOwnerAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Ending the handed-off session runs after the <c>Login</c> row, so when it fails the sign-in
    /// time and the row stand behind the error, and the handed-off session stays live.
    /// </summary>
    [Fact]
    public async Task LoginComplete_WhenEndingTheHandedOffSessionFails_LeavesTheSignInRecordedBehindTheError()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        StubSession(subjectId);
        var rows = RecordRows(_auditService);
        var refreshTokens = new Mock<IRefreshTokenService>();
        refreshTokens.Setup(r => r.FindOwnerAsync("presented"))
            .ThrowsAsync(new TimeoutException("the token read timed out"));
        _controller.HttpContext.RequestServices = new ServiceCollection()
            .AddSingleton(refreshTokens.Object).AddSingleton(_auditService.Object).BuildServiceProvider();
        _controller.HttpContext.Request.Headers.Cookie = ".Nocturne.RefreshToken=presented";

        var act = () => LoginCompleteAsync();

        await act.Should().ThrowAsync<TimeoutException>();
        _subjectService.Verify(s => s.UpdateLastLoginAsync(subjectId), Times.Once);
        rows.Select(r => (r.Type, r.Success)).Should().Equal((AuthAuditEventType.Login, true));
        refreshTokens.Verify(
            r => r.RevokeSessionForSubjectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// The pending row is written before the token is minted, as before, so a failed mint leaves a
    /// pending row with no token. It is now an error with no cookie and no failure row.
    /// </summary>
    [Fact]
    public async Task LoginComplete_WhenTheStepUpTokenCannotBeMinted_LeavesThePendingRowAndSetsNoCookie()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        var rows = RecordRows(_auditService);
        _totpService
            .Setup(s => s.GetSecondFactorRequirementAsync(subjectId, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        _totpService.Setup(s => s.CreateStepUpTokenAsync(subjectId, _tenantId, false))
            .ThrowsAsync(new DbUpdateException("the insert failed"));

        var act = () => LoginCompleteAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
        rows.Select(r => (r.Type, r.Success, r.Details)).Should().Equal(
            (AuthAuditEventType.Login, false, "{\"method\":\"passkey\",\"secondFactorPending\":true}"));
        _controller.HttpContext.Response.Headers.SetCookie.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginComplete_WritesTheSameSignInHintAsTheDirectWriter(bool codeOwed)
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        StubSuccessfulAssertion(subjectId, "rhys");
        StubSession(subjectId);
        _totpService
            .Setup(s => s.GetSecondFactorRequirementAsync(subjectId, It.IsAny<string?>()))
            .ReturnsAsync(codeOwed ? SecondFactorRequirement.CodeRequired : SecondFactorRequirement.NotEnrolled);
        _totpService.Setup(s => s.CreateStepUpTokenAsync(subjectId, _tenantId, false)).ReturnsAsync("step-up-token");

        await LoginCompleteAsync();

        var direct = new DefaultHttpContext().Response;
        if (!codeOwed)
            direct.SetSessionCookies(new SessionTokenPair("access", "refresh", 900, subjectId), FixtureCookieOptions());
        direct.SetLastSignInCookie(SessionCookieExtensions.SignInMethods.Passkey, providerId: null, FixtureCookieOptions());

        var written = _controller.HttpContext.Response.Headers.SetCookie.ToArray();
        var expected = direct.Headers.SetCookie.ToArray();
        written.Select(CookieName).Should().Equal(expected.Select(CookieName));
        var writtenHint = Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(written.Last());
        var expectedHint = Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(expected.Last());
        writtenHint.Expires.Should().BeCloseTo(expectedHint.Expires!.Value, TimeSpan.FromSeconds(1),
            "the expiry is read from the clock at each write and formatted to the second");
        WithoutExpiry(written.Last()).Should().Be(WithoutExpiry(expected.Last()),
            "the hint is the last cookie either answer writes");
    }

    private static string CookieName(string? setCookie) => setCookie![..setCookie.IndexOf('=')];

    private static string WithoutExpiry(string? setCookie)
    {
        var cookie = Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(setCookie);
        cookie.Expires = null;
        return cookie.ToString();
    }

    #endregion

    [Fact]
    public async Task RecoveryVerify_WithAValidCode_EndsTheTrustOfEveryDevice()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        _recoveryCodeService.Setup(s => s.VerifyAndConsumeAsync(subjectId, "ABCD-1234")).ReturnsAsync(true);

        var result = await _controller.RecoveryVerify(
            new RecoveryVerifyRequest { Username = "rhys", Code = "ABCD-1234" });

        result.Result.Should().BeOfType<OkObjectResult>();
        _trustedDevices.Verify(t => t.RevokeAllAsync(subjectId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecoveryVerify_RecordsAnUnfinishedSignInRatherThanALogin()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        _recoveryCodeService.Setup(s => s.VerifyAndConsumeAsync(subjectId, "ABCD-1234")).ReturnsAsync(true);

        await _controller.RecoveryVerify(new RecoveryVerifyRequest { Username = "rhys", Code = "ABCD-1234" });

        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.Login, subjectId, true,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()), Times.Never,
            "the recovery session enrols a passkey and signs no one in");
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.Login, subjectId, false,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.Is<string?>(d => d != null && d.Contains("\"method\":\"recovery_code\"") && d.Contains("\"passkeyEnrolmentPending\":true")),
            It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task RecoveryVerify_WithARejectedCode_LeavesTrustedDevicesAlone()
    {
        var subjectId = await SeedMemberAsync("rhys", withPasskey: true);
        _recoveryCodeService.Setup(s => s.VerifyAndConsumeAsync(subjectId, "WRONG")).ReturnsAsync(false);

        await _controller.RecoveryVerify(new RecoveryVerifyRequest { Username = "rhys", Code = "WRONG" });

        _trustedDevices.Verify(t => t.RevokeAllAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

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
        // The unknown username still goes through the same fixed-cost verify path.
        _recoveryCodeService.Verify(s => s.VerifyAndConsumeAsync(null, "123456"), Times.Once);
    }

    #region Relying-party host

    [Fact]
    public async Task LoginOptions_WhenTheHostCannotUseTheRpId_RefusesWithTheReason()
    {
        const string detail = "Passkeys on this server are set up for 'localhost' ... BASE_DOMAIN";
        _controller.HttpContext.Request.Host = new HostString("cgm.example.com");
        _passkeyService.Setup(s => s.DescribeRpIdMismatch("cgm.example.com")).Returns(detail);

        var result = await _controller.LoginOptions(new PasskeyLoginOptionsRequest { Username = "someone" });

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(400);
        // Only `detail` reaches the browser, so that is where the operator's instruction has to be.
        Assert.IsType<ProblemDetails>(objectResult.Value).Detail.Should().Be(detail);
        _passkeyService.Verify(
            s => s.GenerateAssertionOptionsAsync(It.IsAny<string>(), It.IsAny<Guid>()),
            Times.Never,
            "issuing options the browser will reject is what produced the opaque error");
    }

    [Fact]
    public async Task LoginOptions_WhenTheHostCanUseTheRpId_IssuesOptions()
    {
        _controller.HttpContext.Request.Host = new HostString("rhys.cgm.example.com");
        // Explicit, not Moq's loose default: the point of the test is that the guard consulted
        // the service about this host and was answered, not that a null fell out of nowhere.
        _passkeyService
            .Setup(s => s.DescribeRpIdMismatch("rhys.cgm.example.com"))
            .Returns((string?)null);
        _passkeyService
            .Setup(s => s.GenerateAssertionOptionsAsync("someone", _tenantId))
            .ReturnsAsync(new PasskeyAssertionOptions("{\"challenge\":\"abc\"}", "token-data"));

        var result = await _controller.LoginOptions(new PasskeyLoginOptionsRequest { Username = "someone" });

        Assert.IsType<OkObjectResult>(result.Result);
        _passkeyService.Verify(s => s.DescribeRpIdMismatch("rhys.cgm.example.com"), Times.Once);
    }

    #endregion

    #region Auth Status Endpoints

    private static IAccountLinkDelivery Delivery(bool configured)
    {
        var delivery = new Mock<IAccountLinkDelivery>();
        delivery.SetupGet(d => d.IsConfigured).Returns(configured);
        return delivery.Object;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetAuthStatus_OffersRecoveryByEmailExactlyWhenADeliveryRouteIsConfigured(bool configured)
    {
        await EnsureTenantAsync(_tenantId);

        var result = await _controller.GetAuthStatus(Delivery(configured));

        ((AuthStatusResponse)Assert.IsType<OkObjectResult>(result).Value!).EmailRecoveryOffered.Should().Be(configured);
    }

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
        var result = await _controller.GetAuthStatus(Delivery(configured: false));

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuthStatusResponse>(okResult.Value);
        response.SetupRequired.Should().BeTrue();
        response.RecoveryMode.Should().BeFalse();
    }

    [Fact]
    public async Task GetAuthStatus_OnlyCredentialIsOnADisabledProvider_ReturnsSetupRequired()
    {
        // Arrange — the identity cannot sign in, so the tenant is not past first-run setup
        await EnsureTenantAsync(_tenantId);

        var providerId = Guid.CreateVersion7();
        _dbContext.OidcProviders.Add(new OidcProviderEntity
        {
            Id = providerId,
            Name = "Disabled provider",
            IssuerUrl = "https://idp.invalid",
            ClientId = "client",
            IsEnabled = false,
        });

        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Locked Out",
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            ProviderId = providerId,
            OidcSubjectId = "ext-1",
            Issuer = "https://idp.invalid",
            LinkedAt = DateTime.UtcNow,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = subjectId,
        });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetAuthStatus(Delivery(configured: false));

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuthStatusResponse>(okResult.Value);
        response.SetupRequired.Should().BeTrue();
    }

    private async Task SeedMemberAsync(string name, bool signsIn, params string[] permissions)
    {
        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity { Id = subjectId, Name = name, IsActive = true });
        if (signsIn)
        {
            _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(), SubjectId = subjectId, CredentialId = subjectId.ToByteArray(), PublicKey = [1],
            });
        }
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = subjectId, DirectPermissions = [.. permissions],
        });
        await _dbContext.SaveChangesAsync();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GetAuthStatus_ReportsRecoveryModeOnlyWhenNoMemberAdministratorCanSignIn(bool administratorSignsIn, bool recoveryMode)
    {
        await EnsureTenantAsync(_tenantId);
        await SeedMemberAsync("Owner", administratorSignsIn, Scope.FullAccess);
        await SeedMemberAsync("Follower", !administratorSignsIn, Scope.GlucoseRead);

        var result = await _controller.GetAuthStatus(Delivery(configured: false));

        var response = Assert.IsType<AuthStatusResponse>(Assert.IsType<OkObjectResult>(result).Value);
        response.SetupRequired.Should().BeFalse();
        response.RecoveryMode.Should().Be(recoveryMode);
    }

    #endregion

    #region ListCredentials reports whether the account has a backup way in

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ListCredentials_CarriesTheSingleSignInMethodVerdict(bool single)
    {
        Authenticate();
        _passkeyService.Setup(s => s.GetCredentialsAsync(_subjectId)).ReturnsAsync([]);
        _subjectService.Setup(s => s.CountPrimaryAuthFactorsAsync(_subjectId)).ReturnsAsync(1);
        _subjectService.Setup(s => s.HasSingleSignInMethodAsync(_subjectId)).ReturnsAsync(single);

        var result = await _controller.ListCredentials();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PasskeyCredentialListResponse>(okResult.Value);
        response.HasSingleSignInMethod.Should().Be(single);
    }

    [Fact]
    public async Task RemoveCredential_WhenLastPrimaryFactor_Returns409WithTheLastFactorCode()
    {
        Authenticate();
        var credentialId = Guid.NewGuid();
        _subjectService.Setup(s => s.TryRemovePasskeyCredentialAsync(_subjectId, credentialId))
            .ReturnsAsync(FactorRemovalResult.LastPrimaryFactor);

        var result = await _controller.RemoveCredential(credentialId);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Detail.Should().NotBeNullOrWhiteSpace();
        problem.Extensions.Should().ContainKey("error").WhoseValue.Should().Be("last_factor");
    }

    [Fact]
    public async Task RemoveCredential_WhenNotFound_Returns404()
    {
        Authenticate();
        var credentialId = Guid.NewGuid();
        _subjectService.Setup(s => s.TryRemovePasskeyCredentialAsync(_subjectId, credentialId))
            .ReturnsAsync(FactorRemovalResult.NotFound);

        var result = await _controller.RemoveCredential(credentialId);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    #endregion
}
