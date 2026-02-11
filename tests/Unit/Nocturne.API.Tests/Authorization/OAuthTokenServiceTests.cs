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
/// Unit tests for OAuthTokenService covering authorization code exchange,
/// refresh token rotation, and token reuse detection.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "OAuth")]
public class OAuthTokenServiceTests : IDisposable
{
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;
    private readonly Mock<IJwtService> _mockJwtService;
    private readonly Mock<ISubjectService> _mockSubjectService;
    private readonly Mock<IOAuthGrantService> _mockGrantService;
    private readonly Mock<ILogger<OAuthTokenService>> _mockLogger;

    // Deterministic test values
    private const string TestAccessToken = "test-access-token-jwt";
    private const string TestRefreshToken = "test-refresh-token";
    private const string TestRefreshTokenHash = "test-refresh-token-hash";
    private const string TestNewRefreshToken = "test-new-refresh-token";
    private const string TestNewRefreshTokenHash = "test-new-refresh-token-hash";
    private const string TestCodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string TestRedirectUri = "https://example.com/callback";
    private const string TestClientId = "test-client-id";

    // Pre-computed S256 challenge for TestCodeVerifier
    private static readonly string TestCodeChallenge = PkceValidator.ComputeCodeChallenge(TestCodeVerifier);

    private readonly Guid _testSubjectId = Guid.CreateVersion7();
    private readonly Guid _testClientEntityId = Guid.CreateVersion7();
    private readonly Guid _testGrantId = Guid.CreateVersion7();

    public OAuthTokenServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var dbContext = new NocturneDbContext(_contextOptions);
        dbContext.Database.EnsureCreated();

        _mockJwtService = new Mock<IJwtService>();
        _mockSubjectService = new Mock<ISubjectService>();
        _mockGrantService = new Mock<IOAuthGrantService>();
        _mockLogger = new Mock<ILogger<OAuthTokenService>>();

