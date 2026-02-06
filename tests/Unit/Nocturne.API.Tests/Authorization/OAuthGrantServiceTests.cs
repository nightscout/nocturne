using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Unit tests for OAuthGrantService covering grant management,
/// follower grants, scope validation, and ownership checks.
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
    private const string FollowerClientId = KnownOAuthClients.FollowerClientId;

    private readonly Guid _testClientEntityId = Guid.CreateVersion7();
    private readonly Guid _followerClientEntityId = Guid.CreateVersion7();
    private readonly Guid _ownerSubjectId = Guid.CreateVersion7();
    private readonly Guid _followerSubjectId = Guid.CreateVersion7();

    public OAuthGrantServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var dbContext = new NocturneDbContext(_contextOptions);
        dbContext.Database.EnsureCreated();

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
        _mockClientService.Setup(c => c.FindOrCreateClientAsync(
                TestClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthClientInfo
            {
                Id = _testClientEntityId,
                ClientId = TestClientId,
                DisplayName = "Test App",
                IsKnown = false,
            });

        _mockClientService.Setup(c => c.FindOrCreateClientAsync(
                FollowerClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthClientInfo
            {
                Id = _followerClientEntityId,
                ClientId = FollowerClientId,
                DisplayName = "Nocturne Follower",
                IsKnown = true,
            });
    }

    private OAuthGrantService CreateService(NocturneDbContext dbContext)
    {
        return new OAuthGrantService(dbContext, _mockClientService.Object, _mockLogger.Object);
    }

    private NocturneDbContext CreateDbContext()
    {
        return new NocturneDbContext(_contextOptions);
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

    private async Task SeedFollowerClientAsync(NocturneDbContext db)
    {
        db.OAuthClients.Add(new OAuthClientEntity
        {
            Id = _followerClientEntityId,
            ClientId = FollowerClientId,
            DisplayName = "Nocturne Follower",
            IsKnown = true,
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
        Guid? followerSubjectId = null,
        DateTime? revokedAt = null)
    {
        var id = Guid.CreateVersion7();
        db.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = id,
            ClientEntityId = clientEntityId ?? _testClientEntityId,
            SubjectId = subjectId ?? _ownerSubjectId,
            GrantType = grantType,
            Scopes = scopes ?? new List<string> { "entries.read" },
            Label = label,
            FollowerSubjectId = followerSubjectId,
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

    [Fact]
    public async Task GetGrantsForSubjectAsync_IncludesFollowerInfo()
    {
        using var db = CreateDbContext();
        await SeedFollowerClientAsync(db);
        await SeedSubjectAsync(db, _followerSubjectId, "Follower User", "follower@test.com");
        await SeedGrantAsync(db,
            clientEntityId: _followerClientEntityId,
            subjectId: _ownerSubjectId,
            grantType: OAuthGrantTypes.Follower,
            followerSubjectId: _followerSubjectId);

        var service = CreateService(db);
        var grants = await service.GetGrantsForSubjectAsync(_ownerSubjectId);

        Assert.Single(grants);
        Assert.Equal(_followerSubjectId, grants[0].FollowerSubjectId);
        Assert.Equal("Follower User", grants[0].FollowerName);
        Assert.Equal("follower@test.com", grants[0].FollowerEmail);
    }

    // ---------------------------------------------------------------
    // RevokeGrantAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task RevokeGrantAsync_SetsRevokedAt()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
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
    // CreateFollowerGrantAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateFollowerGrantAsync_CreatesWithCorrectGrantType()
    {
        using var db = CreateDbContext();
        await SeedFollowerClientAsync(db);
        await SeedSubjectAsync(db, _followerSubjectId, "Follower");

        var service = CreateService(db);
        var grant = await service.CreateFollowerGrantAsync(
            _ownerSubjectId, _followerSubjectId, new[] { "entries.read" });

        Assert.Equal(OAuthGrantTypes.Follower, grant.GrantType);
    }

    [Fact]
    public async Task CreateFollowerGrantAsync_SetsFollowerSubjectId()
    {
        using var db = CreateDbContext();
        await SeedFollowerClientAsync(db);
        await SeedSubjectAsync(db, _followerSubjectId, "Follower", "follower@test.com");

        var service = CreateService(db);
        var grant = await service.CreateFollowerGrantAsync(
            _ownerSubjectId, _followerSubjectId, new[] { "entries.read" }, "Mum");

        Assert.Equal(_followerSubjectId, grant.FollowerSubjectId);
        Assert.Equal("Follower", grant.FollowerName);
        Assert.Equal("follower@test.com", grant.FollowerEmail);
        Assert.Equal("Mum", grant.Label);
    }

    [Fact]
    public async Task CreateFollowerGrantAsync_RejectsSelfFollow()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateFollowerGrantAsync(
                _ownerSubjectId, _ownerSubjectId, new[] { "entries.read" }));
    }

    [Fact]
    public async Task CreateFollowerGrantAsync_RejectsWriteScopes()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateFollowerGrantAsync(
                _ownerSubjectId, _followerSubjectId,
                new[] { "entries.read", "entries.readwrite" }));
    }

    [Fact]
    public async Task CreateFollowerGrantAsync_RejectsFullAccessScope()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateFollowerGrantAsync(
                _ownerSubjectId, _followerSubjectId, new[] { "*" }));
    }

    [Fact]
    public async Task CreateFollowerGrantAsync_UpdatesExistingGrant()
    {
        using var db = CreateDbContext();
        await SeedFollowerClientAsync(db);
        await SeedSubjectAsync(db, _followerSubjectId, "Follower");
        await SeedGrantAsync(db,
            clientEntityId: _followerClientEntityId,
            subjectId: _ownerSubjectId,
            grantType: OAuthGrantTypes.Follower,
            scopes: new List<string> { "entries.read" },
            followerSubjectId: _followerSubjectId);

        var service = CreateService(db);
        var grant = await service.CreateFollowerGrantAsync(
            _ownerSubjectId, _followerSubjectId,
            new[] { "treatments.read" }, "Updated Label");

        // Should merge scopes
        Assert.Contains("entries.read", grant.Scopes);
        Assert.Contains("treatments.read", grant.Scopes);
        Assert.Equal("Updated Label", grant.Label);

        // Should not create a duplicate
        var count = await db.OAuthGrants.CountAsync(g =>
            g.SubjectId == _ownerSubjectId
            && g.FollowerSubjectId == _followerSubjectId
            && g.RevokedAt == null);
        Assert.Equal(1, count);
    }

    // ---------------------------------------------------------------
    // GetGrantsAsFollowerAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetGrantsAsFollowerAsync_ReturnsGrantsWhereUserIsFollower()
    {
        using var db = CreateDbContext();
        await SeedFollowerClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner", "owner@test.com");
        await SeedGrantAsync(db,
            clientEntityId: _followerClientEntityId,
            subjectId: _ownerSubjectId,
            grantType: OAuthGrantTypes.Follower,
            followerSubjectId: _followerSubjectId);

        var service = CreateService(db);
        var grants = await service.GetGrantsAsFollowerAsync(_followerSubjectId);

        Assert.Single(grants);
        Assert.Equal(_ownerSubjectId, grants[0].SubjectId);
    }

    [Fact]
    public async Task GetGrantsAsFollowerAsync_ExcludesRevokedGrants()
    {
        using var db = CreateDbContext();
        await SeedFollowerClientAsync(db);
        await SeedGrantAsync(db,
            clientEntityId: _followerClientEntityId,
            subjectId: _ownerSubjectId,
            grantType: OAuthGrantTypes.Follower,
            followerSubjectId: _followerSubjectId,
            revokedAt: DateTime.UtcNow);

        var service = CreateService(db);
        var grants = await service.GetGrantsAsFollowerAsync(_followerSubjectId);

        Assert.Empty(grants);
    }

    // ---------------------------------------------------------------
    // GetActiveFollowerGrantAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetActiveFollowerGrantAsync_ReturnsMatchingGrant()
    {
        using var db = CreateDbContext();
        await SeedFollowerClientAsync(db);
        await SeedGrantAsync(db,
            clientEntityId: _followerClientEntityId,
            subjectId: _ownerSubjectId,
            grantType: OAuthGrantTypes.Follower,
            followerSubjectId: _followerSubjectId);

        var service = CreateService(db);
        var grant = await service.GetActiveFollowerGrantAsync(_ownerSubjectId, _followerSubjectId);

        Assert.NotNull(grant);
        Assert.Equal(_ownerSubjectId, grant.SubjectId);
        Assert.Equal(_followerSubjectId, grant.FollowerSubjectId);
    }

    [Fact]
    public async Task GetActiveFollowerGrantAsync_ReturnsNullWhenRevoked()
    {
        using var db = CreateDbContext();
        await SeedFollowerClientAsync(db);
        await SeedGrantAsync(db,
            clientEntityId: _followerClientEntityId,
            subjectId: _ownerSubjectId,
            grantType: OAuthGrantTypes.Follower,
            followerSubjectId: _followerSubjectId,
            revokedAt: DateTime.UtcNow);

        var service = CreateService(db);
        var grant = await service.GetActiveFollowerGrantAsync(_ownerSubjectId, _followerSubjectId);

        Assert.Null(grant);
    }

    // ---------------------------------------------------------------
    // UpdateGrantAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task UpdateGrantAsync_UpdatesLabel()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
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
        var grantId = await SeedGrantAsync(db,
            scopes: new List<string> { "entries.read" });

        var service = CreateService(db);
        var result = await service.UpdateGrantAsync(grantId, _ownerSubjectId,
            scopes: new[] { "entries.read", "treatments.readwrite" });

        Assert.NotNull(result);
        Assert.Contains("entries.read", result.Scopes);
        Assert.Contains("treatments.readwrite", result.Scopes);
    }

    [Fact]
    public async Task UpdateGrantAsync_ReturnsNullForWrongOwner()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        var grantId = await SeedGrantAsync(db);

        var service = CreateService(db);
        var wrongOwnerId = Guid.CreateVersion7();
        var result = await service.UpdateGrantAsync(grantId, wrongOwnerId, label: "Hacked");

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateGrantAsync_RejectsWriteScopesForFollowerGrant()
    {
        using var db = CreateDbContext();
        await SeedFollowerClientAsync(db);
        var grantId = await SeedGrantAsync(db,
            clientEntityId: _followerClientEntityId,
            grantType: OAuthGrantTypes.Follower,
            followerSubjectId: _followerSubjectId);

        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateGrantAsync(grantId, _ownerSubjectId,
                scopes: new[] { "entries.readwrite" }));
    }
}
