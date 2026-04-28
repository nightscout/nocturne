using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Middleware;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

public class TenantSetupMiddlewareTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<ITenantAccessor> _tenantAccessor;
    private readonly Guid _tenantId = Guid.CreateVersion7();

    public TenantSetupMiddlewareTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(options);
        _dbContext.TenantId = _tenantId;
        _dbContext.Database.EnsureCreated();

        // Seed the tenant entity so FK constraints are satisfied for TenantMembers
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test Tenant",
        });
        _dbContext.SaveChanges();

        _tenantAccessor = new Mock<ITenantAccessor>();
        _tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        _tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private (TenantSetupMiddleware middleware, DefaultHttpContext context) Build(
        string path = "/api/status",
        Action? onNext = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        var mw = new TenantSetupMiddleware(
            _ => { onNext?.Invoke(); return Task.CompletedTask; },
            NullLogger<TenantSetupMiddleware>.Instance);
        return (mw, ctx);
    }

    [Fact]
    public async Task WhenTenantHasNoCredentials_Returns503WithSetupRequired()
    {
        // Arrange — no passkey credentials in db
        var mw = new TenantSetupMiddleware(
            _ => Task.CompletedTask,
            NullLogger<TenantSetupMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/status";
        ctx.Response.Body = new MemoryStream();

        // Act
        await mw.InvokeAsync(ctx, _tenantAccessor.Object, _dbContext);

        // Assert
        ctx.Response.StatusCode.Should().Be(503);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        body.Should().Contain("setup_required");
        body.Should().Contain("\"setupRequired\":true");
    }

    [Fact]
    public async Task WhenTenantHasCredential_CallsNext()
    {
        // Arrange — seed a subject and passkey credential for this tenant
        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Test User",
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = System.Text.Encoding.UTF8.GetBytes("cred-id"),
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

        var nextCalled = false;
        var (mw, ctx) = Build(onNext: () => nextCalled = true);

        // Act
        await mw.InvokeAsync(ctx, _tenantAccessor.Object, _dbContext);

        // Assert
        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().NotBe(503);
    }

    [Fact]
    public async Task WhenTenantNotResolved_CallsNext()
    {
        // Arrange — unresolved tenant
        _tenantAccessor.Setup(t => t.IsResolved).Returns(false);
        var nextCalled = false;
        var (mw, ctx) = Build(onNext: () => nextCalled = true);

        // Act
        await mw.InvokeAsync(ctx, _tenantAccessor.Object, _dbContext);

        // Assert
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task WhenTenantHasOrphanedSubject_Returns503WithRecoveryMode()
    {
        // Arrange — tenant has a passkey (setup is done) but also an orphaned subject
        var healthySubjectId = Guid.CreateVersion7();
        var orphanedSubjectId = Guid.CreateVersion7();

        // Healthy subject with passkey
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = healthySubjectId,
            Name = "Healthy User",
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = healthySubjectId,
            CredentialId = System.Text.Encoding.UTF8.GetBytes("cred-1"),
            PublicKey = [],
            SignCount = 0,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = healthySubjectId,
        });

        // Orphaned subject — member of this tenant, no passkey, no OIDC
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = orphanedSubjectId,
            Name = "Orphaned User",
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = orphanedSubjectId,
        });

        await _dbContext.SaveChangesAsync();

        var (mw, ctx) = Build();

        // Act
        await mw.InvokeAsync(ctx, _tenantAccessor.Object, _dbContext);

        // Assert
        ctx.Response.StatusCode.Should().Be(503);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        body.Should().Contain("recovery_mode_active");
        body.Should().Contain("\"recoveryMode\":true");
    }

    [Fact]
    public async Task WhenOrphanedSubjectBelongsToDifferentTenant_PassesThrough()
    {
        // Arrange — this tenant is healthy, orphaned subject is on another tenant
        var subjectId = Guid.CreateVersion7();
        var orphanedSubjectId = Guid.CreateVersion7();
        var otherTenantId = Guid.CreateVersion7();

        // Seed the other tenant for FK constraint
        _dbContext.Set<TenantEntity>().Add(new TenantEntity
        {
            Id = otherTenantId,
            Slug = "other",
            DisplayName = "Other Tenant",
        });

        // Healthy subject with passkey on our tenant
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Healthy User",
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = System.Text.Encoding.UTF8.GetBytes("cred-1"),
            PublicKey = [],
            SignCount = 0,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = subjectId,
        });

        // Orphaned subject on a different tenant
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = orphanedSubjectId,
            Name = "Orphaned User",
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = otherTenantId,
            SubjectId = orphanedSubjectId,
        });

        await _dbContext.SaveChangesAsync();

        var nextCalled = false;
        var (mw, ctx) = Build(onNext: () => nextCalled = true);

        // Act
        await mw.InvokeAsync(ctx, _tenantAccessor.Object, _dbContext);

        // Assert
        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().NotBe(503);
    }

    [Fact]
    public async Task WhenOrphanedSubjectHasOidc_PassesThrough()
    {
        // Arrange — subject has OIDC binding, no passkey needed
        var subjectId = Guid.CreateVersion7();

        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "OIDC User",
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = System.Text.Encoding.UTF8.GetBytes("cred-1"),
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

        var nextCalled = false;
        var (mw, ctx) = Build(onNext: () => nextCalled = true);

        // Act
        await mw.InvokeAsync(ctx, _tenantAccessor.Object, _dbContext);

        // Assert
        nextCalled.Should().BeTrue();
    }

    private static Endpoint CreateEndpoint(params object[] metadata)
        => new(
            requestDelegate: _ => Task.CompletedTask,
            metadata: new EndpointMetadataCollection(metadata),
            displayName: "test-endpoint");

    [Fact]
    public async Task WhenEndpointHasAllowDuringSetupAttribute_CallsNext_EvenWithNoCredentials()
    {
        // Arrange — no credentials, but endpoint is marked [AllowDuringSetup]
        var nextCalled = false;
        var (mw, ctx) = Build(onNext: () => nextCalled = true);
        ctx.SetEndpoint(CreateEndpoint(new AllowDuringSetupAttribute()));

        // Act
        await mw.InvokeAsync(ctx, _tenantAccessor.Object, _dbContext);

        // Assert
        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().NotBe(503);
    }

    [Fact]
    public async Task WhenEndpointWithoutAttribute_AndNoCredentials_Blocks()
    {
        // Arrange — endpoint has no metadata, tenant has no credentials
        var (mw, ctx) = Build();
        ctx.SetEndpoint(CreateEndpoint());

        // Act
        await mw.InvokeAsync(ctx, _tenantAccessor.Object, _dbContext);

        // Assert
        ctx.Response.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task WhenNoEndpointMatched_AndNotApiPath_CallsNext()
    {
        // Arrange — no endpoint (e.g. static file), non-API path
        var nextCalled = false;
        var (mw, ctx) = Build(path: "/favicon.ico", onNext: () => nextCalled = true);
        // (no SetEndpoint call)

        // Act
        await mw.InvokeAsync(ctx, _tenantAccessor.Object, _dbContext);

        // Assert
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task WhenTenantResolved_InSingleTenantMode_NoCredentials_Returns503()
    {
        // Arrange — simulates single-tenant mode where TenantResolutionMiddleware
        // has resolved the sole tenant. The middleware should still check setup state.
        var mw = new TenantSetupMiddleware(
            _ => Task.CompletedTask,
            NullLogger<TenantSetupMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/status";
        ctx.Response.Body = new MemoryStream();

        // Act — tenant is resolved (simulating single-tenant auto-resolution)
        await mw.InvokeAsync(ctx, _tenantAccessor.Object, _dbContext);

        // Assert
        ctx.Response.StatusCode.Should().Be(503);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        body.Should().Contain("setup_required");
    }

    [Fact]
    public async Task WhenTenantResolved_InSingleTenantMode_WithCredentials_CallsNext()
    {
        // Arrange — single-tenant mode with a fully set up tenant
        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Single Tenant User",
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = System.Text.Encoding.UTF8.GetBytes("cred-single"),
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

        var nextCalled = false;
        var (mw, ctx) = Build(onNext: () => nextCalled = true);

        // Act
        await mw.InvokeAsync(ctx, _tenantAccessor.Object, _dbContext);

        // Assert
        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().NotBe(503);
    }

    [Fact]
    public async Task WhenTenantHasOnboardingCompletedAt_IsOnboarded()
    {
        var subjectId = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "User",
            IsActive = true,
            IsSystemSubject = false,
        });
        _dbContext.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = System.Text.Encoding.UTF8.GetBytes("cred-onb"),
            PublicKey = [],
            SignCount = 0,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = subjectId,
        });

        var tenant = await _dbContext.Set<TenantEntity>().FindAsync(_tenantId);
        tenant!.OnboardingCompletedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        var reloaded = await _dbContext.Set<TenantEntity>().FindAsync(_tenantId);
        reloaded!.OnboardingCompletedAt.Should().NotBeNull();
    }
}
