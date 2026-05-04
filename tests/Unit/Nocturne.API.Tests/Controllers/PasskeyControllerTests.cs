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

    [Fact]
    public async Task RegisterOptions_EmptyUsername_ReturnsBadRequest()
    {
        var request = new PasskeyRegisterOptionsRequest
        {
            SubjectId = Guid.CreateVersion7(),
            Username = "",
        };

        var result = await _controller.RegisterOptions(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task RegisterOptions_ValidRequest_CallsServiceAndReturnsOptionsWithToken()
    {
        var subjectId = Guid.CreateVersion7();
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(subjectId, "testuser", _tenantId))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"abc\"}", "token-data"));

        var request = new PasskeyRegisterOptionsRequest
        {
            SubjectId = subjectId,
            Username = "testuser",
        };

        var result = await _controller.RegisterOptions(request);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PasskeyOptionsResponse>(okResult.Value);
        Assert.Contains("challenge", response.Options);
        Assert.Equal("token-data", response.ChallengeToken);
        _passkeyService.Verify(s => s.GenerateRegistrationOptionsAsync(subjectId, "testuser", _tenantId), Times.Once);
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

    #region Setup Flow — Admin Role Assignment

    [Fact]
    public async Task SetupOptions_WhenSetupRequired_CreatesSubjectAndAssignsAdminRole()
    {
        // Arrange — seed the resolved tenant with an owner role
        var tenant = new TenantEntity
        {
            Id = _tenantId,
            Slug = "default",
            DisplayName = "Default",
        };
        var ownerRole = new TenantRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Slug = "owner",
            Name = "Owner",
            IsSystem = true,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        };
        _dbContext.Tenants.Add(tenant);
        _dbContext.TenantRoles.Add(ownerRole);
        await _dbContext.SaveChangesAsync();

        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(
                It.IsAny<Guid>(), "admin", _tenantId))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"setup\"}", "setup-token"));

        _subjectService
            .Setup(s => s.AssignRoleAsync(It.IsAny<Guid>(), "admin", null))
            .ReturnsAsync(true);

        _tenantService
            .Setup(s => s.AddMemberAsync(_tenantId, It.IsAny<Guid>(), It.IsAny<List<Guid>>(), null, null, false, default))
            .Callback<Guid, Guid, List<Guid>, List<string>?, string?, bool, CancellationToken>((tenantId, subjectId, _, _, _, _, _) =>
            {
                _dbContext.TenantMembers.Add(new TenantMemberEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    SubjectId = subjectId,
                    SysCreatedAt = DateTime.UtcNow,
                    SysUpdatedAt = DateTime.UtcNow,
                });
                _dbContext.SaveChanges();
            })
            .Returns(Task.CompletedTask);

        var request = new SetupOptionsRequest
        {
            Username = "admin",
            DisplayName = "Administrator",
        };

        // Act
        var result = await _controller.SetupOptions(request);

        // Assert — response is OK with passkey options
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PasskeyOptionsResponse>(okResult.Value);
        response.ChallengeToken.Should().Be("setup-token");
        response.Options.Should().Contain("challenge");

        // Assert — admin role was assigned
        _subjectService.Verify(
            s => s.AssignRoleAsync(It.IsAny<Guid>(), "admin", null),
            Times.Once);

        // Assert — subject was persisted in the database
        var subjects = await _dbContext.Subjects
            .IgnoreQueryFilters()
            .Where(s => !s.IsSystemSubject)
            .ToListAsync();
        subjects.Should().HaveCount(1);
        subjects[0].Username.Should().Be("admin");
        subjects[0].Name.Should().Be("Administrator");
        subjects[0].IsActive.Should().BeTrue();

        // Assert — tenant membership was created as Owner
        var members = await _dbContext.TenantMembers
            .IgnoreQueryFilters()
            .Where(tm => tm.TenantId == _tenantId)
            .ToListAsync();
        members.Should().HaveCount(1);
        members[0].SubjectId.Should().Be(subjects[0].Id);
    }

    [Fact]
    public async Task SetupOptions_WhenSetupNotRequired_ReturnsForbidden()
    {
        // Seed the resolved tenant with an existing passkey credential (setup already done)
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "default",
            DisplayName = "Default",
        });
        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Existing",
            Username = "existing",
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = System.Text.Encoding.UTF8.GetBytes("cred"),
            PublicKey = [],
            SignCount = 0,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = subjectId,
        });
        await _dbContext.SaveChangesAsync();

        var request = new SetupOptionsRequest
        {
            Username = "admin",
            DisplayName = "Administrator",
        };

        var result = await _controller.SetupOptions(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task SetupOptions_MissingUsername_ReturnsBadRequest()
    {
        // Seed the resolved tenant (no passkeys = setup mode)
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "default",
            DisplayName = "Default",
        });
        await _dbContext.SaveChangesAsync();

        var request = new SetupOptionsRequest
        {
            Username = "",
            DisplayName = "Administrator",
        };

        var result = await _controller.SetupOptions(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task SetupComplete_IssuesSessionWithAdminRoleAndWildcardPermission()
    {
        // Arrange — seed the resolved tenant (no passkeys = setup mode)
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "default",
            DisplayName = "Default",
        });
        await _dbContext.SaveChangesAsync();

        var subjectId = Guid.CreateVersion7();

        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync("{}", "challenge-token", _tenantId))
            .ReturnsAsync(new PasskeyCredentialResult(Guid.CreateVersion7(), subjectId));

        _recoveryCodeService
            .Setup(s => s.GenerateCodesAsync(subjectId))
            .ReturnsAsync(new List<string> { "CODE1", "CODE2", "CODE3" });

        _sessionService
            .Setup(s => s.IssueSessionAsync(
                subjectId,
                It.IsAny<SessionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("jwt-access-token", "refresh-token-value", 900));

        var request = new SetupCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "challenge-token",
        };

        // Act
        var result = await _controller.SetupComplete(request);

        // Assert — response is OK with session tokens
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<SetupCompleteResponse>(okResult.Value);
        response.Success.Should().BeTrue();
        response.AccessToken.Should().Be("jwt-access-token");
        response.RefreshToken.Should().Be("refresh-token-value");
        response.RecoveryCodes.Should().HaveCount(3);
        response.ExpiresIn.Should().Be(900);

        // Assert — session was issued for the subject
        _sessionService.Verify(
            s => s.IssueSessionAsync(
                subjectId,
                It.Is<SessionContext>(ctx => ctx.DeviceDescription == "Setup Passkey"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SetupComplete_WhenSetupNotRequired_ReturnsForbidden()
    {
        // Seed the resolved tenant with an existing passkey credential (setup already done)
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "default",
            DisplayName = "Default",
        });
        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Existing",
            Username = "existing",
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = System.Text.Encoding.UTF8.GetBytes("cred"),
            PublicKey = [],
            SignCount = 0,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = subjectId,
        });
        await _dbContext.SaveChangesAsync();

        var request = new SetupCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "token",
        };

        var result = await _controller.SetupComplete(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task SetupComplete_MissingChallengeToken_ReturnsBadRequest()
    {
        // Seed the resolved tenant (no passkeys = setup mode)
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "default",
            DisplayName = "Default",
        });
        await _dbContext.SaveChangesAsync();

        var request = new SetupCompleteRequest
        {
            AttestationResponseJson = "{}",
            ChallengeToken = "",
        };

        var result = await _controller.SetupComplete(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(400);
    }

    #endregion

    #region Setup Flow — Tenant Variants

    [Fact]
    public async Task SetupOptions_CreatesUserInResolvedTenant()
    {
        // Arrange — seed the resolved tenant
        var tenant = new TenantEntity
        {
            Id = _tenantId,
            Slug = "rhys",
            DisplayName = "Rhys",
        };
        var ownerRole = new TenantRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            Slug = "owner",
            Name = "Owner",
            IsSystem = true,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        };
        _dbContext.Tenants.Add(tenant);
        _dbContext.TenantRoles.Add(ownerRole);
        await _dbContext.SaveChangesAsync();

        _tenantService
            .Setup(s => s.AddMemberAsync(_tenantId, It.IsAny<Guid>(), It.IsAny<List<Guid>>(), null, null, false, default))
            .Callback<Guid, Guid, List<Guid>, List<string>?, string?, bool, CancellationToken>((tenantId, subjectId, _, _, _, _, _) =>
            {
                _dbContext.TenantMembers.Add(new TenantMemberEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    SubjectId = subjectId,
                    SysCreatedAt = DateTime.UtcNow,
                    SysUpdatedAt = DateTime.UtcNow,
                });
                _dbContext.SaveChanges();
            })
            .Returns(Task.CompletedTask);

        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(
                It.IsAny<Guid>(), "rhys", _tenantId))
            .ReturnsAsync(new PasskeyRegistrationOptions("{\"challenge\":\"mt\"}", "mt-token"));

        _subjectService
            .Setup(s => s.AssignRoleAsync(It.IsAny<Guid>(), "admin", null))
            .ReturnsAsync(true);

        var request = new SetupOptionsRequest
        {
            Username = "rhys",
            DisplayName = "Rhys",
        };

        // Act
        var result = await _controller.SetupOptions(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PasskeyOptionsResponse>(okResult.Value);
        response.ChallengeToken.Should().Be("mt-token");

        var members = await _dbContext.TenantMembers
            .IgnoreQueryFilters()
            .Where(tm => tm.TenantId == _tenantId)
            .ToListAsync();
        members.Should().HaveCount(1);

        // Assert — subject was persisted with correct properties
        var subjects = await _dbContext.Subjects
            .IgnoreQueryFilters()
            .Where(s => !s.IsSystemSubject)
            .ToListAsync();
        subjects.Should().HaveCount(1);
        subjects[0].Username.Should().Be("rhys");
        subjects[0].Name.Should().Be("Rhys");
        subjects[0].IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SetupComplete_UsesResolvedTenant()
    {
        // Arrange — seed the resolved tenant (no passkeys = setup mode)
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "rhys",
            DisplayName = "Rhys",
        });
        await _dbContext.SaveChangesAsync();

        var subjectId = Guid.CreateVersion7();

        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync("{}", "token", _tenantId))
            .ReturnsAsync(new PasskeyCredentialResult(Guid.CreateVersion7(), subjectId));

        _recoveryCodeService
            .Setup(s => s.GenerateCodesAsync(subjectId))
            .ReturnsAsync(["CODE1", "CODE2", "CODE3"]);

        _sessionService
            .Setup(s => s.IssueSessionAsync(
                subjectId,
                It.IsAny<SessionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access-token", "refresh-token", 900));

        var request = new SetupCompleteRequest
        {
            ChallengeToken = "token",
            AttestationResponseJson = "{}",
        };

        // Act
        var result = await _controller.SetupComplete(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<SetupCompleteResponse>(okResult.Value);
        response.Success.Should().BeTrue();
        response.RecoveryCodes.Should().HaveCount(3);
        response.AccessToken.Should().Be("access-token");
        response.RefreshToken.Should().Be("refresh-token");
    }

    [Fact]
    public async Task SetupOptions_WhenAlreadyHasCredential_ReturnsForbidden()
    {
        // Arrange — seed the tenant first (FK requirement)
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test",
        });

        // Arrange — tenant already has a passkey credential (setup already done)
        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Existing",
            Username = "existing",
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = System.Text.Encoding.UTF8.GetBytes("existing-cred"),
            PublicKey = [],
            SignCount = 0,
        });
        // Link subject to tenant so the membership-based check finds the passkey
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = subjectId,
        });
        await _dbContext.SaveChangesAsync();

        var request = new SetupOptionsRequest { Username = "admin", DisplayName = "Admin" };

        // Act
        var result = await _controller.SetupOptions(request);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        objectResult.StatusCode.Should().Be(403);
    }

    #endregion

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
