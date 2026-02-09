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
/// Unit tests for OAuthDeviceCodeService covering device code creation,
/// user code lookup, normalization, approval, and denial flows.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "OAuth")]
public class OAuthDeviceCodeServiceTests : IDisposable
{
    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _contextOptions;
    private readonly Mock<IJwtService> _mockJwtService;
    private readonly Mock<IOAuthClientService> _mockClientService;
    private readonly Mock<IOAuthGrantService> _mockGrantService;
    private readonly Mock<ILogger<OAuthDeviceCodeService>> _mockLogger;

    // Deterministic test values
    private const string TestDeviceCode = "test-device-code";
    private const string TestDeviceCodeHash = "test-device-code-hash";
    private const string TestClientId = "test-client-id";

    private readonly Guid _testClientEntityId = Guid.CreateVersion7();
    private readonly Guid _testSubjectId = Guid.CreateVersion7();
    private readonly Guid _testGrantId = Guid.CreateVersion7();

    public OAuthDeviceCodeServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var dbContext = new NocturneDbContext(_contextOptions);
        dbContext.Database.EnsureCreated();

        _mockJwtService = new Mock<IJwtService>();
        _mockClientService = new Mock<IOAuthClientService>();
        _mockGrantService = new Mock<IOAuthGrantService>();
        _mockLogger = new Mock<ILogger<OAuthDeviceCodeService>>();

