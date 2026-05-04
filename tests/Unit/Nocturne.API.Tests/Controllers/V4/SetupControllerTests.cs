using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4;
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

    // OwnerOptions tests that require a sole tenant with members are skipped
    // in unit tests because GetSoleTenantWithoutOwnerAsync calls set_config(),
    // a PostgreSQL-only function. These scenarios are covered by integration tests.

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

    // SoftLock_TenantWithOnlySystemMembers_OwnerOptionsSucceeds is an
    // integration test — it requires PostgreSQL's set_config() function
    // which is not available in SQLite.

    // ── ValidateUsername ──────────────────────────────────────────────────
    // Tests that hit the DB after format checks (valid usernames) are skipped
    // because ValidateUsername calls ExecuteSqlRawAsync("set_config(...)"),
    // a PostgreSQL-only function not available in SQLite.

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

    // OwnerOidc tests that require a sole tenant (e.g. validation of empty
    // username/ProviderId) are skipped in unit tests because
    // GetSoleTenantWithoutOwnerAsync calls set_config(), a PostgreSQL-only
    // function. These scenarios are covered by integration tests.

    // ── Helpers ──────────────────────────────────────────────────────────

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
