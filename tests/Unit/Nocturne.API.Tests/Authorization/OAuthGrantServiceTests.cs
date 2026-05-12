using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Unit tests for OAuthGrantService covering grant management,
/// scope updates, and ownership checks.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "OAuth")]
public class OAuthGrantServiceTests : IDisposable
{
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;
    private readonly Mock<IOAuthClientService> _mockClientService;
    private readonly Mock<ILogger<OAuthGrantService>> _mockLogger;

    private const string TestClientId = "test-client-id";

    private readonly Guid _testTenantId = Guid.CreateVersion7();
    private readonly Guid _testClientEntityId = Guid.CreateVersion7();
    private readonly Guid _ownerSubjectId = Guid.CreateVersion7();

    public OAuthGrantServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var dbContext = new NocturneDbContext(_contextOptions);
        dbContext.Database.EnsureCreated();
        dbContext.Tenants.Add(new TenantEntity { Id = _testTenantId, Slug = "test" });
        dbContext.SaveChanges();

        _mockClientService = new Mock<IOAuthClientService>();
        _mockLogger = new Mock<ILogger<OAuthGrantService>>();

        SetupDefaultMocks();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private void SetupDefaultMocks()
    {
        _mockClientService.Setup(c => c.GetClientAsync(
                TestClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthClientInfo
            {
                Id = _testClientEntityId,
                ClientId = TestClientId,
                DisplayName = "Test App",
                IsKnown = false,
            });
    }

    private OAuthGrantService CreateService(NocturneDbContext dbContext)
    {
        return new OAuthGrantService(dbContext, _mockClientService.Object, _mockLogger.Object);
    }

    private NocturneDbContext CreateDbContext()
    {
        return new NocturneDbContext(_contextOptions) { TenantId = _testTenantId };
    }

    private async Task SeedClientAsync(NocturneDbContext db, Guid? id = null, string? clientId = null)
    {
        db.OAuthClients.Add(new OAuthClientEntity
        {
            Id = id ?? _testClientEntityId,
            ClientId = clientId ?? TestClientId,
            DisplayName = "Test App",
            IsKnown = false,
            RedirectUris = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedSubjectAsync(NocturneDbContext db, Guid id, string name, string? email = null)
    {
        db.Subjects.Add(new SubjectEntity
        {
            Id = id,
            Name = name,
            Email = email,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedGrantAsync(
        NocturneDbContext db,
        Guid? clientEntityId = null,
        Guid? subjectId = null,
        string grantType = "app",
        List<string>? scopes = null,
        string? label = null,
        DateTime? revokedAt = null)
    {
        var id = Guid.CreateVersion7();
        db.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = id,
            ClientEntityId = clientEntityId ?? _testClientEntityId,
            SubjectId = subjectId ?? _ownerSubjectId,
            GrantType = grantType,
            Scopes = scopes ?? new List<string> { "glucose.read" },
            Label = label,
            RevokedAt = revokedAt,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ---------------------------------------------------------------
    // GetGrantsForSubjectAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetGrantsForSubjectAsync_ReturnsOnlyActiveGrants()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        await SeedGrantAsync(db, subjectId: _ownerSubjectId);
        await SeedGrantAsync(db, subjectId: _ownerSubjectId, revokedAt: DateTime.UtcNow);

        var service = CreateService(db);
        var grants = await service.GetGrantsForSubjectAsync(_ownerSubjectId);

        Assert.Single(grants);
    }

    [Fact]
    public async Task GetGrantsForSubjectAsync_ReturnsEmptyWhenNoGrants()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);
        var grants = await service.GetGrantsForSubjectAsync(Guid.CreateVersion7());

        Assert.Empty(grants);
    }

    // ---------------------------------------------------------------
    // RevokeGrantAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task RevokeGrantAsync_SetsRevokedAt()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db);

        var service = CreateService(db);
        await service.RevokeGrantAsync(grantId);

        var entity = await db.OAuthGrants.FirstAsync(g => g.Id == grantId);
        Assert.NotNull(entity.RevokedAt);
    }

    [Fact]
    public async Task RevokeGrantAsync_CascadesToRefreshTokens()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db);

        db.OAuthRefreshTokens.Add(new OAuthRefreshTokenEntity
        {
            Id = Guid.CreateVersion7(),
            GrantId = grantId,
            TokenHash = "test-hash-1",
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(90),
        });
        db.OAuthRefreshTokens.Add(new OAuthRefreshTokenEntity
        {
            Id = Guid.CreateVersion7(),
            GrantId = grantId,
            TokenHash = "test-hash-2",
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(90),
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.RevokeGrantAsync(grantId);

        var tokens = await db.OAuthRefreshTokens.Where(t => t.GrantId == grantId).ToListAsync();
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
    }

    // ---------------------------------------------------------------
    // UpdateGrantAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task UpdateGrantAsync_UpdatesLabel()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db, label: "Old Label");

        var service = CreateService(db);
        var result = await service.UpdateGrantAsync(grantId, _ownerSubjectId, label: "New Label");

        Assert.NotNull(result);
        Assert.Equal("New Label", result.Label);
    }

    [Fact]
    public async Task UpdateGrantAsync_UpdatesScopes()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db,
            scopes: new List<string> { "glucose.read" });

        var service = CreateService(db);
        var result = await service.UpdateGrantAsync(grantId, _ownerSubjectId,
            scopes: new[] { "glucose.read", "treatments.readwrite" });

        Assert.NotNull(result);
        Assert.Contains("glucose.read", result.Scopes);
        Assert.Contains("treatments.readwrite", result.Scopes);
    }

    [Fact]
    public async Task UpdateGrantAsync_ReturnsNullForWrongOwner()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db);

        var service = CreateService(db);
        var wrongOwnerId = Guid.CreateVersion7();
        var result = await service.UpdateGrantAsync(grantId, wrongOwnerId, label: "Hacked");

        Assert.Null(result);
    }
}
