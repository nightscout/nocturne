using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class TenantDirectGrantControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly NocturneDbContext _dbContext;
    private readonly NocturneDbContext _auditDbContext;
    private readonly TenantDirectGrantController _controller;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _memberSubjectId = Guid.CreateVersion7();
    private readonly Guid _nonMemberSubjectId = Guid.CreateVersion7();
    private readonly Guid _adminSubjectId = Guid.CreateVersion7();

    public TenantDirectGrantControllerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(_dbOptions) { TenantId = _tenantId };
        _dbContext.Database.EnsureCreated();

        // Seed required entities for FK constraints
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "default",
            DisplayName = "Default",
            IsActive = true,
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = _memberSubjectId,
            Name = "Member",
            IsActive = true,
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = _nonMemberSubjectId,
            Name = "Outsider",
            IsActive = true,
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = _adminSubjectId,
            Name = "Platform Admin",
            IsActive = true,
            IsPlatformAdmin = true,
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = _memberSubjectId,
        });
        _dbContext.SaveChanges();

        var factory = new Mock<IDbContextFactory<NocturneDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NocturneDbContext(_dbOptions));

        // The audit service runs on the request-scoped context, which on this path carries no
        // tenant — so a recorded tenant can only have come from the grant's own pinned context.
        _auditDbContext = new NocturneDbContext(_dbOptions);
        var httpContext = new DefaultHttpContext();
        var directGrantService = new DirectGrantService(
            new AuthAuditService(
                _auditDbContext,
                new HttpContextAccessor { HttpContext = httpContext },
                new Mock<ILogger<AuthAuditService>>().Object),
            new Mock<ILogger<DirectGrantService>>().Object);
        var tenantMemberService = new TenantMemberService(factory.Object);

        _controller = new TenantDirectGrantController(
            factory.Object, tenantMemberService, directGrantService)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    public void Dispose()
    {
        _auditDbContext.Dispose();
        _dbContext.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Create_MemberSubject_ReturnsTokenBoundToTenant()
    {
        var request = new AdminCreateDirectGrantRequest
        {
            SubjectId = _memberSubjectId,
            Label = "Partner Integration",
            Scopes = ["glucose.read"],
        };

        var result = await _controller.Create(_tenantId, request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<CreateDirectGrantResponse>(okResult.Value);
        Assert.StartsWith("noc_", response.Token);
        Assert.Equal("Partner Integration", response.Label);
        Assert.Contains("glucose.read", response.Scopes);

        var grant = await _dbContext.OAuthGrants.FirstOrDefaultAsync(g => g.Id == response.Id);
        Assert.NotNull(grant);
        Assert.Equal(_tenantId, grant!.TenantId);
        Assert.Equal(_memberSubjectId, grant.SubjectId);
        Assert.Equal(OAuthGrantTypes.Direct, grant.GrantType);
    }

    [Fact]
    public async Task Create_StoredHashesMatchReturnedPlaintext()
    {
        var request = new AdminCreateDirectGrantRequest
        {
            SubjectId = _memberSubjectId,
            Label = "Hash Test",
            Scopes = ["glucose.read"],
        };

        var result = await _controller.Create(_tenantId, request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<CreateDirectGrantResponse>(okResult.Value);

        var grant = await _dbContext.OAuthGrants.FirstOrDefaultAsync(g => g.Id == response.Id);
        Assert.NotNull(grant);
        Assert.Equal(DirectGrantTokenHandler.ComputeSha256Hex(response.Token), grant!.TokenHash);
        Assert.Equal(HashUtils.Sha1Hex(response.Token), grant.LegacySecretHash);
    }

    [Fact]
    public async Task Create_SubjectNotMemberOfTenant_ReturnsBadRequest()
    {
        var request = new AdminCreateDirectGrantRequest
        {
            SubjectId = _nonMemberSubjectId,
            Label = "Should Not Exist",
            Scopes = ["glucose.read"],
        };

        var result = await _controller.Create(_tenantId, request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.False(await _dbContext.OAuthGrants.AnyAsync());
    }

    [Fact]
    public async Task Create_InvalidScopes_ReturnsBadRequest()
    {
        var request = new AdminCreateDirectGrantRequest
        {
            SubjectId = _memberSubjectId,
            Label = "Bad Scopes",
            Scopes = ["invalid.scope"],
        };

        var result = await _controller.Create(_tenantId, request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public void Controller_RequiresPlatformAdminRole()
    {
        var authorize = typeof(TenantDirectGrantController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ToList();

        Assert.Contains(authorize, a => a.Roles == "platform_admin");
    }

    [Fact]
    public async Task List_ReturnsGrantsAcrossSubjects_ExcludingRevoked()
    {
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = _memberSubjectId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = ["glucose.read"],
            Label = "Member Grant",
            TokenHash = "hash1",
            CreatedAt = DateTime.UtcNow,
        });
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = _nonMemberSubjectId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = ["glucose.read"],
            Label = "Other Subject Grant",
            TokenHash = "hash2",
            CreatedAt = DateTime.UtcNow,
        });
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = _memberSubjectId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = ["glucose.read"],
            Label = "Revoked Grant",
            TokenHash = "hash3",
            CreatedAt = DateTime.UtcNow,
            RevokedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.List(_tenantId, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var grants = Assert.IsType<List<DirectGrantDto>>(okResult.Value);
        Assert.Equal(2, grants.Count);
        Assert.DoesNotContain(grants, g => g.Label == "Revoked Grant");
        Assert.Contains(grants, g => g.SubjectId == _nonMemberSubjectId);
    }

    [Fact]
    public async Task List_ExcludesGrantsBelongingToAnotherTenant()
    {
        var (_, otherGrantId) = await SeedGrantOnOtherTenantAsync();

        var result = await _controller.List(_tenantId, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var grants = Assert.IsType<List<DirectGrantDto>>(okResult.Value);
        Assert.DoesNotContain(grants, g => g.Id == otherGrantId);
    }

    [Fact]
    public async Task Revoke_GrantOnAnotherTenant_ReturnsNotFoundAndLeavesItActive()
    {
        var (otherTenantId, otherGrantId) = await SeedGrantOnOtherTenantAsync();

        var result = await _controller.Revoke(_tenantId, otherGrantId, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);

        await using var otherContext = new NocturneDbContext(_dbOptions) { TenantId = otherTenantId };
        var grant = await otherContext.OAuthGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == otherGrantId);
        Assert.NotNull(grant);
        Assert.Null(grant!.RevokedAt);
    }

    /// <summary>
    /// Creates a second tenant carrying one active direct grant, so a test can assert the
    /// controller's reach stops at the tenant named in the route.
    /// </summary>
    private async Task<(Guid TenantId, Guid GrantId)> SeedGrantOnOtherTenantAsync()
    {
        var otherTenantId = Guid.CreateVersion7();
        var grantId = Guid.CreateVersion7();

        await using var context = new NocturneDbContext(_dbOptions) { TenantId = otherTenantId };
        context.Tenants.Add(new TenantEntity
        {
            Id = otherTenantId,
            Slug = "other",
            DisplayName = "Other",
            IsActive = true,
        });
        context.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = grantId,
            SubjectId = _memberSubjectId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = ["glucose.read"],
            Label = "Other Tenant Grant",
            TokenHash = "hashother",
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        return (otherTenantId, grantId);
    }

    [Fact]
    public async Task Revoke_SetsRevokedAt()
    {
        var grantId = Guid.CreateVersion7();
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = grantId,
            SubjectId = _memberSubjectId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = ["glucose.read"],
            Label = "ToRevoke",
            TokenHash = "hashrevoke",
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.Revoke(_tenantId, grantId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);

        var grant = await _dbContext.OAuthGrants.AsNoTracking().FirstOrDefaultAsync(g => g.Id == grantId);
        Assert.NotNull(grant);
        Assert.NotNull(grant!.RevokedAt);
    }

    [Fact]
    public async Task Revoke_NonexistentGrant_ReturnsNotFound()
    {
        var result = await _controller.Revoke(_tenantId, Guid.CreateVersion7(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
    }

    [Fact]
    public async Task Create_ByPlatformAdmin_FilesRowWhoseActorIsTheAdminAndSubjectIsTheGrantee()
    {
        AuthenticateAs(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _adminSubjectId,
            IsPlatformAdmin = true,
        });

        await CreateGrantAsync("Admin Minted");

        var row = await SingleAuditRowAsync();
        Assert.Equal(AuthAuditEventType.PlatformAdminGrantIssued, row.EventType);
        Assert.Equal(_memberSubjectId, row.SubjectId);
        Assert.Equal(_adminSubjectId, row.ActorSubjectId);
        Assert.NotEqual(row.SubjectId, row.ActorSubjectId);
        Assert.Equal(_tenantId, row.TenantId);
    }

    [Fact]
    public async Task Revoke_ByPlatformAdmin_FilesRowWhoseActorIsTheAdminAndSubjectIsTheGrantee()
    {
        AuthenticateAs(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _adminSubjectId,
            IsPlatformAdmin = true,
        });

        var grantId = await CreateGrantAsync("Admin Revoked");
        await _controller.Revoke(_tenantId, grantId, CancellationToken.None);

        var row = await _auditDbContext.AuthAuditLog.AsNoTracking()
            .SingleAsync(a => a.EventType == AuthAuditEventType.PlatformAdminGrantRevoked);
        Assert.Equal(_memberSubjectId, row.SubjectId);
        Assert.Equal(_adminSubjectId, row.ActorSubjectId);
        Assert.NotEqual(row.SubjectId, row.ActorSubjectId);
        Assert.Equal(_tenantId, row.TenantId);
    }

    [Fact]
    public async Task Create_ByInstanceKeyCaller_FilesRowIdentifyingTheKeyWithoutASubject()
    {
        const string fingerprint = "0123456789abcdef";
        AuthenticateAs(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.InstanceKey,
            SubjectId = null,
            IsPlatformAdmin = true,
            CredentialFingerprint = fingerprint,
        });

        await CreateGrantAsync("Provisioner Token");

        var row = await SingleAuditRowAsync();
        Assert.Equal(AuthAuditEventType.PlatformAdminGrantIssued, row.EventType);
        Assert.Equal(_memberSubjectId, row.SubjectId);
        Assert.Null(row.ActorSubjectId);
        Assert.Equal($"{AuthType.InstanceKey}:{fingerprint}", row.ActorCredential);
        Assert.Equal(_tenantId, row.TenantId);
    }

    private void AuthenticateAs(AuthContext auth) =>
        _controller.HttpContext.Items["AuthContext"] = auth;

    private async Task<Guid> CreateGrantAsync(string label)
    {
        var result = await _controller.Create(
            _tenantId,
            new AdminCreateDirectGrantRequest
            {
                SubjectId = _memberSubjectId,
                Label = label,
                Scopes = ["glucose.read"],
            },
            CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<CreateDirectGrantResponse>(okResult.Value).Id;
    }

    private async Task<AuthAuditLogEntity> SingleAuditRowAsync() =>
        await _auditDbContext.AuthAuditLog.AsNoTracking().SingleAsync();
}
