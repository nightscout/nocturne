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

        var jwtService = new Mock<IJwtService>();
        jwtService.Setup(j => j.GenerateRefreshToken()).Returns(() => Guid.NewGuid().ToString("N"));
        _loginCodes = new LoginCodeService(jwtService.Object, new Mock<IAuthAuditService>().Object);
        // Login codes are tenant-scoped, so the session's contexts are on the request's tenant, as
        // the request pipeline hands them out.
        _recoverySession = new RecoverySession(
            new SharedSqliteFactory(_db.Options).AsNocturneFactory(_tenantId), _loginCodes, oidcOptions,
            NullLogger<RecoverySession>.Instance);

        _tenantService = new Mock<ITenantService>();

        _controller = new PasskeyController(
            _passkeyService.Object,
            _totpService.Object,
            _trustedDevices.Object,
            _recoveryCodeService.Object,
            _recoverySession,
            _sessionService.Object,
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
            logger.Object,
            TimeProvider.System);

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

    /// <summary>Puts an authenticated subject on the request, as the auth middleware would.</summary>
    private void Authenticate(Guid? subjectId = null) =>
        _controller.ControllerContext.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = subjectId ?? _subjectId,
            TenantId = _tenantId,
        };

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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ReturnsAsync(new PasskeyCredentialResult(credentialId ?? Guid.CreateVersion7(), subjectId));

    private Task<ActionResult<PasskeyRegisterCompleteResponse>> CompleteRegistrationAsync() =>
        _controller.RegisterComplete(new PasskeyRegisterCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "token",
        });

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
        await GiveRecoverySessionAsync(subjectId);
        StubRegistrationOptions(subjectId, "owner");

        var result = await _controller.RegisterOptions(
            new PasskeyRegisterOptionsRequest { Username = "owner" });

        Assert.IsType<OkObjectResult>(result.Result);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(subjectId, "owner"), Times.Once);
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
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>()),
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), subjectId, It.IsAny<string?>()),
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), subjectId, It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task RegisterComplete_WhenTheAttestationIsRefused_LeavesTheSessionUsable()
    {
        var subjectId = await SeedMemberAsync("owner", withPasskey: true);
        var session = await GiveRecoverySessionAsync(subjectId);
        _passkeyService
            .SetupSequence(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ThrowsAsync(new Fido2NetLib.Fido2VerificationException("bad attestation"))
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ReturnsAsync(new PasskeyCredentialResult(Guid.CreateVersion7(), subjectId));

        // Login codes are tenant-scoped, so this needs contexts on the request's tenant, as the
        // request pipeline hands out.
        var tenantContexts = new SharedSqliteFactory(_db.Options).AsNocturneFactory(_tenantId);
        var controller = new PasskeyController(
            _passkeyService.Object, _totpService.Object, _trustedDevices.Object, _recoveryCodeService.Object, _recoverySession,
            _sessionService.Object, _subjectService.Object, new Mock<IAuthAuditService>().Object,
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ReturnsAsync(new PasskeyCredentialResult(Guid.CreateVersion7(), subjectId));
        var failingContexts = new Mock<INocturneDbContextFactory>();
        failingContexts
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var controller = new PasskeyController(
            _passkeyService.Object, _totpService.Object, _trustedDevices.Object, _recoveryCodeService.Object,
            new RecoverySession(failingContexts.Object, _loginCodes, Options.Create(new OidcOptions()), NullLogger<RecoverySession>.Instance),
            _sessionService.Object, _subjectService.Object, new Mock<IAuthAuditService>().Object,
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
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, inviteeId));

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
        _subjectService.Verify(s => s.UpdateLastLoginAsync(inviteeId), Times.Once);
    }

    [Fact]
    public async Task InviteComplete_WhenTheInviteIsRefused_RemovesThePasskeyAndIssuesNoSession()
    {
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        inviteService.Setup(s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(new AcceptMemberInviteResult(false, "invite_exhausted", "This invite has no uses left."));
        var credentialId = StubRegistrationChallengeMintedFor(inviteeId);

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "invitee",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-invitee",
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
    public async Task TenantInviteComplete_RecordsTheSignInTime()
    {
        var newOwnerId = await SeedEnrollingSubjectAsync("founder");
        StubRegistrationChallengeMintedFor(newOwnerId);
        var inviteId = Guid.CreateVersion7();
        _tenantService.Setup(s => s.ValidateSlugAsync("founder-site", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(true));
        var tenantInvites = new Mock<ITenantCreationInviteService>();
        tenantInvites.Setup(s => s.ClaimAsync("tenant-invite-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<Guid>.Ok(inviteId));
        tenantInvites
            .Setup(s => s.CreateTenantAsync(
                inviteId, "founder-site", "Founder", newOwnerId, "founder", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<TenantCreationInviteRedeemed>.Ok(
                new TenantCreationInviteRedeemed(Guid.CreateVersion7(), "founder-site", "Founder", "https://founder-site.example")));
        _sessionService
            .Setup(s => s.IssueSessionAsync(newOwnerId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, newOwnerId));

        var result = await _controller.TenantInviteComplete(
            new TenantInviteCompleteRequest
            {
                Token = "tenant-invite-token",
                Slug = "founder-site",
                SiteName = "Founder",
                Username = "founder",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-founder",
            },
            tenantInvites.Object);

        result.Should().BeOfType<OkObjectResult>();
        _subjectService.Verify(s => s.UpdateLastLoginAsync(newOwnerId), Times.Once);
    }

    [Fact]
    public async Task InviteComplete_WhenTheSignInTimeCannotBeRecorded_IssuesNoSessionAndSetsNoCookie()
    {
        var inviteeId = await SeedEnrollingSubjectAsync("invitee");
        var inviteService = StubValidInvite();
        StubRegistrationChallengeMintedFor(inviteeId);
        _subjectService.Setup(s => s.UpdateLastLoginAsync(inviteeId))
            .ThrowsAsync(new DbUpdateException("the update failed"));

        var result = await _controller.InviteComplete(
            new InviteCompleteRequest
            {
                Token = "invite-token",
                Username = "invitee",
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-invitee",
            },
            inviteService.Object);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _controller.HttpContext.Response.Headers.SetCookie.Should().BeEmpty();
    }

    [Fact]
    public async Task TenantInviteComplete_WhenTheSignInTimeCannotBeRecorded_IssuesNoSessionAndSetsNoCookie()
    {
        var newOwnerId = await SeedEnrollingSubjectAsync("founder");
        StubRegistrationChallengeMintedFor(newOwnerId);
        var inviteId = Guid.CreateVersion7();
        _tenantService.Setup(s => s.ValidateSlugAsync("founder-site", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(true));
        var tenantInvites = new Mock<ITenantCreationInviteService>();
        tenantInvites.Setup(s => s.ClaimAsync("tenant-invite-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<Guid>.Ok(inviteId));
        tenantInvites
            .Setup(s => s.CreateTenantAsync(
                inviteId, "founder-site", "Founder", newOwnerId, "founder", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantInviteOutcome<TenantCreationInviteRedeemed>.Ok(
                new TenantCreationInviteRedeemed(Guid.CreateVersion7(), "founder-site", "Founder", "https://founder-site.example")));
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
                ChallengeToken = "challenge-for-founder",
            },
            tenantInvites.Object);

        await complete.Should().ThrowAsync<DbUpdateException>();
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _controller.HttpContext.Response.Headers.SetCookie.Should().BeEmpty();
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
            FilingScopes(), _passkeyCredentials.Object);

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
            FilingScopes(), _passkeyCredentials.Object);

        Assert.IsType<OkResult>(result);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), requestorId, It.IsAny<string?>()),
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
        StubRegistrationChallengeMintedFor(requestorId);

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
        var credentialId = StubRegistrationChallengeMintedFor(requestorId);
        var filer = new Mock<IMembershipRequestService>();
        filer.Setup(f => f.CreateRequestAsync(_tenantId, requestorId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateMembershipRequestResult(CreateMembershipRequestOutcome.Refused, "This profile is not accepting membership requests."));

        var result = await CompleteAccessRequestAsync("Sam Smith", filer.Object);

        Assert.IsType<ObjectResult>(result).StatusCode.Should().Be(400);
        _passkeyCredentials.Verify(p => p.RemoveAsync(credentialId, requestorId, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task AccessRequestComplete_WhenFilingThrows_RemovesTheCredential()
    {
        await AllowAccessRequestsAsync();
        var requestorId = await SeedPendingAccessRequestAsync("Sam Smith");
        var credentialId = StubRegistrationChallengeMintedFor(requestorId);
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
        StubRegistrationChallengeMintedFor(requestorId);
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
        StubRegistrationChallengeMintedFor(requestorId);
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
        StubRegistrationChallengeMintedFor(requestorId);
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
        StubRegistrationChallengeMintedFor(requestorId);
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
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, shell));

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
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, newest));

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
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, subjects[0].Id));

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
            FilingScopes(), _passkeyCredentials.Object);

        Assert.IsType<OkResult>(result);
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
            _subjectService.Object,
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
    /// <returns>The id of the credential the stub reports stored.</returns>
    private Guid StubRegistrationChallengeMintedFor(Guid subjectId)
    {
        var credentialId = Guid.CreateVersion7();
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .ReturnsAsync((string _, string _, Guid _, Guid expectedSubjectId, string? _) =>
                expectedSubjectId == subjectId
                    ? new PasskeyCredentialResult(credentialId, subjectId)
                    : throw new InvalidOperationException(
                        "Registration challenge was not issued for the enrolling subject."));
        return credentialId;
    }

    private Mock<IMemberInviteService> StubValidInvite()
    {
        var inviteService = new Mock<IMemberInviteService>();
        inviteService.Setup(s => s.GetInviteByTokenAsync("invite-token", _tenantId))
            .ReturnsAsync(new MemberInviteInfo(
                Guid.CreateVersion7(), _tenantId, "Test", "Owner", [], null, null, false,
                DateTime.UtcNow.AddDays(1), null, 0, true, false, false, DateTime.UtcNow, [], [], [], null));
        inviteService.Setup(s => s.AcceptInviteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(new AcceptMemberInviteResult(true, MembershipId: Guid.CreateVersion7()));
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
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));
        Assert.IsType<OkObjectResult>((await _controller.AccessRequestOptions(
            new AccessRequestOptionsRequest { DisplayName = displayName, Message = message })).Result);
        var requestorId = (await _dbContext.Subjects.AsNoTracking().SingleAsync(s => s.Name == displayName)).Id;
        StubRegistrationChallengeMintedFor(requestorId);
        Assert.IsType<OkResult>(await CompleteAccessRequestAsync(displayName));
        return requestorId;
    }

    private Task<ActionResult> CompleteAccessRequestAsync(string displayName, IMembershipRequestService? filer = null) =>
        _controller.AccessRequestComplete(
            new AccessRequestCompleteRequest
            {
                DisplayName = displayName,
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge",
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
            new TenantRoleService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System, Moq.Mock.Of<Nocturne.Core.Contracts.Identity.IMemberInviteRepository>(), new Nocturne.Infrastructure.Data.Repositories.TeamRepository(contexts)),
            _notifications.Object,
            _requestAudit.Object,
            TimeProvider.System,
            NullLogger<MembershipRequestService>.Instance, AccountLinkRoutes.Off,
            new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))));
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
