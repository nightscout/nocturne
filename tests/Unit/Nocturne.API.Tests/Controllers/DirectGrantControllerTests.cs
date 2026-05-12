using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class DirectGrantControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly NocturneDbContext _dbContext;
    private readonly DirectGrantController _controller;
    private readonly Guid _testTenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    public DirectGrantControllerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId };
        _dbContext.Database.EnsureCreated();

        // Seed required entities for FK constraints
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _testTenantId,
            Slug = "default",
            DisplayName = "Default",
            IsActive = true,
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = _subjectId,
            Name = "Test User",
            IsActive = true,
        });
        _dbContext.SaveChanges();

        var logger = new Mock<ILogger<DirectGrantController>>();
        var auditService = new Mock<IAuthAuditService>();

        _controller = new DirectGrantController(_dbContext, auditService.Object, logger.Object);

        // Set up authenticated HttpContext
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantContext"] = new TenantContext(_testTenantId, "default", "Default", true);
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _subjectId,
            Permissions = ["*"],
        };
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
    public async Task Create_ReturnsTokenWithNocPrefix()
    {
        var request = new CreateDirectGrantRequest
        {
            Label = "Test Token",
            Scopes = ["glucose.read"],
        };

        var result = await _controller.Create(request);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<CreateDirectGrantResponse>(okResult.Value);
        Assert.StartsWith("noc_", response.Token);
        Assert.Equal("Test Token", response.Label);
        Assert.Contains("glucose.read", response.Scopes);
    }

    [Fact]
    public async Task Create_StoresHashedToken()
    {
        var request = new CreateDirectGrantRequest
        {
            Label = "Hash Test",
            Scopes = ["glucose.read"],
        };

        var result = await _controller.Create(request);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<CreateDirectGrantResponse>(okResult.Value);

        // Verify the stored hash matches what we'd compute
        var grant = await _dbContext.OAuthGrants.FirstOrDefaultAsync(g => g.Id == response.Id);
        Assert.NotNull(grant);
        var expectedHash = DirectGrantTokenHandler.ComputeSha256Hex(response.Token);
        Assert.Equal(expectedHash, grant!.TokenHash);
        Assert.Equal(OAuthGrantTypes.Direct, grant.GrantType);
        Assert.Null(grant.ClientEntityId);
    }

    [Fact]
    public async Task Create_EmptyLabel_ReturnsBadRequest()
    {
        var request = new CreateDirectGrantRequest
        {
            Label = "",
            Scopes = ["glucose.read"],
        };

        var result = await _controller.Create(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task Create_NoScopes_ReturnsBadRequest()
    {
        var request = new CreateDirectGrantRequest
        {
            Label = "Test",
            Scopes = [],
        };

        var result = await _controller.Create(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidScopes_ReturnsBadRequest()
    {
        var request = new CreateDirectGrantRequest
        {
            Label = "Test",
            Scopes = ["invalid.scope"],
        };

        var result = await _controller.Create(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task Create_Unauthenticated_ReturnsUnauthorized()
    {
        _controller.ControllerContext.HttpContext.Items["AuthContext"] = null;

        var request = new CreateDirectGrantRequest
        {
            Label = "Test",
            Scopes = ["glucose.read"],
        };

        var result = await _controller.Create(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(401, objectResult.StatusCode);
    }

    [Fact]
    public async Task List_ExcludesRevokedGrants()
    {
        // Seed active and revoked grants
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = _subjectId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = ["glucose.read"],
            Label = "Active",
            TokenHash = "hash1",
            CreatedAt = DateTime.UtcNow,
        });
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = _subjectId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = ["glucose.read"],
            Label = "Revoked",
            TokenHash = "hash2",
            CreatedAt = DateTime.UtcNow,
            RevokedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.List();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var grants = Assert.IsType<List<DirectGrantDto>>(okResult.Value);
        Assert.Single(grants);
        Assert.Equal("Active", grants[0].Label);
    }

    [Fact]
    public async Task List_DoesNotReturnTokenValue()
    {
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = _subjectId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = ["glucose.read"],
            Label = "Test",
            TokenHash = "somehash",
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.List();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var grants = Assert.IsType<List<DirectGrantDto>>(okResult.Value);
        Assert.Single(grants);
        // DirectGrantDto does not have a Token property
        var properties = typeof(DirectGrantDto).GetProperties();
        Assert.DoesNotContain(properties, p => p.Name == "Token");
    }

    [Fact]
    public async Task Revoke_SetsRevokedAt()
    {
        var grantId = Guid.CreateVersion7();
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = grantId,
            SubjectId = _subjectId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = ["glucose.read"],
            Label = "ToRevoke",
            TokenHash = "hashrevoke",
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.Revoke(grantId);

        Assert.IsType<NoContentResult>(result);

        var grant = await _dbContext.OAuthGrants.FirstOrDefaultAsync(g => g.Id == grantId);
        Assert.NotNull(grant);
        Assert.NotNull(grant!.RevokedAt);
    }

    [Fact]
    public async Task Revoke_NonexistentGrant_ReturnsNotFound()
    {
        var result = await _controller.Revoke(Guid.CreateVersion7());

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
    }

    [Fact]
    public async Task Revoke_AlreadyRevoked_ReturnsNoContent()
    {
        var grantId = Guid.CreateVersion7();
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = grantId,
            SubjectId = _subjectId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = ["glucose.read"],
            Label = "AlreadyRevoked",
            TokenHash = "hashalreadyrevoked",
            CreatedAt = DateTime.UtcNow,
            RevokedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.Revoke(grantId);

        Assert.IsType<NoContentResult>(result);
    }
}
