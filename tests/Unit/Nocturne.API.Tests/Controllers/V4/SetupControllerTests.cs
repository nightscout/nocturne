using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Demo;
using Nocturne.API.Services.Identity;
using Nocturne.API.Tests.Services.Connectors;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using Nocturne.Infrastructure.Data.Repositories;

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
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<ITenantService> _tenantService;
    private readonly Mock<IPasskeyService> _passkeyService;
    private readonly Mock<IRecoveryCodeService> _recoveryCodeService;
    private readonly Mock<ISessionService> _sessionService;
    private readonly Mock<ISubjectService> _subjectService;
    private readonly Mock<IOidcAuthService> _oidcAuthService;
    private readonly Mock<ITotpService> _totpService;
    private readonly Mock<IAuthAuditService> _auditService;
    private readonly PlatformOptions _platformOptions;
    private readonly Mock<IDbContextFactory<NocturneDbContext>> _dbFactory;
    private readonly IOptions<OidcOptions> _oidcOptions;
    private readonly PlatformAdminBootstrapService _platformAdminBootstrap;
    private readonly SetupController _controller;

    public SetupControllerTests()
    {
        _db = TestDbContextFactory.CreateSqlite();

        _dbContext = _db.CreateContext();

        _tenantService = new Mock<ITenantService>();
        _passkeyService = new Mock<IPasskeyService>();
        _recoveryCodeService = new Mock<IRecoveryCodeService>();
        _sessionService = new Mock<ISessionService>();
        _subjectService = new Mock<ISubjectService>();
        _oidcAuthService = new Mock<IOidcAuthService>();
        _totpService = new Mock<ITotpService>();
        _totpService.Setup(t => t.GetSecondFactorRequirementAsync(It.IsAny<Guid>(), It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.NotEnrolled);
        _auditService = new Mock<IAuthAuditService>();
        _sessionService
            .Setup(s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid subjectId, SessionContext _, CancellationToken _) =>
                new SessionTokenPair("access", "refresh", 3600, subjectId));

        _oidcOptions = Options.Create(new OidcOptions
        {
            Cookie = new CookieSettings
            {
                AccessTokenName = ".Nocturne.AccessToken",
                RefreshTokenName = ".Nocturne.RefreshToken",
                Secure = true,
            },
        });

        _dbFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        _dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var ctx = _db.CreateContext();
                return ctx;
            });

        // A real instance, not a mock: the platform-admin grant is part of the
        // behaviour under test, and BootstrapAsync is not virtual.
        _platformOptions = new PlatformOptions();
        _platformAdminBootstrap = new PlatformAdminBootstrapService(
            new TenantRepository(_dbFactory.Object.AsNocturneFactory()),
            new TenantMembershipRepository(_dbFactory.Object.AsNocturneFactory(), AccountLinkRoutes.Off),
            new SubjectRepository(_dbFactory.Object.AsNocturneFactory(), AccountLinkRoutes.Off),
            Options.Create(_platformOptions),
            NullLogger<PlatformAdminBootstrapService>.Instance);

        _controller = BuildController(
            new OperatorConfiguration(),
            new Mock<IHttpClientFactory>().Object,
            new Mock<ILogger<SetupController>>().Object);
    }

    private SetupController BuildController(
        OperatorConfiguration operatorConfig,
        IHttpClientFactory httpClientFactory,
        ILogger<SetupController> logger,
        PlatformAdminBootstrapService? platformAdminBootstrap = null) =>
        new(
            _tenantService.Object,
            _passkeyService.Object,
            _recoveryCodeService.Object,
            new SignInCompletion(
                _totpService.Object,
                new TenantMemberService(
                    new TenantMembershipRepository(_dbFactory.Object.AsNocturneFactory(), AccountLinkRoutes.Off),
                    TimeProvider.System),
                _sessionService.Object,
                _subjectService.Object,
                _auditService.Object),
            _subjectService.Object,
            new SubjectRepository(_dbFactory.Object.AsNocturneFactory(), AccountLinkRoutes.Off),
            new TenantRepository(_dbFactory.Object.AsNocturneFactory()),
            new TenantMembershipRepository(_dbFactory.Object.AsNocturneFactory(), AccountLinkRoutes.Off),
            _oidcOptions,
            _oidcAuthService.Object,
            Options.Create(operatorConfig),
            httpClientFactory,
            platformAdminBootstrap ?? _platformAdminBootstrap,
            new InstanceSetupState(new TenantRepository(_dbFactory.Object.AsNocturneFactory()), new SubjectRepository(_dbFactory.Object.AsNocturneFactory(), AccountLinkRoutes.Off)),
            logger)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
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
        _dbContext.Set<TenantRoleEntity>().Add(OwnerRole(tenantId));
        await _dbContext.SaveChangesAsync();

        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner))
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
    public async Task OwnerOptions_WhenAnotherMemberHoldsTheUsername_Returns400AndIssuesNoOptions()
    {
        var tenantId = Guid.CreateVersion7();
        var candidateId = Guid.Parse("00000000-0000-7000-8000-000000000001");
        var formerId = Guid.Parse("00000000-0000-7000-8000-000000000002");

        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = tenantId, Slug = "my-instance", DisplayName = "My Instance",
        });
        _dbContext.Subjects.AddRange(
            new SubjectEntity { Id = candidateId, Name = "Candidate", Username = "candidate", IsActive = true },
            new SubjectEntity { Id = formerId, Name = "Former", Username = "taken", IsActive = true });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = formerId, Username = "taken",
        });
        _dbContext.Set<TenantRoleEntity>().Add(OwnerRole(tenantId));
        await _dbContext.SaveChangesAsync();

        var result = await _controller.OwnerOptions(
            new SetupOwnerOptionsRequest { Username = "taken", DisplayName = "Owner" },
            CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeOfType<ProblemDetails>().Subject.Detail.Should().Be("This username is already taken");
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner), Times.Never);
        _dbContext.ChangeTracker.Clear();
        (await _dbContext.TenantMembers.CountAsync()).Should().Be(1, "the refusal writes no membership");
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

    [Fact]
    public async Task OwnerOptions_WhenTheOnlyTenantIsTheDemoTenant_Returns409NoTenantExists()
    {
        // An operator who deletes every real tenant leaves one tenant whose member holds no
        // credential — which must not read as a tenant awaiting its first owner.
        await SeedDemoTenantAsync();

        var result = await _controller.OwnerOptions(
            new SetupOwnerOptionsRequest { Username = "someone", DisplayName = "Someone" },
            CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().BeEquivalentTo(new { error = "no_tenant_exists" });
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner), Times.Never);
    }

    [Fact]
    public async Task OwnerOptions_WhenADemoTenantAccompaniesTheOwnerlessTenant_EnrolsANewSubject()
    {
        // A stock install provisions the demo at boot, so the operator's first-run setup runs
        // with the demo visitor already the oldest non-system subject on the instance. Enrolling
        // it would hand the operator's tenant to an account anyone can mint a session for.
        var demoSubjectId = await SeedDemoTenantAsync();
        var tenantId = Guid.CreateVersion7();
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = tenantId, Slug = "my-instance", DisplayName = "My Instance",
        });
        _dbContext.Set<TenantRoleEntity>().Add(OwnerRole(tenantId));
        await _dbContext.SaveChangesAsync();

        Guid enrolling = default;
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner))
            .Callback((Guid subjectId, string _, PasskeyRegistrationFlow _) => enrolling = subjectId)
            .ReturnsAsync(new PasskeyRegistrationOptions("{}", "challenge-token"));

        var result = await _controller.OwnerOptions(
            new SetupOwnerOptionsRequest { Username = "owner", DisplayName = "Owner" },
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<SetupOwnerOptionsResponse>().Subject.TenantId.Should().Be(tenantId);
        enrolling.Should().NotBe(demoSubjectId).And.NotBe(default(Guid));

        var context = FreshContext();
        var demoSubject = await context.Subjects.SingleAsync(s => s.Id == demoSubjectId);
        demoSubject.Name.Should().Be(DemoTenantService.DemoMemberName);
        demoSubject.Username.Should().BeNull();
        (await context.TenantMembers
            .AnyAsync(m => m.TenantId == tenantId && m.SubjectId == demoSubjectId))
            .Should().BeFalse();
    }

    [Fact]
    public async Task OwnerOptions_AuditsTheOwnerRoleAsTheOwnersOwnAct()
    {
        var tenantId = Guid.CreateVersion7();
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = tenantId, Slug = "my-instance", DisplayName = "My Instance",
        });
        _dbContext.Set<TenantRoleEntity>().Add(OwnerRole(tenantId));
        await _dbContext.SaveChangesAsync();
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner))
            .ReturnsAsync(new PasskeyRegistrationOptions("{}", "challenge-token"));

        await _controller.OwnerOptions(
            new SetupOwnerOptionsRequest { Username = "owner", DisplayName = "Owner" },
            CancellationToken.None);

        var context = FreshContext();
        var owner = await context.TenantMembers.SingleAsync(m => m.TenantId == tenantId);
        var rows = await context.AuthAuditLog.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(new
        {
            EventType = AuthAuditEventType.MemberJoined,
            TargetSubjectId = (Guid?)owner.SubjectId,
            ActorSubjectId = (Guid?)owner.SubjectId,
            ActorCredential = (string?)null,
            DetailsJson = """{"kind":"tenant","how":"setup"}""",
        });
        var row = rows[1];
        row.Should().BeEquivalentTo(new
        {
            EventType = AuthAuditEventType.RoleAssigned,
            TargetSubjectId = (Guid?)owner.SubjectId,
            ActorSubjectId = (Guid?)owner.SubjectId,
            ActorCredential = (string?)null,
            DetailsJson = """{"role":"owner","kind":"tenant"}""",
        });
    }

    [Fact]
    public async Task OwnerOptions_WhenTheTenantHasNoOwnerRole_AnswersAnErrorAndCreatesNothing()
    {
        var logger = new Mock<ILogger<SetupController>>();
        var controller = BuildController(
            new OperatorConfiguration(), new Mock<IHttpClientFactory>().Object, logger.Object);
        var tenantId = await SeedTenantWithoutOwnerRoleAsync();

        var result = await controller.OwnerOptions(
            new SetupOwnerOptionsRequest { Username = "owner", DisplayName = "Owner" },
            CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(500);
        problem.Value.Should().BeOfType<ProblemDetails>().Subject.Detail
            .Should().NotContain(tenantId.ToString());
        await AssertNothingWrittenAsync(tenantId);
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner), Times.Never);
        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(tenantId.ToString())),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task OwnerOptions_WhenTheTenantHasNoOwnerRole_LeavesTheRecordedSetupOwnerUnchanged()
    {
        var tenantId = await SeedTenantWithoutOwnerRoleAsync();
        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId, Name = "First Name", Username = "first", IsActive = true,
        });
        (await _dbContext.Set<TenantEntity>().SingleAsync(t => t.Id == tenantId)).SetupOwnerSubjectId = subjectId;
        await _dbContext.SaveChangesAsync();

        var result = await _controller.OwnerOptions(
            new SetupOwnerOptionsRequest { Username = "second", DisplayName = "Second Name" },
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
        var subject = await FreshContext().Subjects.SingleAsync(s => s.Id == subjectId);
        subject.Name.Should().Be("First Name");
        subject.Username.Should().Be("first");
        await AssertNothingWrittenAsync(tenantId, expectedSubjects: 1);
    }

    [Fact]
    public async Task OwnerOptions_WhenRetriedBySubjectAlreadyMember_StillSucceedsWithTheSameSubject()
    {
        var (tenantId, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        _passkeyService
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner))
            .ReturnsAsync(new PasskeyRegistrationOptions("{}", "challenge-token"));

        var result = await _controller.OwnerOptions(
            new SetupOwnerOptionsRequest { Username = "owner", DisplayName = "Owner" },
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _passkeyService.Verify(
            s => s.GenerateRegistrationOptionsAsync(subjectId, "owner", PasskeyRegistrationFlow.SetupOwner), Times.Once);
        var context = FreshContext();
        (await context.TenantMembers.CountAsync(m => m.TenantId == tenantId)).Should().Be(1);
        (await context.Subjects.CountAsync()).Should().Be(1);
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
    public async Task ValidateUsername_WhenTheOnlyTenantIsTheDemoTenant_ReportsNoTenant()
    {
        // Setup is anonymous while no credential exists anywhere, so answering off the demo
        // tenant would make its member names probeable.
        await SeedDemoTenantAsync();

        var result = await _controller.ValidateUsername("demo", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var validation = ok.Value.Should().BeOfType<SlugValidationResult>().Subject;
        validation.IsValid.Should().BeFalse();
        validation.Message.Should().Contain("No tenant exists");
    }

    [Fact]
    public async Task ValidateUsername_WhenEmpty_ReturnsError()
    {
        var result = await _controller.ValidateUsername("", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var validation = ok.Value.Should().BeOfType<SlugValidationResult>().Subject;
        validation.IsValid.Should().BeFalse();
    }

    /// <summary>
    /// A configured webhook that cannot answer must not become a wall across the one screen the
    /// operator has no way past, so the name is admitted. And the endpoint is anonymous, so the
    /// caller chooses the request count — the log volume must not follow it.
    /// </summary>
    [Fact]
    public async Task ValidateUsername_WhenTheWebhookIsDown_AdmitsTheNameAndReportsTheOutageOnce()
    {
        var logger = new Mock<ILogger<SetupController>>();
        var controller = BuildWebhookController(
            logger,
            // An answering webhook first, so the report is owed whatever an earlier test in this
            // process left latched.
            HttpStatusCode.OK,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable);
        await SeedConfiguredTenantAsync("test", "Test");

        await controller.ValidateUsername("primed", CancellationToken.None);
        var first = await controller.ValidateUsername("owner-one", CancellationToken.None);
        var second = await controller.ValidateUsername("owner-two", CancellationToken.None);

        foreach (var result in new[] { first, second })
        {
            result.Should().BeOfType<OkObjectResult>()
                .Which.Value.Should().BeOfType<SlugValidationResult>()
                .Which.IsValid.Should().BeTrue();
        }

        VerifyOutageReported(logger, Times.Once());
    }

    [Fact]
    public async Task ValidateUsername_WhenTheWebhookDoesNotAnswerInTime_AdmitsTheName()
    {
        var controller = BuildSilentWebhookController(TimeSpan.FromMilliseconds(50));
        await SeedConfiguredTenantAsync("test", "Test");

        var result = await controller.ValidateUsername("owner-one", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<SlugValidationResult>()
            .Which.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateUsername_WhenTheCallerCancels_DoesNotTreatItAsAWebhookOutage()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = BuildSilentWebhookController(TimeSpan.FromSeconds(30), arrived);
        await SeedConfiguredTenantAsync("test", "Test");
        using var cts = new CancellationTokenSource();

        var call = controller.ValidateUsername("owner-one", cts.Token);
        await arrived.Task;
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
    }

    private SetupController BuildSilentWebhookController(TimeSpan timeout, TaskCompletionSource? arrived = null)
    {
        var handler = new SilentHandler(arrived);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(OperatorWebhook.UsernameValidationClient))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { Timeout = timeout });

        return BuildController(
            new OperatorConfiguration { UsernameValidationWebhookUrl = "https://webhook.invalid/validate" },
            httpClientFactory.Object,
            new Mock<ILogger<SetupController>>().Object);
    }

    private sealed class SilentHandler(TaskCompletionSource? arrived) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            arrived?.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private SetupController BuildWebhookController(
        Mock<ILogger<SetupController>> logger, params HttpStatusCode[] responses)
    {
        var handler = new SequentialMockHandler();
        foreach (var status in responses)
        {
            handler.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    """{"isValid":true}""", Encoding.UTF8, "application/json"),
            });
        }

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler));

        return BuildController(
            new OperatorConfiguration
            {
                UsernameValidationWebhookUrl = "https://webhook.invalid/validate",
            },
            httpClientFactory.Object,
            logger.Object);
    }

    private static void VerifyOutageReported(
        Mock<ILogger<SetupController>> logger, Times times) =>
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

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
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner, It.IsAny<Guid>(), ownerSubjectId, It.IsAny<string?>()),
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
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 900, ownerSubjectId));

        var result = await _controller.OwnerComplete(
            new SetupOwnerCompleteRequest
            {
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-owner",
            },
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<SetupOwnerCompleteResponse>().Subject.Success.Should().BeTrue();
        _subjectService.Verify(s => s.UpdateLastLoginAsync(ownerSubjectId), Times.Once);
    }

    [Fact]
    public async Task OwnerComplete_WhenTheSignInTimeCannotBeRecorded_LeavesAnUndeliveredSessionAndSetsNoCookie()
    {
        var ownerSubjectId = await SeedOwnerlessTenantWithOwnerSubjectAsync();
        StubRegistrationChallengeMintedFor(ownerSubjectId);
        _subjectService.Setup(s => s.UpdateLastLoginAsync(ownerSubjectId))
            .ThrowsAsync(new DbUpdateException("the update failed"));

        var act = () => _controller.OwnerComplete(
            new SetupOwnerCompleteRequest
            {
                AttestationResponseJson = "{}",
                ChallengeToken = "challenge-for-owner",
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        _sessionService.Verify(
            s => s.IssueSessionAsync(ownerSubjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the session is issued before last-login, and its refresh token is never delivered");
        _controller.HttpContext.Response.Headers.SetCookie.Should().BeEmpty();
        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(It.IsAny<Guid>()), Times.Never);
        VerifyNoLoginRow();
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
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner, It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
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

    [Fact]
    public async Task OwnerOidc_WhenTheTenantHasNoOwnerRole_AnswersAnErrorAndCreatesNothing()
    {
        var tenantId = await SeedTenantWithoutOwnerRoleAsync();

        var result = await _controller.OwnerOidc(
            new SetupOwnerOidcRequest { Username = "owner", DisplayName = "Owner", ProviderId = Guid.CreateVersion7() },
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
        await AssertNothingWrittenAsync(tenantId);
        _oidcAuthService.Verify(
            s => s.GenerateSetupAuthorizationUrlAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()),
            Times.Never);
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
    public async Task OwnerComplete_WhenADemoTenantAccompaniesTheSoleTenant_StillGrantsPlatformAdmin()
    {
        // The grant re-derives single-tenant-ness for itself, so it has to count tenants the way
        // the guard that admitted this setup does — or a stock install's owner completes setup
        // and still cannot reach the admin UI.
        await SeedDemoTenantAsync();
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
            .Setup(s => s.GenerateRegistrationOptionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner))
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
            .Setup(s => s.HandleSetupCallbackAsync("auth-code", "expected-state", "expected-state", It.IsAny<Guid>()))
            .ReturnsAsync(OidcSetupCallbackResult.Succeeded(subjectId, Guid.CreateVersion7(), "sid"));

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
            .Setup(s => s.HandleSetupCallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()))
            .ReturnsAsync(OidcSetupCallbackResult.Failed("invalid_grant"));

        await _controller.OidcCallback(
            code: "auth-code", state: "expected-state", error: null, error_description: null,
            ct: CancellationToken.None);

        var subject = await FreshContext().Subjects.SingleAsync(s => s.Id == subjectId);
        subject.IsPlatformAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task OwnerComplete_WhenAnotherCompletionHasClaimedTheSetup_AnswersAsTheOpenCheckAndSignsNoOneIn()
    {
        var (tenantId, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubPasskeyCompletion(subjectId);
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner, tenantId, subjectId, It.IsAny<string?>()))
            .ThrowsAsync(new SetupOwnerTakenException());

        var result = await CompleteOwnerSetupAsync();

        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(It.IsAny<Guid>()), Times.Never);
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _subjectService.Verify(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()), Times.Never);
        _auditService.VerifyNoOtherCalls();
        _controller.HttpContext.Response.Headers.SetCookie.ToString().Should().BeEmpty();
        (await FreshContext().Subjects.SingleAsync(s => s.Id == subjectId)).IsPlatformAdmin.Should().BeFalse();

        await GiveOwnerAPasskeyAsync(subjectId);
        var afterSetupClosed = await CompleteOwnerSetupAsync();
        result.Should().BeOfType<ConflictObjectResult>();
        afterSetupClosed.Should().BeOfType<ConflictObjectResult>();
        System.Text.Json.JsonSerializer.Serialize(((ConflictObjectResult)result).Value)
            .Should().Be(System.Text.Json.JsonSerializer.Serialize(((ConflictObjectResult)afterSetupClosed).Value))
            .And.Be("""{"error":"owner_already_exists"}""");
    }

    [Fact]
    public async Task OwnerComplete_WhenThePlatformAdminGrantThrows_IssuesNoSession()
    {
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubPasskeyCompletion(subjectId);
        var failingSubjects = new Mock<ISubjectRepository>();
        failingSubjects
            .Setup(s => s.AnyPlatformAdminAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("grant failed"));
        var controller = BuildController(
            new OperatorConfiguration(),
            new Mock<IHttpClientFactory>().Object,
            new Mock<ILogger<SetupController>>().Object,
            new PlatformAdminBootstrapService(
                new TenantRepository(_dbFactory.Object.AsNocturneFactory()),
                new TenantMembershipRepository(_dbFactory.Object.AsNocturneFactory(), AccountLinkRoutes.Off),
                failingSubjects.Object,
                Options.Create(_platformOptions),
                NullLogger<PlatformAdminBootstrapService>.Instance));

        var act = () => controller.OwnerComplete(
            new SetupOwnerCompleteRequest { AttestationResponseJson = "{}", ChallengeToken = "challenge-token" },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("only the passkey proof's refusals are answered as one");
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        failingSubjects.Verify(s => s.AnyPlatformAdminAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OidcCallback_ClaimsTheSetupOfTheTenantTheOpenCheckFound()
    {
        var (tenantId, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        SetOidcStateCookieOnRequest("expected-state");
        _oidcAuthService
            .Setup(s => s.HandleSetupCallbackAsync("auth-code", "expected-state", "expected-state", tenantId))
            .ReturnsAsync(OidcSetupCallbackResult.Succeeded(subjectId, Guid.CreateVersion7(), "sid"));

        var result = await CallBackAsync(_controller);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/setup");
    }

    [Fact]
    public async Task OidcCallback_WhenAnotherCompletionHasClaimedTheSetup_AnswersAsTheOpenCheckAndSignsNoOneIn()
    {
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        SetOidcStateCookieOnRequest("expected-state");
        _oidcAuthService
            .Setup(s => s.HandleSetupCallbackAsync("auth-code", "expected-state", "expected-state", It.IsAny<Guid>()))
            .ReturnsAsync(OidcSetupCallbackResult.Failed("setup_already_complete", "Another completion has claimed the setup"));

        var result = await CallBackAsync(_controller);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/setup?error=setup_already_complete");
        _controller.HttpContext.Response.Headers.SetCookie.ToString().Should().NotContain(".Nocturne.AccessToken");
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _auditService.VerifyNoOtherCalls();
        (await FreshContext().Subjects.SingleAsync(s => s.Id == subjectId)).IsPlatformAdmin.Should().BeFalse();
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
            s => s.HandleSetupCallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()),
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
            .Setup(s => s.HandleSetupCallbackAsync("auth-code", "expected-state", "expected-state", It.IsAny<Guid>()))
            .ReturnsAsync(OidcSetupCallbackResult.Succeeded(bystanderId, Guid.CreateVersion7(), "sid"));

        await _controller.OidcCallback(
            code: "auth-code", state: "expected-state", error: null, error_description: null,
            ct: CancellationToken.None);

        var bystander = await FreshContext().Subjects.SingleAsync(s => s.Id == bystanderId);
        bystander.IsPlatformAdmin.Should().BeFalse();
    }

    // ── Setup signs the owner in through the sign-in completion ──────────

    private const string ClientIp = "203.0.113.7";
    private const string Agent = "Firefox";
    private const string SetupPasskeyRow = """{"method":"passkey","enrolment":"setup"}""";
    private const string SetupOidcRow = """{"method":"oidc","enrolment":"setup"}""";

    [Fact]
    public async Task OwnerComplete_EndsSetupThenSignsInThenMakesRecoveryCodes()
    {
        var (tenantId, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        var steps = RecordSteps(tenantId);
        StubStoredPasskey(subjectId, steps);
        AsBrowser(_controller);

        var result = await CompleteOwnerSetupAsync();

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeEquivalentTo(new SetupOwnerCompleteResponse
        {
            Success = true,
            RecoveryCodes = ["code-1", "code-2"],
            AccessToken = "access",
            RefreshToken = "refresh",
            ExpiresIn = 3600,
        });
        steps.Should().Equal(
            "proof", "session (setup ended)", "last-login",
            $"audit:{AuthAuditEventType.Login}:True:{SetupPasskeyRow}", "recovery-codes (setup ended)");
        var expectedContext = new SessionContext(DeviceDescription: "Setup Passkey", IpAddress: ClientIp, UserAgent: Agent);
        _sessionService.Verify(s => s.IssueSessionAsync(subjectId, expectedContext, It.IsAny<CancellationToken>()));
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.Login, subjectId, true, ClientIp, Agent, null, SetupPasskeyRow, null, null, null));
    }

    [Fact]
    public async Task OwnerComplete_SetsTheSessionCookiesAndThePasskeySignInHint()
    {
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubStoredPasskey(subjectId);

        await CompleteOwnerSetupAsync();

        var cookies = _controller.HttpContext.Response.Headers.SetCookie.ToString();
        cookies.Should().Contain(".Nocturne.AccessToken=access");
        cookies.Should().Contain(".Nocturne.RefreshToken=refresh");
        cookies.Should().Contain($"{SessionCookieExtensions.LastSignInCookieName}=passkey");
    }

    [Fact]
    public async Task OwnerComplete_OverAHandedOffSession_EndsIt()
    {
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubStoredPasskey(subjectId);
        var previousHolder = Guid.CreateVersion7();
        var refreshTokens = PresentHandedOffSession(_controller, previousHolder);

        (await CompleteOwnerSetupAsync()).Should().BeOfType<OkObjectResult>();

        refreshTokens.Verify(
            r => r.RevokeSessionForSubjectAsync(previousHolder, "previous-session", It.IsAny<string>()), Times.Once);
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.Logout, previousHolder, true, It.IsAny<string?>(), It.IsAny<string?>(), null,
            """{"reason":"replaced_by_sign_in"}""", null, null, null));
    }

    public static TheoryData<Exception> PasskeyProofRefusals => new()
    {
        new InvalidOperationException("Registration challenge was not issued for the enrolling subject."),
        new JsonException("The attestation is not JSON."),
        new PasskeyRegistrationRefusedException(
            "The attestation was refused.", new System.Formats.Asn1.AsnContentException("Must be a primitive")),
    };

    [Theory]
    [MemberData(nameof(PasskeyProofRefusals))]
    public async Task OwnerComplete_AnswersEachRefusalOfThePasskeyProofAsAFailedRegistration(Exception refusal)
    {
        await SeedSoleTenantWithOwnerRoleAsync();
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner, It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .ThrowsAsync(refusal);

        var result = await CompleteOwnerSetupAsync();

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        var details = problem.Value.Should().BeOfType<ProblemDetails>().Subject;
        details.Title.Should().Be("Registration Failed");
        details.Detail.Should().Be("Passkey registration failed during setup");
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(It.IsAny<Guid>()), Times.Never);
        VerifyNoLoginRow();
    }

    [Fact]
    public async Task OwnerComplete_DoesNotAnswerAFailureOfThePasskeyStoreAsARefusedProof()
    {
        await SeedSoleTenantWithOwnerRoleAsync();
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner, It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .ThrowsAsync(new TimeoutException("the database did not answer"));

        var act = () => CompleteOwnerSetupAsync();

        await act.Should().ThrowAsync<TimeoutException>();
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OwnerComplete_OnceSetupHasEnded_IsRefusedAndIssuesNoSecondSession()
    {
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubStoredPasskey(subjectId);
        (await CompleteOwnerSetupAsync()).Should().BeOfType<OkObjectResult>();

        var again = await CompleteOwnerSetupAsync();

        again.Should().BeOfType<ConflictObjectResult>().Which.Value.Should()
            .BeEquivalentTo(new { error = "owner_already_exists" });
        _passkeyService.Verify(
            s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PasskeyRegistrationFlow>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Once);
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The owner holds no authenticator, so no code is owed. Were one owed, the owner is not
    /// signed in, and setup has still ended with an owner who signs in on the login page.
    /// </summary>
    [Fact]
    public async Task OwnerComplete_WhenACodeWereOwed_SignsNoOneInAndLeavesAnOwnerWhoCanSignIn()
    {
        var (tenantId, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubStoredPasskey(subjectId);
        _totpService.Setup(t => t.GetSecondFactorRequirementAsync(subjectId, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        _totpService.Setup(t => t.CreateStepUpTokenAsync(subjectId, tenantId, false)).ReturnsAsync("step-up");

        var result = await CompleteOwnerSetupAsync();

        result.Should().BeOfType<ConflictObjectResult>().Which.Value.Should()
            .BeEquivalentTo(new { error = "owner_already_exists" });
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        var cookies = _controller.HttpContext.Response.Headers.SetCookie.ToString();
        cookies.Should().NotContain(".Nocturne.AccessToken");
        cookies.Should().Contain($"{SessionCookieExtensions.LastSignInCookieName}=passkey");
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.Login, subjectId, false, It.IsAny<string?>(), It.IsAny<string?>(), null,
            """{"method":"passkey","enrolment":"setup","secondFactorPending":true}""", null, null, null));
        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(It.IsAny<Guid>()), Times.Never);

        await AssertOwnerCanSignInAsync(tenantId, subjectId);
    }

    [Fact]
    public async Task OwnerComplete_WhenTheOwnerIsNoLongerAMember_IsRefusedWithNoSession()
    {
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubStoredPasskey(subjectId);
        await RemoveMembershipAsync(subjectId);

        var result = await CompleteOwnerSetupAsync();

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _controller.HttpContext.Response.Headers.SetCookie.Should().BeEmpty();
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.FailedAuth, subjectId, false, It.IsAny<string?>(), It.IsAny<string?>(), null,
            """{"method":"passkey","enrolment":"setup","reason":"not_a_member"}""", null, null, null));
        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(It.IsAny<Guid>()), Times.Never);
        VerifyNoLoginRow();
    }

    /// <summary>
    /// The session cookies are already on the response when the codes fail; the exception handler
    /// clears the response before it writes the 500, so none is delivered.
    /// </summary>
    [Fact]
    public async Task OwnerComplete_WhenTheRecoveryCodesCannotBeMade_LeavesAnUndeliveredSessionAndAnOwnerWhoSignsInWithThePasskey()
    {
        var (tenantId, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubStoredPasskey(subjectId);
        _recoveryCodeService.Setup(s => s.GenerateCodesAsync(subjectId))
            .ThrowsAsync(new DbUpdateException("the codes were not stored"));

        var act = () => CompleteOwnerSetupAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
        _sessionService.Verify(
            s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        await AssertOwnerCanSignInAsync(tenantId, subjectId);
    }

    [Fact]
    public async Task OwnerComplete_WhenTheSessionCannotBeIssued_MakesNoRecoveryCodesAndLeavesAnOwnerWhoSignsInWithThePasskey()
    {
        var (tenantId, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubStoredPasskey(subjectId);
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("the session was not stored"));

        var act = () => CompleteOwnerSetupAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
        _recoveryCodeService.Verify(s => s.GenerateCodesAsync(It.IsAny<Guid>()), Times.Never);
        _subjectService.Verify(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()), Times.Never);
        _controller.HttpContext.Response.Headers.SetCookie.Should().BeEmpty();
        VerifyNoLoginRow();
        await AssertOwnerCanSignInAsync(tenantId, subjectId);
    }

    /// <summary>
    /// The session cookies are already on the response when the end fails; the exception handler
    /// clears the response before it writes the 500, so none is delivered.
    /// </summary>
    [Fact]
    public async Task OwnerComplete_WhenTheHandedOffSessionCannotBeEnded_LeavesTheLoginRowAnUndeliveredSessionAndNoHint()
    {
        var (tenantId, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubStoredPasskey(subjectId);
        var previousHolder = Guid.CreateVersion7();
        var refreshTokens = PresentHandedOffSession(_controller, previousHolder);
        refreshTokens
            .Setup(r => r.RevokeSessionForSubjectAsync(previousHolder, "previous-session", It.IsAny<string>()))
            .ThrowsAsync(new DbUpdateException("the revoke failed"));

        var act = () => CompleteOwnerSetupAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
        _sessionService.Verify(
            s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.Login, subjectId, true, It.IsAny<string?>(), It.IsAny<string?>(), null,
            SetupPasskeyRow, null, null, null), Times.Once);
        _auditService.Verify(
            a => a.LogAsync(
                AuthAuditEventType.Logout, It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()),
            Times.Never);
        _controller.HttpContext.Response.Headers.SetCookie.ToString().Should()
            .NotContain(SessionCookieExtensions.LastSignInCookieName);
        await AssertOwnerCanSignInAsync(tenantId, subjectId);
    }

    [Fact]
    public async Task OidcCallback_EndsSetupBeforeTheSessionThenLastLoginThenTheLoginRow()
    {
        var (tenantId, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        var steps = RecordSteps(tenantId);
        var providerId = StubSetupCallback(_controller, subjectId);
        AsBrowser(_controller);

        var result = await CallBackAsync(_controller);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/setup");
        steps.Should().Equal(
            "session (setup ended)", "last-login", $"audit:{AuthAuditEventType.Login}:True:{SetupOidcRow}");
        var expectedContext = new SessionContext(OidcSessionId: "sid", DeviceDescription: "Setup OIDC", IpAddress: ClientIp, UserAgent: Agent);
        _sessionService.Verify(s => s.IssueSessionAsync(subjectId, expectedContext, It.IsAny<CancellationToken>()));
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.Login, subjectId, true, ClientIp, Agent, null, SetupOidcRow, null, null, null));
        var cookies = _controller.HttpContext.Response.Headers.SetCookie.ToString();
        cookies.Should().Contain(".Nocturne.AccessToken=access");
        cookies.Should().Contain(".Nocturne.RefreshToken=refresh");
        cookies.Should().Contain($"{SessionCookieExtensions.LastSignInCookieName}=oidc%3A{providerId}");
    }

    [Fact]
    public async Task OidcCallback_OverAHandedOffSession_EndsIt()
    {
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubSetupCallback(_controller, subjectId);
        var previousHolder = Guid.CreateVersion7();
        var refreshTokens = PresentHandedOffSession(_controller, previousHolder);

        await CallBackAsync(_controller);

        refreshTokens.Verify(
            r => r.RevokeSessionForSubjectAsync(previousHolder, "previous-session", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task OidcCallback_WhenThePlatformAdminGrantThrows_IssuesNoSession()
    {
        var (tenantId, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        var failingSubjects = new Mock<ISubjectRepository>();
        failingSubjects
            .Setup(s => s.AnyPlatformAdminAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("grant failed"));
        var controller = BuildController(
            new OperatorConfiguration(),
            new Mock<IHttpClientFactory>().Object,
            new Mock<ILogger<SetupController>>().Object,
            new PlatformAdminBootstrapService(
                new TenantRepository(_dbFactory.Object.AsNocturneFactory()),
                new TenantMembershipRepository(_dbFactory.Object.AsNocturneFactory(), AccountLinkRoutes.Off),
                failingSubjects.Object,
                Options.Create(_platformOptions),
                NullLogger<PlatformAdminBootstrapService>.Instance));
        StubSetupCallback(controller, subjectId);

        var act = () => CallBackAsync(controller);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the owner step runs before the sign-in, so its failure leaves no undelivered session");
        controller.HttpContext.Response.Headers.SetCookie.ToString().Should().NotContain(".Nocturne.AccessToken");
        (await FreshContext().Tenants.SingleAsync(t => t.Id == tenantId)).SetupOwnerSubjectId.Should().BeNull();
    }

    [Fact]
    public async Task OidcCallback_WhenTheSessionCannotBeIssued_LeavesAnOwnerWhoSignsInWithTheIdentityAndNoLoginRow()
    {
        var (tenantId, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubSetupCallback(_controller, subjectId);
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("the session was not stored"));

        var act = () => CallBackAsync(_controller);

        await act.Should().ThrowAsync<DbUpdateException>();
        _subjectService.Verify(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()), Times.Never);
        _controller.HttpContext.Response.Headers.SetCookie.ToString().Should().NotContain(".Nocturne.AccessToken");
        VerifyNoLoginRow();
        (await FreshContext().Tenants.SingleAsync(t => t.Id == tenantId)).SetupOwnerSubjectId.Should().BeNull();
        (await FreshContext().Subjects.SingleAsync(s => s.Id == subjectId)).IsPlatformAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task OidcCallback_WhenACodeWereOwed_SignsNoOneInAndLeavesAnOwnerWhoCanSignIn()
    {
        var (tenantId, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        var providerId = StubSetupCallback(_controller, subjectId);
        _totpService.Setup(t => t.GetSecondFactorRequirementAsync(subjectId, It.IsAny<string?>()))
            .ReturnsAsync(SecondFactorRequirement.CodeRequired);
        _totpService.Setup(t => t.CreateStepUpTokenAsync(subjectId, tenantId, false)).ReturnsAsync("step-up");

        var result = await CallBackAsync(_controller);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/setup?error=setup_already_complete");
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        var cookies = _controller.HttpContext.Response.Headers.SetCookie.ToString();
        cookies.Should().NotContain(".Nocturne.AccessToken");
        cookies.Should().Contain($"{SessionCookieExtensions.LastSignInCookieName}=oidc%3A{providerId}");
        (await FreshContext().Tenants.SingleAsync(t => t.Id == tenantId)).SetupOwnerSubjectId.Should().BeNull();
        (await FreshContext().Subjects.SingleAsync(s => s.Id == subjectId)).IsPlatformAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task OidcCallback_WhenTheOwnerIsNoLongerAMember_IsRefusedWithNoSession()
    {
        var (_, subjectId) = await SeedSoleTenantWithOwnerRoleAsync();
        StubSetupCallback(_controller, subjectId);
        await RemoveMembershipAsync(subjectId);

        var result = await CallBackAsync(_controller);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/setup?error=not_a_member");
        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _controller.HttpContext.Response.Headers.SetCookie.ToString().Should().NotContain(".Nocturne.AccessToken");
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.FailedAuth, subjectId, false, It.IsAny<string?>(), It.IsAny<string?>(), null,
            """{"method":"oidc","enrolment":"setup","reason":"not_a_member"}""", null, null, null));
    }

    private static void AsBrowser(SetupController controller)
    {
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse(ClientIp);
        controller.HttpContext.Request.Headers.UserAgent = Agent;
    }

    /// <summary>
    /// Records the steps after the proof in order, each marked with whether setup had ended (the
    /// tenant's setup owner record cleared) when it ran.
    /// </summary>
    private List<string> RecordSteps(Guid tenantId)
    {
        var steps = new List<string>();
        string Ended() =>
            FreshContext().Tenants.Single(t => t.Id == tenantId).SetupOwnerSubjectId == null ? " (setup ended)" : "";

        _recoveryCodeService.Setup(s => s.GenerateCodesAsync(It.IsAny<Guid>()))
            .Callback(() => steps.Add("recovery-codes" + Ended()))
            .ReturnsAsync(["code-1", "code-2"]);
        _sessionService
            .Setup(s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => steps.Add("session" + Ended()))
            .ReturnsAsync((Guid subjectId, SessionContext _, CancellationToken _) =>
                new SessionTokenPair("access", "refresh", 3600, subjectId));
        _subjectService.Setup(s => s.UpdateLastLoginAsync(It.IsAny<Guid>()))
            .Callback(() => steps.Add("last-login"))
            .Returns(Task.CompletedTask);
        _auditService
            .Setup(a => a.LogAsync(
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()))
            .Callback((string type, Guid? _, bool success, string? _, string? _, string? _, string? details, Guid? _,
                AuthAuditActor? _, Guid? _) => steps.Add($"audit:{type}:{success}:{details}"))
            .Returns(Task.CompletedTask);
        return steps;
    }

    /// <summary>
    /// Stands in for the passkey proof for <paramref name="subjectId"/>: stores the passkey and ends
    /// the setup owner record as the real store does, which is what ends setup.
    /// </summary>
    private void StubStoredPasskey(Guid subjectId, List<string>? steps = null) =>
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner, It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ReturnsAsync(() =>
            {
                steps?.Add("proof");
                using var db = FreshContext();
                db.PasskeyCredentials.Add(new PasskeyCredentialEntity
                {
                    Id = Guid.CreateVersion7(),
                    SubjectId = subjectId,
                    CredentialId = Guid.NewGuid().ToByteArray(),
                    PublicKey = [],
                    SignCount = 0,
                });
                EndSetupOwnerRecord(db, subjectId);
                db.SaveChanges();
                return new PasskeyCredentialResult(Guid.CreateVersion7(), subjectId);
            });

    /// <summary>
    /// Makes the provider callback prove an identity for <paramref name="subjectId"/> and end the
    /// setup owner record, as the real link does.
    /// </summary>
    private Guid StubSetupCallback(SetupController controller, Guid subjectId)
    {
        var providerId = Guid.CreateVersion7();
        controller.HttpContext.Request.Headers.Cookie = ".Nocturne.OidcState=expected-state";
        _oidcAuthService
            .Setup(s => s.HandleSetupCallbackAsync("auth-code", "expected-state", "expected-state", It.IsAny<Guid>()))
            .ReturnsAsync(() =>
            {
                using var db = FreshContext();
                EndSetupOwnerRecord(db, subjectId);
                db.SaveChanges();
                return OidcSetupCallbackResult.Succeeded(subjectId, providerId, "sid");
            });
        return providerId;
    }

    private static void EndSetupOwnerRecord(NocturneDbContext db, Guid subjectId)
    {
        foreach (var tenant in db.Tenants.Where(t => t.SetupOwnerSubjectId == subjectId))
            tenant.SetupOwnerSubjectId = null;
    }

    private async Task GiveOwnerAPasskeyAsync(Guid subjectId)
    {
        await using var db = FreshContext();
        db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = Guid.NewGuid().ToByteArray(),
            PublicKey = [],
            SignCount = 0,
        });
        await db.SaveChangesAsync();
    }

    private static Task<IActionResult> CallBackAsync(SetupController controller) =>
        controller.OidcCallback(
            code: "auth-code", state: "expected-state", error: null, error_description: null,
            ct: CancellationToken.None);

    private Mock<IRefreshTokenService> PresentHandedOffSession(SetupController controller, Guid holder)
    {
        var refreshTokens = new Mock<IRefreshTokenService>();
        refreshTokens.Setup(r => r.FindOwnerAsync("presented"))
            .ReturnsAsync(new RefreshTokenSession(holder, "previous-session", true));
        controller.HttpContext.RequestServices = new ServiceCollection()
            .AddSingleton(refreshTokens.Object).AddSingleton(_auditService.Object).BuildServiceProvider();
        var presented = ".Nocturne.RefreshToken=presented";
        var existing = controller.HttpContext.Request.Headers.Cookie.ToString();
        controller.HttpContext.Request.Headers.Cookie = existing.Length == 0 ? presented : $"{existing}; {presented}";
        return refreshTokens;
    }

    private async Task RemoveMembershipAsync(Guid subjectId)
    {
        await using var db = FreshContext();
        var memberIds = await db.TenantMembers.Where(m => m.SubjectId == subjectId).Select(m => m.Id).ToListAsync();
        await db.Set<TenantMemberRoleEntity>().Where(r => memberIds.Contains(r.TenantMemberId)).ExecuteDeleteAsync();
        await db.TenantMembers.Where(m => m.SubjectId == subjectId).ExecuteDeleteAsync();
    }

    /// <summary>
    /// Setup has ended with an owner who holds a passkey, the owner role and platform admin, so the
    /// owner signs in on the login page and setup answers that an owner exists.
    /// </summary>
    private async Task AssertOwnerCanSignInAsync(Guid tenantId, Guid subjectId)
    {
        await using var db = FreshContext();
        (await db.Tenants.SingleAsync(t => t.Id == tenantId)).SetupOwnerSubjectId.Should().BeNull();
        (await db.Subjects.SingleAsync(s => s.Id == subjectId)).IsPlatformAdmin.Should().BeTrue();
        (await db.PasskeyCredentials.CountAsync(c => c.SubjectId == subjectId)).Should().Be(1);
        (await db.TenantMembers.CountAsync(m => m.TenantId == tenantId && m.SubjectId == subjectId)).Should().Be(1);
        (await CompleteOwnerSetupAsync()).Should().BeOfType<ConflictObjectResult>();
    }

    private void VerifyNoLoginRow() =>
        _auditService.Verify(
            a => a.LogAsync(
                AuthAuditEventType.Login, It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()),
            Times.Never);

    // ── Helpers ──────────────────────────────────────────────────────────

    private NocturneDbContext FreshContext() => _db.CreateContext();

    private static TenantRoleEntity OwnerRole(Guid tenantId) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "Owner", Slug = RoleSeeds.Owner, IsSystem = true,
    };

    /// <summary>Seeds the sole tenant of a damaged install: it holds no owner role.</summary>
    private async Task<Guid> SeedTenantWithoutOwnerRoleAsync()
    {
        var tenantId = Guid.CreateVersion7();
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = tenantId, Slug = "my-instance", DisplayName = "My Instance",
        });
        await _dbContext.SaveChangesAsync();
        return tenantId;
    }

    private async Task AssertNothingWrittenAsync(Guid tenantId, int expectedSubjects = 0)
    {
        var context = FreshContext();
        (await context.Subjects.CountAsync()).Should().Be(expectedSubjects, "a refused step creates no subject");
        (await context.TenantMembers.CountAsync(m => m.TenantId == tenantId)).Should().Be(0, "a refused step grants nothing");
        _subjectService.Verify(s => s.AssignRoleAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

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
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner, It.IsAny<Guid>(), subjectId, It.IsAny<string?>()))
            .ReturnsAsync(new PasskeyCredentialResult(Guid.CreateVersion7(), subjectId));
        _recoveryCodeService
            .Setup(s => s.GenerateCodesAsync(subjectId))
            .ReturnsAsync(["code-1", "code-2"]);
        _sessionService
            .Setup(s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access", "refresh", 3600, subjectId));
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
    /// Seeds the demo tenant in the shape <see cref="DemoTenantService.ConfigureAccessAsync"/>
    /// leaves it: the visitor subject carries no global username and no credential, and the
    /// membership carries the <c>demo</c> username. Returns the visitor subject's id.
    /// </summary>
    /// <remarks>
    /// Seeded first in every test that uses it, so its UUIDv7 subject id sorts ahead of the
    /// operator's — which is the ordering that makes it the first-run owner candidate.
    /// </remarks>
    private async Task<Guid> SeedDemoTenantAsync()
    {
        var tenantId = Guid.CreateVersion7();
        var subjectId = Guid.CreateVersion7();

        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = tenantId, Slug = "demo", DisplayName = "Demo", IsDemo = true,
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId, Name = DemoTenantService.DemoMemberName,
            IsActive = true, IsSystemSubject = false, IsDemoSubject = true,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = subjectId,
            Username = DemoTenantService.DemoMemberUsername,
        });
        await _dbContext.SaveChangesAsync();

        return subjectId;
    }

    /// <summary>
    /// Seeds the state a fresh install reaches after tenant creation and a first pass
    /// through owner setup: a sole tenant whose credential-less member holds the owner
    /// role and is the tenant's recorded setup owner, written directly.
    /// </summary>
    private async Task<(Guid TenantId, Guid SubjectId)> SeedSoleTenantWithOwnerRoleAsync()
    {
        var tenantId = Guid.CreateVersion7();
        var subjectId = Guid.CreateVersion7();
        var memberId = Guid.CreateVersion7();
        var ownerRoleId = Guid.CreateVersion7();

        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId, Name = "Owner", Username = "owner",
            IsActive = true, IsSystemSubject = false,
        });
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = tenantId, Slug = "my-instance", DisplayName = "My Instance", SetupOwnerSubjectId = subjectId,
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
    private async Task<Guid> SeedOwnerlessTenantWithOwnerSubjectAsync() =>
        (await SeedSoleTenantWithOwnerRoleAsync()).SubjectId;

    /// <summary>
    /// Makes the passkey service behave as the real one does for a challenge token minted for
    /// <paramref name="subjectId"/>: it accepts that subject as the enrolling one and refuses
    /// every other.
    /// </summary>
    private void StubRegistrationChallengeMintedFor(Guid subjectId)
    {
        _passkeyService
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(), It.IsAny<string>(), PasskeyRegistrationFlow.SetupOwner, It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>()))
            .ReturnsAsync((string _, string _, PasskeyRegistrationFlow _, Guid _, Guid expectedSubjectId, string? _) =>
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