        SetupDefaultMocks();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private void SetupDefaultMocks()
    {
        _mockJwtService.Setup(j => j.GenerateRefreshToken())
            .Returns(TestDeviceCode);
        _mockJwtService.Setup(j => j.HashRefreshToken(TestDeviceCode))
            .Returns(TestDeviceCodeHash);

        _mockClientService.Setup(c => c.FindOrCreateClientAsync(
                TestClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthClientInfo
            {
                Id = _testClientEntityId,
                ClientId = TestClientId,
                DisplayName = "Test App",
                IsKnown = false,
            });

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

    private OAuthDeviceCodeService CreateService(NocturneDbContext dbContext)
    {
        return new OAuthDeviceCodeService(
            dbContext,
            _mockJwtService.Object,
            _mockClientService.Object,
            _mockGrantService.Object,
            _mockLogger.Object
        );
    }

    private NocturneDbContext CreateDbContext()
    {
        return new NocturneDbContext(_contextOptions);
    }

    /// <summary>
    /// Seed an OAuthDeviceCodeEntity into the database.
    /// </summary>
    private async Task SeedDeviceCodeAsync(
        NocturneDbContext db,
        string? userCode = null,
        string? clientId = null,
        string? deviceCodeHash = null,
        List<string>? scopes = null,
        DateTime? expiresAt = null,
        DateTime? approvedAt = null,
        DateTime? deniedAt = null,
        Guid? grantId = null,
        Guid? subjectId = null)
    {
        var entity = new OAuthDeviceCodeEntity
        {
            Id = Guid.CreateVersion7(),
            ClientId = clientId ?? TestClientId,
            DeviceCodeHash = deviceCodeHash ?? TestDeviceCodeHash,
            UserCode = userCode ?? "ABCD1234",
            Scopes = scopes ?? new List<string> { "entries.read" },
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(15),
            ApprovedAt = approvedAt,
            DeniedAt = deniedAt,
            GrantId = grantId,
            SubjectId = subjectId,
            Interval = 5,
        };
        db.OAuthDeviceCodes.Add(entity);
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------
    // CreateDeviceCodeAsync tests
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateDeviceCodeAsync_ValidRequest_ReturnsDeviceAndUserCode()
    {
        // Arrange
        using var db = CreateDbContext();
        var service = CreateService(db);

        // Act
        var result = await service.CreateDeviceCodeAsync(
            TestClientId,
            new List<string> { "entries.read" });

        // Assert
        Assert.Equal(TestDeviceCode, result.DeviceCode);
        Assert.NotNull(result.UserCode);
        Assert.NotEmpty(result.UserCode);
        Assert.Equal(900, result.ExpiresIn); // 15 minutes * 60 seconds
        Assert.Equal(5, result.Interval);

        // Verify entity was persisted with the hashed device code
        var entity = await db.OAuthDeviceCodes
            .FirstOrDefaultAsync(d => d.DeviceCodeHash == TestDeviceCodeHash);
        Assert.NotNull(entity);
        Assert.Equal(TestClientId, entity.ClientId);
        Assert.Contains("entries.read", entity.Scopes);
        Assert.True(entity.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task CreateDeviceCodeAsync_UserCodeFormat_IsHumanReadable()
    {
        // Arrange
        using var db = CreateDbContext();
        var service = CreateService(db);

        // Act
        var result = await service.CreateDeviceCodeAsync(
            TestClientId,
            new List<string> { "entries.read" });

        // Assert - verify XXXX-YYYY format using only the reduced alphabet
        // Alphabet: BCDFGHJKMNPQRSTVWXYZ23456789 (no vowels, no ambiguous chars)
        Assert.Matches(
            @"^[BCDFGHJKMNPQRSTVWXYZ23456789]{4}-[BCDFGHJKMNPQRSTVWXYZ23456789]{4}$",
            result.UserCode);
    }

    // ---------------------------------------------------------------
    // GetByUserCodeAsync tests
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetByUserCodeAsync_ExistingCode_ReturnsInfo()
    {
        // Arrange
        using var db = CreateDbContext();
        await SeedDeviceCodeAsync(db, userCode: "TESTCD01",
            scopes: new List<string> { "entries.read", "treatments.readwrite" });

        var service = CreateService(db);

        // Act
        var info = await service.GetByUserCodeAsync("TESTCD01");

        // Assert
        Assert.NotNull(info);
        Assert.Equal("TESTCD01", info.UserCode);
        Assert.Equal(TestClientId, info.ClientId);
        Assert.Equal("Test App", info.ClientDisplayName);
        Assert.False(info.IsKnownClient);
        Assert.Contains("entries.read", info.Scopes);
        Assert.Contains("treatments.readwrite", info.Scopes);
        Assert.False(info.IsExpired);
        Assert.False(info.IsApproved);
        Assert.False(info.IsDenied);
    }

    [Fact]
    public async Task GetByUserCodeAsync_NonExistentCode_ReturnsNull()
    {
        // Arrange
        using var db = CreateDbContext();
        var service = CreateService(db);

        // Act
        var info = await service.GetByUserCodeAsync("NOTEXIST");

        // Assert
        Assert.Null(info);
    }

    [Fact]
    public async Task GetByUserCodeAsync_NormalizesInput()
    {
        // Arrange - store with normalized (uppercase, no separator) form
        using var db = CreateDbContext();
        await SeedDeviceCodeAsync(db, userCode: "ABCD1234");

        var service = CreateService(db);

        // Act - look up with lowercase and hyphen
        var info = await service.GetByUserCodeAsync("abcd-1234");

        // Assert
        Assert.NotNull(info);
        Assert.Equal("ABCD1234", info.UserCode);
    }

    // ---------------------------------------------------------------
    // ApproveDeviceCodeAsync tests
    // ---------------------------------------------------------------

    [Fact]
    public async Task ApproveDeviceCodeAsync_ValidPendingCode_SetsApprovedAndCreatesGrant()
    {
        // Arrange
        using var db = CreateDbContext();
        await SeedDeviceCodeAsync(db, userCode: "PNDNG001");

        var service = CreateService(db);

        // Act
        var result = await service.ApproveDeviceCodeAsync("PNDNG001", _testSubjectId);

        // Assert
        Assert.True(result);

        // Verify entity was updated
        var entity = await db.OAuthDeviceCodes
            .FirstAsync(d => d.UserCode == "PNDNG001");
        Assert.NotNull(entity.ApprovedAt);
        Assert.Equal(_testGrantId, entity.GrantId);
        Assert.Equal(_testSubjectId, entity.SubjectId);

        // Verify grant creation was called with correct arguments
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
    public async Task ApproveDeviceCodeAsync_ExpiredCode_ReturnsFalse()
    {
        // Arrange
        using var db = CreateDbContext();
        await SeedDeviceCodeAsync(db, userCode: "EXPRD001",
            expiresAt: DateTime.UtcNow.AddMinutes(-5));

        var service = CreateService(db);

        // Act
        var result = await service.ApproveDeviceCodeAsync("EXPRD001", _testSubjectId);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ApproveDeviceCodeAsync_AlreadyApproved_ReturnsFalse()
    {
        // Arrange
        using var db = CreateDbContext();
        await SeedDeviceCodeAsync(db, userCode: "APRVD001",
            approvedAt: DateTime.UtcNow.AddMinutes(-1));

        var service = CreateService(db);

        // Act
        var result = await service.ApproveDeviceCodeAsync("APRVD001", _testSubjectId);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ApproveDeviceCodeAsync_AlreadyDenied_ReturnsFalse()
    {
        // Arrange
        using var db = CreateDbContext();
        await SeedDeviceCodeAsync(db, userCode: "DEND0001",
            deniedAt: DateTime.UtcNow.AddMinutes(-1));

        var service = CreateService(db);

        // Act
        var result = await service.ApproveDeviceCodeAsync("DEND0001", _testSubjectId);

        // Assert
        Assert.False(result);
    }

    // ---------------------------------------------------------------
    // DenyDeviceCodeAsync tests
    // ---------------------------------------------------------------

    [Fact]
    public async Task DenyDeviceCodeAsync_ValidPendingCode_SetsDenied()
    {
        // Arrange
        using var db = CreateDbContext();
        await SeedDeviceCodeAsync(db, userCode: "DENY0001");

        var service = CreateService(db);

        // Act
        var result = await service.DenyDeviceCodeAsync("DENY0001");

        // Assert
        Assert.True(result);

        // Verify entity was updated
        var entity = await db.OAuthDeviceCodes
            .FirstAsync(d => d.UserCode == "DENY0001");
        Assert.NotNull(entity.DeniedAt);
    }

    [Fact]
    public async Task DenyDeviceCodeAsync_ExpiredCode_ReturnsFalse()
    {
        // Arrange
        using var db = CreateDbContext();
        await SeedDeviceCodeAsync(db, userCode: "DNYEX001",
            expiresAt: DateTime.UtcNow.AddMinutes(-5));

        var service = CreateService(db);

        // Act
        var result = await service.DenyDeviceCodeAsync("DNYEX001");

        // Assert
        Assert.False(result);
    }
}

/// <summary>
/// Unit tests for OAuthTokenService.ExchangeDeviceCodeAsync covering device code
/// polling, approval states, slow-down enforcement, and token minting.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "OAuth")]
public class OAuthDeviceCodeExchangeTests : IDisposable
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
    private const string TestDeviceCode = "test-device-code";
    private const string TestDeviceCodeHash = "test-device-code-hash";
    private const string TestClientId = "test-client-id";

    private readonly Guid _testClientEntityId = Guid.CreateVersion7();
    private readonly Guid _testSubjectId = Guid.CreateVersion7();
    private readonly Guid _testGrantId = Guid.CreateVersion7();

    public OAuthDeviceCodeExchangeTests()
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
        _mockJwtService.Setup(j => j.HashRefreshToken(TestDeviceCode))
            .Returns(TestDeviceCodeHash);
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
    /// Seed an OAuth grant entity into the database.
    /// </summary>
    private async Task<Guid> SeedGrantAsync(
        NocturneDbContext db,
        Guid? id = null,
        Guid? clientEntityId = null,
        Guid? subjectId = null,
        List<string>? scopes = null,
        DateTime? revokedAt = null)
    {
        var grantId = id ?? _testGrantId;
        var entity = new OAuthGrantEntity
        {
            Id = grantId,
            ClientEntityId = clientEntityId ?? _testClientEntityId,
            SubjectId = subjectId ?? _testSubjectId,
            Scopes = scopes ?? new List<string> { "entries.read" },
            GrantType = "app",
            CreatedAt = DateTime.UtcNow,
            RevokedAt = revokedAt,
        };
        db.OAuthGrants.Add(entity);
        await db.SaveChangesAsync();
        return grantId;
    }

    /// <summary>
    /// Seed an OAuthDeviceCodeEntity into the database.
    /// </summary>
    private async Task SeedDeviceCodeAsync(
        NocturneDbContext db,
        string? deviceCodeHash = null,
        string? userCode = null,
        string? clientId = null,
        List<string>? scopes = null,
        DateTime? expiresAt = null,
        DateTime? approvedAt = null,
        DateTime? deniedAt = null,
        Guid? grantId = null,
        Guid? subjectId = null,
        int interval = 5,
        DateTime? lastPolledAt = null)
    {
        var entity = new OAuthDeviceCodeEntity
        {
            Id = Guid.CreateVersion7(),
            ClientId = clientId ?? TestClientId,
            DeviceCodeHash = deviceCodeHash ?? TestDeviceCodeHash,
            UserCode = userCode ?? "ABCD1234",
            Scopes = scopes ?? new List<string> { "entries.read" },
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(15),
            ApprovedAt = approvedAt,
            DeniedAt = deniedAt,
            GrantId = grantId,
            SubjectId = subjectId,
            Interval = interval,
            LastPolledAt = lastPolledAt,
        };
        db.OAuthDeviceCodes.Add(entity);
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------
    // ExchangeDeviceCodeAsync tests
    // ---------------------------------------------------------------

    [Fact]
    public async Task ExchangeDeviceCodeAsync_ApprovedCode_ReturnsTokens()
    {
        // Arrange - seed client, grant, and an approved device code linked to the grant
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        var grantId = await SeedGrantAsync(db);
        await SeedDeviceCodeAsync(db,
            approvedAt: DateTime.UtcNow.AddMinutes(-1),
            grantId: grantId,
            subjectId: _testSubjectId);

        var service = CreateService(db);

        // Act
        var result = await service.ExchangeDeviceCodeAsync(TestDeviceCode, TestClientId);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(TestAccessToken, result.AccessToken);
        Assert.Equal(TestRefreshToken, result.RefreshToken);
        Assert.Equal(3600, result.ExpiresIn);
        Assert.NotNull(result.Scope);
        Assert.Null(result.Error);
        Assert.Null(result.ErrorDescription);

        // Verify a refresh token was persisted
        var refreshToken = await db.OAuthRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == TestRefreshTokenHash);
        Assert.NotNull(refreshToken);
        Assert.Equal(grantId, refreshToken.GrantId);

        // Verify grant last-used was updated
        _mockGrantService.Verify(g => g.UpdateLastUsedAsync(
            grantId,
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExchangeDeviceCodeAsync_PendingCode_ReturnsAuthorizationPending()
    {
        // Arrange - seed a device code with no approval or denial
        using var db = CreateDbContext();
        await SeedDeviceCodeAsync(db);

        var service = CreateService(db);

        // Act
        var result = await service.ExchangeDeviceCodeAsync(TestDeviceCode, TestClientId);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("authorization_pending", result.Error);
        Assert.Contains("pending", result.ErrorDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExchangeDeviceCodeAsync_DeniedCode_ReturnsAccessDenied()
    {
        // Arrange
        using var db = CreateDbContext();
        await SeedDeviceCodeAsync(db,
            deniedAt: DateTime.UtcNow.AddMinutes(-1));

        var service = CreateService(db);

        // Act
        var result = await service.ExchangeDeviceCodeAsync(TestDeviceCode, TestClientId);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("access_denied", result.Error);
        Assert.Contains("denied", result.ErrorDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExchangeDeviceCodeAsync_ExpiredCode_ReturnsExpiredToken()
    {
        // Arrange
        using var db = CreateDbContext();
        await SeedDeviceCodeAsync(db,
            expiresAt: DateTime.UtcNow.AddMinutes(-5));

        var service = CreateService(db);

        // Act
        var result = await service.ExchangeDeviceCodeAsync(TestDeviceCode, TestClientId);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("expired_token", result.Error);
        Assert.Contains("expired", result.ErrorDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExchangeDeviceCodeAsync_TooFastPolling_ReturnsSlowDown()
    {
        // Arrange - seed with LastPolledAt set to now (within the 5-second interval)
        using var db = CreateDbContext();
        await SeedDeviceCodeAsync(db,
            lastPolledAt: DateTime.UtcNow,
            interval: 5);

        var service = CreateService(db);

        // Act
        var result = await service.ExchangeDeviceCodeAsync(TestDeviceCode, TestClientId);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("slow_down", result.Error);

        // Verify interval was increased by 5 seconds per RFC 8628
        var entity = await db.OAuthDeviceCodes
            .FirstAsync(d => d.DeviceCodeHash == TestDeviceCodeHash);
        Assert.Equal(10, entity.Interval); // 5 + 5
    }

    [Fact]
    public async Task ExchangeDeviceCodeAsync_WrongClientId_ReturnsError()
    {
        // Arrange
        using var db = CreateDbContext();
        await SeedDeviceCodeAsync(db);

        var service = CreateService(db);

        // Act - use a client_id that does not match the stored one
        var result = await service.ExchangeDeviceCodeAsync(TestDeviceCode, "wrong-client-id");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("invalid_grant", result.Error);
        Assert.Contains("Client ID", result.ErrorDescription, StringComparison.OrdinalIgnoreCase);
    }
}
