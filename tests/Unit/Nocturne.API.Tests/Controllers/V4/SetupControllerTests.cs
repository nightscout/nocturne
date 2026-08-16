using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Tests for the setup flow, focusing on the soft-lock scenario where a tenant
/// exists but owner passkey registration was never completed.
/// </summary>
/// <remarks>
/// SQLite has no Row Level Security, so every row is visible here. These tests pin the guard
/// logic and the shape of the per-tenant scan, not the policies that make the scan necessary.
/// </remarks>
public class SetupControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<ITenantService> _tenantService;
    private readonly Mock<IPasskeyService> _passkeyService;
    private readonly Mock<IRecoveryCodeService> _recoveryCodeService;
    private readonly Mock<ISessionService> _sessionService;
    private readonly Mock<ISubjectService> _subjectService;
    private readonly Mock<IOidcAuthService> _oidcAuthService;
    private readonly PlatformOptions _platformOptions;
    private readonly SetupController _controller;

    public SetupControllerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(_dbOptions);
        _dbContext.Database.EnsureCreated();

        _tenantService = new Mock<ITenantService>();
        _passkeyService = new Mock<IPasskeyService>();
        _recoveryCodeService = new Mock<IRecoveryCodeService>();
        _sessionService = new Mock<ISessionService>();
        _subjectService = new Mock<ISubjectService>();
        _oidcAuthService = new Mock<IOidcAuthService>();

        var oidcOptions = Options.Create(new OidcOptions
        {
            Cookie = new CookieSettings
            {
                AccessTokenName = ".Nocturne.AccessToken",
                RefreshTokenName = ".Nocturne.RefreshToken",
                Secure = true,
            },
        });

        var dbFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var ctx = new NocturneDbContext(_dbOptions);
                return ctx;
            });

        // A real instance, not a mock: the platform-admin grant is part of the
        // behaviour under test, and BootstrapAsync is not virtual.
        _platformOptions = new PlatformOptions();
        var platformAdminBootstrap = new PlatformAdminBootstrapService(
            dbFactory.Object,
            Options.Create(_platformOptions),
            NullLogger<PlatformAdminBootstrapService>.Instance);

        _controller = new SetupController(
            _tenantService.Object,
            _passkeyService.Object,
            _recoveryCodeService.Object,
            _sessionService.Object,
            _subjectService.Object,
            dbFactory.Object,
            oidcOptions,
            _oidcAuthService.Object,
            Options.Create(new OperatorConfiguration()),
            new Mock<IHttpClientFactory>().Object,
            platformAdminBootstrap,
            new Mock<ILogger<SetupController>>().Object);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    // ── CreateTenant ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTenant_WhenNoTenantsExist_Succeeds()
    {
        // Arrange
        var tenantId = Guid.CreateVersion7();
        _tenantService.Setup(s => s.ValidateSlugAsync("fresh", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(true));
        _tenantService.Setup(s => s.CreateWithoutOwnerAsync("fresh", "Fresh Instance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantCreatedDto(tenantId, "fresh", "Fresh Instance", true, DateTime.UtcNow));

        // Act
        var result = await _controller.CreateTenant(
            new SetupTenantRequest("fresh", "Fresh Instance"), CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<SetupTenantResponse>().Subject;
        response.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task CreateTenant_WhenTenantAlreadyExists_Returns409()
    {
        // Arrange — seed a configured tenant (member with passkey credential)
        await SeedConfiguredTenantAsync("existing", "Existing Tenant");

        // Act
        var result = await _controller.CreateTenant(
            new SetupTenantRequest("new-slug", "New Instance"), CancellationToken.None);

        // Assert — 409 because a configured tenant exists
        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task CreateTenant_WhenTenantAlreadyExists_WithSameSlug_Returns409()
    {
        // Arrange — configured tenant with a passkey credential
        await SeedConfiguredTenantAsync("my-instance", "My Instance");

        // Act
        var result = await _controller.CreateTenant(
            new SetupTenantRequest("my-instance", "My Instance"), CancellationToken.None);

        // Assert — 409 because a configured tenant exists, not slug uniqueness
        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task CreateTenant_WhenConfiguredTenantExists_Returns409()
    {
        // Arrange — one configured tenant plus an unconfigured one
        await SeedConfiguredTenantAsync("tenant-a", "Tenant A");
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = Guid.CreateVersion7(),
            Slug = "tenant-b",
            DisplayName = "Tenant B",
        });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.CreateTenant(
            new SetupTenantRequest("tenant-c", "Tenant C"), CancellationToken.None);

        // Assert
        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task CreateTenant_WhenTheConfiguredTenantIsNotTheFirstOne_Returns409()
    {
        // The gate asks each tenant in turn, so it must not stop at the first one that answers no.
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = Guid.CreateVersion7(),
            Slug = "unconfigured-first",
            DisplayName = "Unconfigured First",
        });
        await _dbContext.SaveChangesAsync();
        await SeedConfiguredTenantAsync("configured-second", "Configured Second");

        var result = await _controller.CreateTenant(
            new SetupTenantRequest("tenant-c", "Tenant C"), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task CreateTenant_WhenACredentialBelongsToNoTenantsMember_Succeeds()
    {
        // The gate is anchored on membership: a credentialed subject who belongs to no tenant is
        // a half-finished enrolment, not a configured instance.
        var subjectId = Guid.CreateVersion7();
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = Guid.CreateVersion7(), Slug = "ownerless", DisplayName = "Ownerless",
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId, Name = "Stray", Username = "stray", IsActive = true,
        });
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = System.Text.Encoding.UTF8.GetBytes("cred-stray"),
            PublicKey = [],
            SignCount = 0,
        });
        await _dbContext.SaveChangesAsync();

        var newTenantId = Guid.CreateVersion7();
        _tenantService.Setup(s => s.ValidateSlugAsync("retry", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(true));
        _tenantService.Setup(s => s.CreateWithoutOwnerAsync("retry", "Retry", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantCreatedDto(newTenantId, "retry", "Retry", true, DateTime.UtcNow));

        var result = await _controller.CreateTenant(
            new SetupTenantRequest("retry", "Retry"), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<SetupTenantResponse>().Subject.TenantId.Should().Be(newTenantId);
    }

    [Fact]
    public async Task CreateTenant_WithInvalidSlug_Returns400()
    {
        // Arrange — no tenants, but slug validation fails
        _tenantService.Setup(s => s.ValidateSlugAsync("bad!", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(false, "Invalid characters"));

        // Act
        var result = await _controller.CreateTenant(
            new SetupTenantRequest("bad!", "Bad Slug"), CancellationToken.None);

        // Assert
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateTenant_WithEmptySlug_Returns400()
    {
        // Act
        var result = await _controller.CreateTenant(
            new SetupTenantRequest("", "Empty Slug"), CancellationToken.None);

        // Assert
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task CreateTenant_WithEmptyDisplayName_Returns400()
    {
        // Act
        var result = await _controller.CreateTenant(
            new SetupTenantRequest("valid-slug", ""), CancellationToken.None);

        // Assert
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
    }

    // ── OwnerOptions (soft-lock preconditions) ────────────────────────────

    [Fact]
    public async Task OwnerOptions_WhenNoTenantsExist_Returns409()
    {
        // Arrange — no tenants at all (user skipped tenant creation somehow)
        var request = new SetupOwnerOptionsRequest
        {
            Username = "admin",
            DisplayName = "Admin User",
        };

        // Act
        var result = await _controller.OwnerOptions(request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task OwnerOptions_WhenMultipleTenantsExist_Returns409()
    {
        // Arrange
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = Guid.CreateVersion7(), Slug = "a", DisplayName = "A",
        });
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = Guid.CreateVersion7(), Slug = "b", DisplayName = "B",
        });
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.OwnerOptions(
            new SetupOwnerOptionsRequest { Username = "admin", DisplayName = "Admin" },
            CancellationToken.None);

        // Assert
        result.Should().BeOfType<ConflictObjectResult>();
    }

    // Sole-tenant paths run here because PinTenantAsync no-ops off Postgres. What SQLite
    // cannot reproduce is RLS, so tests that depend on rows being filtered by tenant
    // belong in the integration suite.

    // ── Soft-lock scenario: the full sequence ─────────────────────────────

    [Fact]
    public async Task SoftLock_TenantCreatedButOwnerNeverCompleted_CreateTenantAllowsRetry()
    {
        // Soft-lock scenario resolved: the guard now checks for credential-bearing
        // members, not raw tenant count. An ownerless tenant does NOT block setup.
        // 1. User visits /setup, creates a tenant (succeeds)
        // 2. User's browser crashes / they close the tab
        // 3. Tenant exists but has no passkey credentials
        // 4. CreateTenant allows the setup to proceed because no configured tenant exists

        // Step 1: Create the tenant (simulating what happened before the crash)
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = Guid.CreateVersion7(),
            Slug = "my-instance",
            DisplayName = "My Instance",
        });
        await _dbContext.SaveChangesAsync();

        // Step 4: User tries to create a tenant again — succeeds because no credentials exist
        var newTenantId = Guid.CreateVersion7();
        _tenantService.Setup(s => s.ValidateSlugAsync("different-slug", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(true));
        _tenantService.Setup(s => s.CreateWithoutOwnerAsync("different-slug", "Different Instance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantCreatedDto(newTenantId, "different-slug", "Different Instance", true, DateTime.UtcNow));

        var result = await _controller.CreateTenant(
            new SetupTenantRequest("different-slug", "Different Instance"),
            CancellationToken.None);

        // Assert — no longer a soft-lock; setup proceeds
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<SetupTenantResponse>().Subject;
        response.TenantId.Should().Be(newTenantId);
    }

    [Fact]
    public async Task SoftLock_TenantCreatedAndOwnerSubjectCreated_ButPasskeyFailed_CreateTenantAllowsRetry()
    {
        // Soft-lock resolved: tenant AND subject exist (OwnerOptions ran) but the
        // WebAuthn ceremony failed. The member has no passkey credential, so the
        // guard does not consider it a configured tenant — setup can proceed.

        var tenantId = Guid.CreateVersion7();
        var subjectId = Guid.CreateVersion7();

        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = tenantId, Slug = "my-instance", DisplayName = "My Instance",
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId, Name = "Incomplete Owner",
            IsActive = true, IsSystemSubject = false,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubjectId = subjectId,
        });
        await _dbContext.SaveChangesAsync();

        // CreateTenant now succeeds — no longer a soft-lock
        var newTenantId = Guid.CreateVersion7();
        _tenantService.Setup(s => s.ValidateSlugAsync("any-slug", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(true));
        _tenantService.Setup(s => s.CreateWithoutOwnerAsync("any-slug", "Any Name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantCreatedDto(newTenantId, "any-slug", "Any Name", true, DateTime.UtcNow));

        var createResult = await _controller.CreateTenant(
            new SetupTenantRequest("any-slug", "Any Name"), CancellationToken.None);
        var ok = createResult.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<SetupTenantResponse>().Subject.TenantId.Should().Be(newTenantId);
    }

    [Fact]
    public async Task OwnerOptions_WhenSoleTenantHasCredentiallessMember_ProceedsPastGuard()
    {
        // The real-world soft-lock: OwnerOptions previously created a subject +
        // membership, then the WebAuthn ceremony failed/was abandoned so no passkey
        // was stored. Retrying must resume setup, not dead-end on owner_already_exists.
        var tenantId = Guid.CreateVersion7();
        var subjectId = Guid.CreateVersion7();

        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = tenantId, Slug = "my-instance", DisplayName = "My Instance",
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId, Name = "Incomplete Owner", Username = "owner",
            IsActive = true, IsSystemSubject = false,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubjectId = subjectId,
        });
        await _dbContext.SaveChangesAsync();

        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(new PasskeyRegistrationOptions("{}", "challenge-token"));

        // Act
        var result = await _controller.OwnerOptions(
            new SetupOwnerOptionsRequest { Username = "owner", DisplayName = "Owner" },
            CancellationToken.None);

        // Assert — guard passed; registration options were issued
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<SetupOwnerOptionsResponse>().Subject;
        response.ChallengeToken.Should().Be("challenge-token");
        response.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task OwnerOptions_WhenSoleTenantHasCredentialedMember_Returns409OwnerAlreadyExists()
    {
        // The guard must still block once a member holds real credentials — a
        // completed setup should not be re-openable.
        await SeedConfiguredTenantAsync("my-instance", "My Instance");

        // Act
        var result = await _controller.OwnerOptions(
            new SetupOwnerOptionsRequest { Username = "someone", DisplayName = "Someone" },
            CancellationToken.None);

        // Assert
        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().BeEquivalentTo(new { error = "owner_already_exists" });
    }

    // SoftLock_TenantWithOnlySystemMembers_OwnerOptionsSucceeds is an integration test — it
    // asserts what the tenant pin makes reachable, which needs real policies.

    // ── ValidateUsername ──────────────────────────────────────────────────

    [Theory]
    [InlineData("ab")]           // too short
    [InlineData("-bad")]         // leading hyphen
    [InlineData("bad-")]         // trailing hyphen
    [InlineData(".bad")]         // leading dot
    [InlineData("bad.")]         // trailing dot
    [InlineData("has spaces")]   // spaces
    public async Task ValidateUsername_WhenInvalidFormat_ReturnsError(string username)
    {
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = Guid.CreateVersion7(), Slug = "test", DisplayName = "Test",
        });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.ValidateUsername(username, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var validation = ok.Value.Should().BeOfType<SlugValidationResult>().Subject;
        validation.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("system")]
    public async Task ValidateUsername_WhenReserved_ReturnsError(string username)
    {
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = Guid.CreateVersion7(), Slug = "test", DisplayName = "Test",
        });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.ValidateUsername(username, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var validation = ok.Value.Should().BeOfType<SlugValidationResult>().Subject;
        validation.IsValid.Should().BeFalse();
        validation.Message.Should().Contain("reserved");
    }

    [Fact]
    public async Task ValidateUsername_WhenEmpty_ReturnsError()
    {
        var result = await _controller.ValidateUsername("", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var validation = ok.Value.Should().BeOfType<SlugValidationResult>().Subject;
        validation.IsValid.Should().BeFalse();
    }

    // ── OwnerComplete binds the enrolment to a server-resolved subject ────

    [Fact]
    public async Task OwnerComplete_WithAChallengeForAnotherSubject_IsRefused()
    {
        var ownerSubjectId = await SeedOwnerlessTenantWithOwnerSubjectAsync();
        var otherSubjectId = Guid.CreateVersion7();
        StubRegistrationChallengeMintedFor(otherSubjectId);

        var result = await _controller.OwnerComplete(
            new SetupOwnerCompleteRequest
            {
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-another-subject",
            },
            CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), ownerSubjectId, It.IsAny<string?>()),
            Times.Once,
            "the enrolling subject comes from the server's lookup, not from the challenge token");
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a refused enrolment must not produce a session");
    }

    [Fact]
    public async Task OwnerComplete_WithAChallengeForTheResolvedOwner_Succeeds()
    {
        var ownerSubjectId = await SeedOwnerlessTenantWithOwnerSubjectAsync();
        StubRegistrationChallengeMintedFor(ownerSubjectId);
        _recoveryCodeService.Setup(s => s.GenerateCodesAsync(ownerSubjectId))
            .ReturnsAsync(["code-1"]);
        _sessionService
            .Setup(s => s.IssueSessionAsync(ownerSubjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900));

        var result = await _controller.OwnerComplete(
            new SetupOwnerCompleteRequest
            {
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-owner",
            },
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<SetupOwnerCompleteResponse>().Subject.Success.Should().BeTrue();
    }

    [Fact]
    public async Task OwnerComplete_BeforeTheOptionsStepCreatedTheOwner_IsRefused()
    {
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = Guid.CreateVersion7(), Slug = "my-instance", DisplayName = "My Instance",
        });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.OwnerComplete(
            new SetupOwnerCompleteRequest
            {
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-from-nowhere",
            },
            CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Never,
            "with no subject to resolve there is nothing to enrol onto");
    }

    // ── OwnerOidc ────────────────────────────────────────────────────────

    [Fact]
    public async Task OwnerOidc_WhenNoTenantsExist_Returns409()
    {
        var request = new SetupOwnerOidcRequest
        {
            Username = "admin",
            DisplayName = "Admin User",
            ProviderId = Guid.CreateVersion7(),
        };

        var result = await _controller.OwnerOidc(request, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task OwnerOidc_WhenMultipleTenantsExist_Returns409()
    {
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = Guid.CreateVersion7(), Slug = "a", DisplayName = "A",
        });
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = Guid.CreateVersion7(), Slug = "b", DisplayName = "B",
        });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.OwnerOidc(
            new SetupOwnerOidcRequest { Username = "admin", DisplayName = "Admin", ProviderId = Guid.CreateVersion7() },
            CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    // OwnerOidc field-validation cases (empty username, empty ProviderId) are left to
    // the integration suite; the sole-tenant guard itself is exercised above.

    // ── Platform admin bootstrap ─────────────────────────────────────────

    [Fact]
    public async Task OwnerComplete_WhenFirstOwnerRegistersPasskey_GrantsPlatformAdmin()
    {
        // The fresh-install lockout: the startup bootstrap pass ran against an empty
        // database, so the owner created by setup kept is_platform_admin = false and
        // /settings/admin redirected to /settings until the API was restarted.
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubPasskeyCompletion(subjectId);

        var result = await CompleteOwnerSetupAsync();

        result.Should().BeOfType<OkObjectResult>();
        var subject = await FreshContext().Subjects.SingleAsync(s => s.Id == subjectId);
        subject.IsPlatformAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task OwnerOptions_BeforeCeremonyCompletes_DoesNotGrantPlatformAdmin()
    {
        // An abandoned WebAuthn ceremony must not leave a credential-less subject holding
        // the flag: that would suppress every later grant, including the startup pass,
        // and lock the instance out permanently.
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(new PasskeyRegistrationOptions("{}", "challenge-token"));

        await _controller.OwnerOptions(
            new SetupOwnerOptionsRequest { Username = "owner", DisplayName = "Owner" },
            CancellationToken.None);

        var subject = await FreshContext().Subjects.SingleAsync(s => s.Id == subjectId);
        subject.IsPlatformAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task OwnerComplete_WhenPlatformAdminAlreadyExists_DoesNotGrantToOwner()
    {
        // An existing platform admin means this is not a fresh install, so setup must
        // not hand the flag to whoever completes it.
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        await SeedPlatformAdminAsync();
        StubPasskeyCompletion(subjectId);

        await CompleteOwnerSetupAsync();

        var subject = await FreshContext().Subjects.SingleAsync(s => s.Id == subjectId);
        subject.IsPlatformAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task OwnerComplete_WhenAdminSubjectIdsConfigured_LeavesOwnerWithoutPlatformAdmin()
    {
        // Operators who pin Platform:AdminSubjectIds own the decision outright.
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        _platformOptions.AdminSubjectIds.Add(Guid.CreateVersion7());
        StubPasskeyCompletion(subjectId);

        await CompleteOwnerSetupAsync();

        var subject = await FreshContext().Subjects.SingleAsync(s => s.Id == subjectId);
        subject.IsPlatformAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task OidcCallback_WhenFirstOwnerLinksIdentity_GrantsPlatformAdmin()
    {
        // The OIDC setup path completes in the callback, not OwnerOidc, so it needs the
        // same grant — this is the path a self-hoster using Google login takes.
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        SetOidcStateCookieOnRequest("expected-state");
        _oidcAuthService
            .Setup(s => s.HandleSetupCallbackAsync(
                "auth-code", "expected-state", "expected-state",
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(OidcSetupCallbackResult.Succeeded(
                subjectId, new OidcTokenResponse { AccessToken = "at", RefreshToken = "rt", ExpiresIn = 3600 }));

        await _controller.OidcCallback(
            code: "auth-code", state: "expected-state", error: null, error_description: null,
            ct: CancellationToken.None);

        var subject = await FreshContext().Subjects.SingleAsync(s => s.Id == subjectId);
        subject.IsPlatformAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task OidcCallback_WhenCallbackFails_DoesNotGrantPlatformAdmin()
    {
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        SetOidcStateCookieOnRequest("expected-state");
        _oidcAuthService
            .Setup(s => s.HandleSetupCallbackAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(OidcSetupCallbackResult.Failed("invalid_grant"));

        await _controller.OidcCallback(
            code: "auth-code", state: "expected-state", error: null, error_description: null,
            ct: CancellationToken.None);

        var subject = await FreshContext().Subjects.SingleAsync(s => s.Id == subjectId);
        subject.IsPlatformAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task OidcCallback_WhenSetupAlreadyComplete_DoesNotProcessTheCallback()
    {
        // Every sibling setup endpoint dead-ends once an owner holds credentials. Without the
        // same guard here this callback stays live for the life of the instance, and it links
        // an identity and issues a session for whatever subject the state names.
        await SeedConfiguredTenantAsync("established", "Established");
        SetOidcStateCookieOnRequest("expected-state");

        var result = await _controller.OidcCallback(
            code: "auth-code", state: "expected-state", error: null, error_description: null,
            ct: CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Contain("setup_already_complete");
        _oidcAuthService.Verify(
            s => s.HandleSetupCallbackAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task OidcCallback_WhenTheCallbackNamesANonOwnerSubject_DoesNotGrantPlatformAdmin()
    {
        // The grant re-derives eligibility instead of trusting the subject it is handed, so a
        // subject that does not hold the tenant's owner role gets nothing.
        await SeedSoleTenantWithOwnerRoleAsync();
        var bystanderId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = bystanderId, Name = "Bystander", Username = "bystander",
            IsActive = true, IsSystemSubject = false,
        });
        await _dbContext.SaveChangesAsync();

        SetOidcStateCookieOnRequest("expected-state");
        _oidcAuthService
            .Setup(s => s.HandleSetupCallbackAsync(
                "auth-code", "expected-state", "expected-state",
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(OidcSetupCallbackResult.Succeeded(
                bystanderId, new OidcTokenResponse { AccessToken = "at", RefreshToken = "rt", ExpiresIn = 3600 }));

        await _controller.OidcCallback(
            code: "auth-code", state: "expected-state", error: null, error_description: null,
            ct: CancellationToken.None);

        var bystander = await FreshContext().Subjects.SingleAsync(s => s.Id == bystanderId);
        bystander.IsPlatformAdmin.Should().BeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private NocturneDbContext FreshContext() => new(_dbOptions);

    /// <summary>
    /// Drives the passkey completion step with the mocks it needs to reach the grant.
    /// </summary>
    private Task<IActionResult> CompleteOwnerSetupAsync() =>
        _controller.OwnerComplete(
            new SetupOwnerCompleteRequest
            {
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-token",
            },
            CancellationToken.None);

    private void StubPasskeyCompletion(Guid subjectId)
    {
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ReturnsAsync(new PasskeyCredentialResult(Guid.CreateVersion7(), subjectId));
        _recoveryCodeService
            .Setup(s => s.GenerateCodesAsync(subjectId))
            .ReturnsAsync(["code-1", "code-2"]);
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 3600));
    }

    private void SetOidcStateCookieOnRequest(string state) =>
        _controller.ControllerContext.HttpContext.Request.Headers.Cookie =
            $".Nocturne.OidcState={state}";

    /// <summary>
    /// Seeds a subject that already holds platform admin, making the instance an
    /// established one rather than a fresh install.
    /// </summary>
    private async Task SeedPlatformAdminAsync()
    {
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = "Existing Admin",
            Username = "existing-admin",
            IsActive = true,
            IsSystemSubject = true,
            IsPlatformAdmin = true,
        });
        await _dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds the state a fresh install reaches after tenant creation and a first pass
    /// through owner setup: a sole tenant whose credential-less member holds the owner
    /// role. <see cref="ITenantService.AddMemberAsync"/> is mocked, so the membership
    /// and its role link are written directly.
    /// </summary>
    private async Task<(Guid TenantId, Guid SubjectId)> SeedSoleTenantWithOwnerRoleAsync()
    {
        var tenantId = Guid.CreateVersion7();
        var subjectId = Guid.CreateVersion7();
        var memberId = Guid.CreateVersion7();
        var ownerRoleId = Guid.CreateVersion7();

        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = tenantId, Slug = "my-instance", DisplayName = "My Instance",
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId, Name = "Owner", Username = "owner",
            IsActive = true, IsSystemSubject = false,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = memberId, TenantId = tenantId, SubjectId = subjectId,
        });
        _dbContext.Set<TenantRoleEntity>().Add(new TenantRoleEntity
        {
            Id = ownerRoleId, TenantId = tenantId, Name = "Owner", Slug = "owner", IsSystem = true,
        });
        _dbContext.Set<TenantMemberRoleEntity>().Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(), TenantMemberId = memberId, TenantRoleId = ownerRoleId,
        });
        await _dbContext.SaveChangesAsync();

        return (tenantId, subjectId);
    }

    /// <summary>
    /// Seeds the sole tenant plus the credential-less owner subject and membership that
    /// <c>owner/options</c> leaves behind, and returns the subject id.
    /// </summary>
    private async Task<Guid> SeedOwnerlessTenantWithOwnerSubjectAsync()
    {
        var tenantId = Guid.CreateVersion7();
        var subjectId = Guid.CreateVersion7();

        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = tenantId, Slug = "my-instance", DisplayName = "My Instance",
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId, Name = "Owner", Username = "owner",
            IsActive = true, IsSystemSubject = false,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = subjectId,
        });
        await _dbContext.SaveChangesAsync();

        return subjectId;
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

    /// <summary>
    /// Seeds a tenant with a member that has a passkey credential, making
    /// the CreateTenant guard consider setup already complete.
    /// </summary>
    private async Task SeedConfiguredTenantAsync(string slug, string displayName)
    {
        var tenantId = Guid.CreateVersion7();
        var subjectId = Guid.CreateVersion7();

        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = tenantId,
            Slug = slug,
            DisplayName = displayName,
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Owner",
            Username = "owner",
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubjectId = subjectId,
        });
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = System.Text.Encoding.UTF8.GetBytes($"cred-{slug}"),
            PublicKey = [],
            SignCount = 0,
        });

        await _dbContext.SaveChangesAsync();
    }
}
