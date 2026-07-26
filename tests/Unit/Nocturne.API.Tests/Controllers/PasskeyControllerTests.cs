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
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class PasskeyControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<IPasskeyService> _passkeyService;
    private readonly Mock<IRecoveryCodeService> _recoveryCodeService;
    private readonly Mock<IJwtService> _jwtService;
    private readonly Mock<ISessionService> _sessionService;
    private readonly Mock<ISubjectService> _subjectService;
    private readonly Mock<ITenantAccessor> _tenantAccessor;
    private readonly Mock<ITenantService> _tenantService;
    private readonly PasskeyController _controller;

    private readonly Guid _tenantId = Guid.CreateVersion7();

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
            _recoveryCodeService.Object,
            _jwtService.Object,
            _sessionService.Object,
            _subjectService.Object,
            auditService.Object,
            _tenantAccessor.Object,
            _tenantService.Object,
            _dbContext,
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

    /// <summary>Seeds an active subject that is a member of the given tenant (this one by default).</summary>
    private async Task<Guid> SeedMemberAsync(string username, Guid? tenantId = null)
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
        await _dbContext.SaveChangesAsync();
        return subjectId;
    }

    private async Task EnsureTenantAsync(Guid tenantId)
    {
        if (await _dbContext.Set<TenantEntity>().AnyAsync(t => t.Id == tenantId))
            return;

        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = tenantId,
            Slug = "t" + tenantId.ToString("N")[..8],
            DisplayName = "Tenant",
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedPasskeyCredentialAsync(Guid subjectId)
    {
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = Guid.CreateVersion7().ToByteArray(),
            PublicKey = [1, 2, 3],
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedOidcIdentityAsync(Guid subjectId)
    {
        var providerId = Guid.CreateVersion7();
        _dbContext.Set<OidcProviderEntity>().Add(new OidcProviderEntity
        {
            Id = providerId,
            Name = "Keycloak",
            IssuerUrl = "https://issuer.example",
            ClientId = "nocturne",
        });
        _dbContext.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            ProviderId = providerId,
            OidcSubjectId = "ext-" + subjectId,
            Issuer = "https://issuer.example",
        });
        await _dbContext.SaveChangesAsync();
    }

    private void Authenticate(Guid subjectId) =>
        _controller.ControllerContext.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = subjectId,
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
            .Setup(s => s.GenerateRegistrationOptionsAsync(subjectId, username, _tenantId))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

    [Fact]
    public async Task RegisterOptions_EmptyUsername_ReturnsBadRequest()
    {
        var request = new PasskeyRegisterOptionsRequest { Username = "" };

        var result = await _controller.RegisterOptions(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task RegisterOptions_ValidRequest_CallsServiceAndReturnsOptionsWithToken()
    {
        var subjectId = await SeedMemberAsync("testuser");
        StubRegistrationOptions(subjectId, "testuser");

        var result = await _controller.RegisterOptions(
            new PasskeyRegisterOptionsRequest { Username = "testuser" });

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PasskeyOptionsResponse>(okResult.Value);
        Assert.Contains("challenge", response.Options);
        Assert.Equal("token-data", response.ChallengeToken);
        _passkeyService.Verify(s => s.GenerateRegistrationOptionsAsync(subjectId, "testuser", _tenantId), Times.Once);
    }

    // ── Registration subject binding ─────────────────────────────────────

    [Fact]
    public async Task RegisterOptions_WhenAnonymousAndTheAccountHasACredential_IsRefused()
    {
        // The takeover: an anonymous caller naming an established account used to have its
        // subject id honoured, binding their own authenticator to that account.
        var victimId = await SeedMemberAsync("victim");
        await SeedPasskeyCredentialAsync(victimId);

        var result = await _controller.RegisterOptions(
            new PasskeyRegisterOptionsRequest { Username = "victim" });

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterOptions_WhenAnonymousAndTheAccountHasAnOidcIdentity_IsRefused()
    {
        var victimId = await SeedMemberAsync("victim");
        await SeedOidcIdentityAsync(victimId);

        var result = await _controller.RegisterOptions(
            new PasskeyRegisterOptionsRequest { Username = "victim" });

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterOptions_WhenAuthenticated_BindsToTheAuthenticatedSubject()
    {
        // Adding a passkey to your own account. The username on the request is not the
        // authority — the session is — so naming someone else changes nothing.
        var callerId = await SeedMemberAsync("caller");
        await SeedPasskeyCredentialAsync(callerId);
        var victimId = await SeedMemberAsync("victim");
        await SeedPasskeyCredentialAsync(victimId);
        Authenticate(callerId);
        StubRegistrationOptions(callerId, "victim");

        var result = await _controller.RegisterOptions(
            new PasskeyRegisterOptionsRequest { Username = "victim" });

        Assert.IsType<OkObjectResult>(result.Result);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(callerId, "victim", _tenantId), Times.Once);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(victimId, It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RegisterOptions_WithARecoverySession_BindsToTheCookieSubject()
    {
        // Re-registering after spending a recovery code: the account still holds its old
        // credential, so only the recovery session makes this allowed.
        var subjectId = await SeedMemberAsync("owner");
        await SeedPasskeyCredentialAsync(subjectId);
        GiveRecoverySession(subjectId, "passkey:manage");
        StubRegistrationOptions(subjectId, "owner");

        var result = await _controller.RegisterOptions(
            new PasskeyRegisterOptionsRequest { Username = "owner" });

        Assert.IsType<OkObjectResult>(result.Result);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(subjectId, "owner", _tenantId), Times.Once);
    }

    [Fact]
    public async Task RegisterOptions_WithARecoverySessionLackingPasskeyManage_IsRefused()
    {
        var subjectId = await SeedMemberAsync("owner");
        await SeedPasskeyCredentialAsync(subjectId);
        GiveRecoverySession(subjectId, "glucose.read");

        var result = await _controller.RegisterOptions(
            new PasskeyRegisterOptionsRequest { Username = "owner" });

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task RegisterOptions_ForAnotherTenantsMember_IsRefused()
    {
        // Subjects are global; membership is what scopes them. A credentialless subject in
        // another tenant must not be claimable from this one.
        var otherTenantSubjectId = await SeedMemberAsync("elsewhere", tenantId: Guid.CreateVersion7());

        var result = await _controller.RegisterOptions(
            new PasskeyRegisterOptionsRequest { Username = "elsewhere" });

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(otherTenantSubjectId, It.IsAny<string>(), It.IsAny<Guid>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterComplete_NoChallengeToken_ReturnsBadRequest()
    {
        var request = new PasskeyRegisterCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "",
        };

        var result = await _controller.RegisterComplete(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

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
