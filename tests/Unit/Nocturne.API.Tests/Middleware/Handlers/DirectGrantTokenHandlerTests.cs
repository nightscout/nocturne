using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Moq;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Middleware.Handlers;

public class DirectGrantTokenHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly Mock<IDbContextFactory<NocturneDbContext>> _dbContextFactory;
    private readonly DirectGrantTokenHandler _handler;

    private readonly Guid _testTenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    public DirectGrantTokenHandlerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.Database.EnsureCreated();

            // Seed required entities for FK constraints
            ctx.Tenants.Add(new Nocturne.Infrastructure.Data.Entities.TenantEntity
            {
                Id = _testTenantId,
                Slug = "default",
                DisplayName = "Default",
                IsActive = true,
            });
            ctx.Subjects.Add(new Nocturne.Infrastructure.Data.Entities.SubjectEntity
            {
                Id = _subjectId,
                Name = "Test User",
                IsActive = true,
            });
            ctx.SaveChanges();
        }

        _dbContextFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        _dbContextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NocturneDbContext(_dbOptions) { TenantId = _testTenantId });

        var logger = new Mock<ILogger<DirectGrantTokenHandler>>();
        _handler = new DirectGrantTokenHandler(_dbContextFactory.Object, logger.Object);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task AuthenticateAsync_NoAuthHeader_ReturnsSkip()
    {
        var context = CreateHttpContext();

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.ShouldSkip);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AuthenticateAsync_NonBearerHeader_ReturnsSkip()
    {
        var context = CreateHttpContext();
        context.Request.Headers.Authorization = "Basic dXNlcjpwYXNz";

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.ShouldSkip);
    }

    [Fact]
    public async Task AuthenticateAsync_JwtFormatToken_ReturnsSkip()
    {
        var context = CreateHttpContext();
        context.Request.Headers.Authorization = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.test.test";

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.ShouldSkip);
    }

    [Fact]
    public async Task AuthenticateAsync_ValidOpaqueToken_ReturnsSuccess()
    {
        var token = "noc_testtoken12345";
        var tokenHash = DirectGrantTokenHandler.ComputeSha256Hex(token);

        // Seed the grant
        await using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = _subjectId,
                GrantType = OAuthGrantTypes.Direct,
                TokenHash = tokenHash,
                Scopes = ["glucose.read", "treatments.read"],
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var context = CreateHttpContext();
        context.Request.Headers.Authorization = $"Bearer {token}";

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.AuthContext);
        Assert.Equal(AuthType.DirectGrant, result.AuthContext!.AuthType);
        Assert.Equal(_subjectId, result.AuthContext.SubjectId);
        Assert.Contains("glucose.read", result.AuthContext.Scopes);
        Assert.Contains("treatments.read", result.AuthContext.Scopes);
    }

    [Fact]
    public async Task AuthenticateAsync_InvalidOpaqueToken_ReturnsSkip()
    {
        var context = CreateHttpContext();
        context.Request.Headers.Authorization = "Bearer noc_nonexistenttoken";

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.ShouldSkip);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AuthenticateAsync_RevokedGrant_ReturnsSkip()
    {
        var token = "noc_revokedtoken123";
        var tokenHash = DirectGrantTokenHandler.ComputeSha256Hex(token);

        // Seed a revoked grant
        await using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = _subjectId,
                GrantType = OAuthGrantTypes.Direct,
                TokenHash = tokenHash,
                Scopes = ["glucose.read"],
                CreatedAt = DateTime.UtcNow,
                RevokedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var context = CreateHttpContext();
        context.Request.Headers.Authorization = $"Bearer {token}";

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.ShouldSkip);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Priority_Is150()
    {
        Assert.Equal(150, _handler.Priority);
    }

    [Fact]
    public void Name_IsDirectGrantTokenHandler()
    {
        Assert.Equal("DirectGrantTokenHandler", _handler.Name);
    }

    private DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Items["TenantContext"] = new TenantContext(_testTenantId, "default", "Default", true);
        return context;
    }
}