        SetupDefaultMocks();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private void SetupDefaultMocks()
    {
        _mockJwtService.Setup(j => j.GenerateRefreshToken())
            .Returns(TestRefreshToken);
        _mockJwtService.Setup(j => j.HashRefreshToken(TestRefreshToken))
            .Returns(TestRefreshTokenHash);
        _mockJwtService.Setup(j => j.GenerateAccessToken(
                It.IsAny<SubjectInfo>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<TimeSpan?>()))
            .Returns(TestAccessToken);
        _mockJwtService.Setup(j => j.GetAccessTokenLifetime())
            .Returns(TimeSpan.FromHours(1));

        _mockSubjectService.Setup(s => s.GetSubjectByIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => new Core.Models.Authorization.Subject
            {
                Id = id,
                Name = "Test User",
                Email = "test@example.com",
            });
        _mockSubjectService.Setup(s => s.GetSubjectRolesAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<string> { "readable" });

        _mockGrantService.Setup(g => g.CreateOrUpdateGrantAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid clientEntityId, Guid subjectId, IEnumerable<string> scopes, string _, string? _, bool _, CancellationToken _) =>
                new OAuthGrantInfo
                {
                    Id = _testGrantId,
                    ClientEntityId = clientEntityId,
                    ClientId = TestClientId,
                    SubjectId = subjectId,
                    Scopes = scopes.ToList(),
                });
    }

    private OAuthTokenService CreateService(NocturneDbContext dbContext)
    {
        return new OAuthTokenService(
            dbContext,
            _mockJwtService.Object,
            _mockSubjectService.Object,
            _mockGrantService.Object,
            _mockLogger.Object
        );
    }

    private NocturneDbContext CreateDbContext()
    {
        return new NocturneDbContext(_contextOptions);
    }

    /// <summary>
    /// Seed an OAuthClientEntity into the database and return its entity Id.
    /// </summary>
    private async Task<Guid> SeedClientAsync(NocturneDbContext db, Guid? id = null, string? clientId = null)
    {
        var entityId = id ?? _testClientEntityId;
        var entity = new OAuthClientEntity
        {
            Id = entityId,
            ClientId = clientId ?? TestClientId,
            DisplayName = "Test App",
            IsKnown = false,
            RedirectUris = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.OAuthClients.Add(entity);
        await db.SaveChangesAsync();
        return entityId;
    }

    /// <summary>
    /// Seed a valid (non-expired, non-redeemed) authorization code into the database.
    /// Returns the plain-text code hash that the service will look up.
    /// </summary>
    private async Task SeedAuthorizationCodeAsync(
        NocturneDbContext db,
        string codeHash,
        Guid? clientEntityId = null,
        Guid? subjectId = null,
        string? redirectUri = null,
        string? codeChallenge = null,
        DateTime? expiresAt = null,
        DateTime? redeemedAt = null)
    {
        var entity = new OAuthAuthorizationCodeEntity
        {
            Id = Guid.CreateVersion7(),
            ClientEntityId = clientEntityId ?? _testClientEntityId,
            SubjectId = subjectId ?? _testSubjectId,
            CodeHash = codeHash,
            Scopes = new List<string> { "entries.read" },
            RedirectUri = redirectUri ?? TestRedirectUri,
            CodeChallenge = codeChallenge ?? TestCodeChallenge,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(10),
            RedeemedAt = redeemedAt,
            CreatedAt = DateTime.UtcNow,
        };
        db.OAuthAuthorizationCodes.Add(entity);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seed an OAuth grant entity into the database.
    /// </summary>
    private async Task<Guid> SeedGrantAsync(
        NocturneDbContext db,
        Guid? id = null,
        Guid? clientEntityId = null,
        Guid? subjectId = null,
        DateTime? revokedAt = null)
    {
        var grantId = id ?? _testGrantId;
        var entity = new OAuthGrantEntity
        {
            Id = grantId,
            ClientEntityId = clientEntityId ?? _testClientEntityId,
            SubjectId = subjectId ?? _testSubjectId,
            Scopes = new List<string> { "entries.read" },
            GrantType = "app",
            CreatedAt = DateTime.UtcNow,
            RevokedAt = revokedAt,
        };
        db.OAuthGrants.Add(entity);
        await db.SaveChangesAsync();
        return grantId;
    }

    /// <summary>
    /// Seed an OAuth refresh token entity into the database.
    /// </summary>
    private async Task<Guid> SeedRefreshTokenAsync(
        NocturneDbContext db,
        string tokenHash,
        Guid? grantId = null,
        DateTime? expiresAt = null,
        DateTime? revokedAt = null,
        Guid? replacedById = null)
    {
        var entity = new OAuthRefreshTokenEntity
        {
            Id = Guid.CreateVersion7(),
            GrantId = grantId ?? _testGrantId,
            TokenHash = tokenHash,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(90),
            RevokedAt = revokedAt,
            ReplacedById = replacedById,
        };
        db.OAuthRefreshTokens.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    // ---------------------------------------------------------------
    // ExchangeAuthorizationCodeAsync tests
    // ---------------------------------------------------------------

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_ValidCode_ReturnsTokens()
    {
        // Arrange
        const string testCode = "valid-auth-code";
        const string testCodeHash = "valid-auth-code-hash";

        _mockJwtService.Setup(j => j.HashRefreshToken(testCode))
            .Returns(testCodeHash);

        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedAuthorizationCodeAsync(db, testCodeHash);

        var service = CreateService(db);

        // Act
        var result = await service.ExchangeAuthorizationCodeAsync(
            testCode,
            TestCodeVerifier,
            TestRedirectUri,
            TestClientId);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(TestAccessToken, result.AccessToken);
        Assert.Equal(TestRefreshToken, result.RefreshToken);
        Assert.Equal(3600, result.ExpiresIn);
        Assert.NotNull(result.Scope);
        Assert.Null(result.Error);
        Assert.Null(result.ErrorDescription);

        // Verify the auth code was marked as redeemed
        var redeemed = await db.OAuthAuthorizationCodes.FirstAsync(c => c.CodeHash == testCodeHash);
        Assert.NotNull(redeemed.RedeemedAt);

        // Verify grant creation was called
        _mockGrantService.Verify(g => g.CreateOrUpdateGrantAsync(
            _testClientEntityId,
            _testSubjectId,
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_ExpiredCode_ReturnsError()
    {
        // Arrange
        const string testCode = "expired-auth-code";
        const string testCodeHash = "expired-auth-code-hash";

        _mockJwtService.Setup(j => j.HashRefreshToken(testCode))
            .Returns(testCodeHash);

        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedAuthorizationCodeAsync(db, testCodeHash,
            expiresAt: DateTime.UtcNow.AddMinutes(-5));

        var service = CreateService(db);

        // Act
        var result = await service.ExchangeAuthorizationCodeAsync(
            testCode,
            TestCodeVerifier,
            TestRedirectUri,
            TestClientId);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("invalid_grant", result.Error);
        Assert.Contains("expired", result.ErrorDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_AlreadyRedeemed_ReturnsError()
    {
        // Arrange
        const string testCode = "redeemed-auth-code";
        const string testCodeHash = "redeemed-auth-code-hash";

        _mockJwtService.Setup(j => j.HashRefreshToken(testCode))
            .Returns(testCodeHash);

        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedAuthorizationCodeAsync(db, testCodeHash,
            redeemedAt: DateTime.UtcNow.AddMinutes(-1));

        var service = CreateService(db);

        // Act
        var result = await service.ExchangeAuthorizationCodeAsync(
            testCode,
            TestCodeVerifier,
            TestRedirectUri,
            TestClientId);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("invalid_grant", result.Error);
        Assert.Contains("already been used", result.ErrorDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_InvalidPkce_ReturnsError()
    {
        // Arrange
        const string testCode = "pkce-fail-code";
        const string testCodeHash = "pkce-fail-code-hash";

        _mockJwtService.Setup(j => j.HashRefreshToken(testCode))
            .Returns(testCodeHash);

        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedAuthorizationCodeAsync(db, testCodeHash);

        var service = CreateService(db);

        // Act - use a wrong code_verifier that does not match the stored challenge
        var result = await service.ExchangeAuthorizationCodeAsync(
            testCode,
            "wrong-code-verifier-that-does-not-match",
            TestRedirectUri,
            TestClientId);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("invalid_grant", result.Error);
        Assert.Contains("PKCE", result.ErrorDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_WrongRedirectUri_ReturnsError()
    {
        // Arrange
        const string testCode = "redirect-mismatch-code";
        const string testCodeHash = "redirect-mismatch-code-hash";

        _mockJwtService.Setup(j => j.HashRefreshToken(testCode))
            .Returns(testCodeHash);

        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedAuthorizationCodeAsync(db, testCodeHash);

        var service = CreateService(db);

        // Act
        var result = await service.ExchangeAuthorizationCodeAsync(
            testCode,
            TestCodeVerifier,
            "https://wrong-site.com/callback",
            TestClientId);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("invalid_grant", result.Error);
        Assert.Contains("Redirect URI", result.ErrorDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_WrongClientId_ReturnsError()
    {
        // Arrange
        const string testCode = "client-mismatch-code";
        const string testCodeHash = "client-mismatch-code-hash";

        _mockJwtService.Setup(j => j.HashRefreshToken(testCode))
            .Returns(testCodeHash);

        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedAuthorizationCodeAsync(db, testCodeHash);

        var service = CreateService(db);

        // Act
        var result = await service.ExchangeAuthorizationCodeAsync(
            testCode,
            TestCodeVerifier,
            TestRedirectUri,
            "wrong-client-id");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("invalid_grant", result.Error);
        Assert.Contains("Client ID", result.ErrorDescription, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // RefreshAccessTokenAsync tests
    // ---------------------------------------------------------------

    [Fact]
    public async Task RefreshAccessTokenAsync_ValidToken_RotatesAndReturnsNewTokens()
    {
        // Arrange
        const string oldToken = "old-refresh-token";
        const string oldTokenHash = "old-refresh-token-hash";

        // Set up the hash lookup chain: service hashes the incoming token to find it in the DB
        _mockJwtService.Setup(j => j.HashRefreshToken(oldToken))
            .Returns(oldTokenHash);

        // The service generates a new refresh token after revoking the old one
        _mockJwtService.Setup(j => j.GenerateRefreshToken())
            .Returns(TestNewRefreshToken);
        _mockJwtService.Setup(j => j.HashRefreshToken(TestNewRefreshToken))
            .Returns(TestNewRefreshTokenHash);

        using var db = CreateDbContext();
        await SeedClientAsync(db);
        var grantId = await SeedGrantAsync(db);
        await SeedRefreshTokenAsync(db, oldTokenHash, grantId: grantId);

        var service = CreateService(db);

        // Act
        var result = await service.RefreshAccessTokenAsync(oldToken, TestClientId);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(TestAccessToken, result.AccessToken);
        Assert.Equal(TestNewRefreshToken, result.RefreshToken);
        Assert.Equal(3600, result.ExpiresIn);
        Assert.NotNull(result.Scope);

        // Verify old token was revoked
        var oldTokenEntity = await db.OAuthRefreshTokens.FirstAsync(t => t.TokenHash == oldTokenHash);
        Assert.NotNull(oldTokenEntity.RevokedAt);
        Assert.NotNull(oldTokenEntity.ReplacedById);

        // Verify new token was created
        var newTokenEntity = await db.OAuthRefreshTokens.FirstAsync(t => t.TokenHash == TestNewRefreshTokenHash);
        Assert.Null(newTokenEntity.RevokedAt);
        Assert.Equal(grantId, newTokenEntity.GrantId);

        // Verify grant last-used was updated
        _mockGrantService.Verify(g => g.UpdateLastUsedAsync(
            grantId,
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_ExpiredToken_ReturnsError()
    {
        // Arrange
        const string expiredToken = "expired-refresh-token";
        const string expiredTokenHash = "expired-refresh-token-hash";

        _mockJwtService.Setup(j => j.HashRefreshToken(expiredToken))
            .Returns(expiredTokenHash);

        using var db = CreateDbContext();
        await SeedClientAsync(db);
        var grantId = await SeedGrantAsync(db);
        await SeedRefreshTokenAsync(db, expiredTokenHash,
            grantId: grantId,
            expiresAt: DateTime.UtcNow.AddDays(-1));

        var service = CreateService(db);

        // Act
        var result = await service.RefreshAccessTokenAsync(expiredToken, TestClientId);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("invalid_grant", result.Error);
        Assert.Contains("expired", result.ErrorDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_RevokedToken_ReturnsError()
    {
        // Arrange
        const string revokedToken = "revoked-refresh-token";
        const string revokedTokenHash = "revoked-refresh-token-hash";

        _mockJwtService.Setup(j => j.HashRefreshToken(revokedToken))
            .Returns(revokedTokenHash);

        using var db = CreateDbContext();
        await SeedClientAsync(db);
        var grantId = await SeedGrantAsync(db);
        // Revoked but no replacement: simple revocation, no reuse detection
        await SeedRefreshTokenAsync(db, revokedTokenHash,
            grantId: grantId,
            revokedAt: DateTime.UtcNow.AddMinutes(-5));

        var service = CreateService(db);

        // Act
        var result = await service.RefreshAccessTokenAsync(revokedToken, TestClientId);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("invalid_grant", result.Error);
        Assert.Contains("revoked", result.ErrorDescription, StringComparison.OrdinalIgnoreCase);

        // Grant should NOT have been fully revoked (no reuse detection since no ReplacedById)
        _mockGrantService.Verify(g => g.RevokeGrantAsync(
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_RevokedTokenReuse_RevokesEntireGrant()
    {
        // Arrange: simulate a rotation chain where the old token was already rotated
        // (has ReplacedById set), then someone tries to reuse the old token.
        const string reusedToken = "reused-refresh-token";
        const string reusedTokenHash = "reused-refresh-token-hash";

        _mockJwtService.Setup(j => j.HashRefreshToken(reusedToken))
            .Returns(reusedTokenHash);

        using var db = CreateDbContext();
        await SeedClientAsync(db);
        var grantId = await SeedGrantAsync(db);

        // Create the replacement token first so we have its ID
        var replacementTokenId = await SeedRefreshTokenAsync(db, "replacement-token-hash",
            grantId: grantId);

        // Create the reused token as revoked with a replacement (indicates prior rotation)
        await SeedRefreshTokenAsync(db, reusedTokenHash,
            grantId: grantId,
            revokedAt: DateTime.UtcNow.AddMinutes(-5),
            replacedById: replacementTokenId);

        var service = CreateService(db);

        // Act
        var result = await service.RefreshAccessTokenAsync(reusedToken, TestClientId);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("invalid_grant", result.Error);
        Assert.Contains("revoked", result.ErrorDescription, StringComparison.OrdinalIgnoreCase);

        // Token reuse was detected: the grant should be fully revoked
        _mockGrantService.Verify(g => g.RevokeGrantAsync(
            grantId,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
