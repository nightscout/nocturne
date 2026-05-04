using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Unit tests for RefreshTokenService covering token creation, validation,
/// rotation (including theft detection), revocation, and pruning.
/// </summary>
public class RefreshTokenServiceTests
{
    private readonly Mock<IFirstPartyTokenRepository> _repository = new();
    private readonly Mock<IJwtService> _jwtService = new();
    private readonly JwtOptions _options = new() { RefreshTokenLifetimeDays = 7 };
    private readonly Guid _subjectId = Guid.CreateVersion7();

    private RefreshTokenService CreateService() =>
        new(
            _repository.Object,
            _jwtService.Object,
            Options.Create(_options),
            NullLogger<RefreshTokenService>.Instance);

    #region CreateRefreshTokenAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateRefreshTokenAsync_ReturnsPlaintextToken_PersistsHash()
    {
        // Arrange
        _jwtService.Setup(j => j.GenerateRefreshToken()).Returns("plaintext-token");
        _jwtService.Setup(j => j.HashRefreshToken("plaintext-token")).Returns("hashed");

        var service = CreateService();

        // Act
        var result = await service.CreateRefreshTokenAsync(_subjectId);

        // Assert
        result.Should().Be("plaintext-token");
        _repository.Verify(r => r.CreateAsync(
            It.Is<RefreshTokenRecord>(rec =>
                rec.TokenHash == "hashed" &&
                rec.SubjectId == _subjectId),
            default));
    }

    #endregion

    #region ValidateRefreshTokenAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ValidateRefreshTokenAsync_ValidToken_ReturnsSubjectId()
    {
        // Arrange
        _jwtService.Setup(j => j.HashRefreshToken("valid-token")).Returns("hash-valid");
        _repository.Setup(r => r.FindByHashAsync("hash-valid", default))
            .ReturnsAsync(MakeRecord(revokedAt: null, expiresAt: DateTime.UtcNow.AddHours(1)));

        var service = CreateService();

        // Act
        var result = await service.ValidateRefreshTokenAsync("valid-token");

        // Assert
        result.Should().Be(_subjectId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ValidateRefreshTokenAsync_ExpiredToken_ReturnsNull()
    {
        // Arrange
        _jwtService.Setup(j => j.HashRefreshToken("expired")).Returns("hash-expired");
        _repository.Setup(r => r.FindByHashAsync("hash-expired", default))
            .ReturnsAsync(MakeRecord(revokedAt: null, expiresAt: DateTime.UtcNow.AddHours(-1)));

        var service = CreateService();

        // Act
        var result = await service.ValidateRefreshTokenAsync("expired");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ValidateRefreshTokenAsync_RevokedToken_ReturnsNull()
    {
        // Arrange
        _jwtService.Setup(j => j.HashRefreshToken("revoked")).Returns("hash-revoked");
        _repository.Setup(r => r.FindByHashAsync("hash-revoked", default))
            .ReturnsAsync(MakeRecord(revokedAt: DateTime.UtcNow.AddMinutes(-5), expiresAt: DateTime.UtcNow.AddHours(1)));

        var service = CreateService();

        // Act
        var result = await service.ValidateRefreshTokenAsync("revoked");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ValidateRefreshTokenAsync_NotFound_ReturnsNull()
    {
        // Arrange
        _jwtService.Setup(j => j.HashRefreshToken("missing")).Returns("hash-missing");
        _repository.Setup(r => r.FindByHashAsync("hash-missing", default))
            .ReturnsAsync((RefreshTokenRecord?)null);

        var service = CreateService();

        // Act
        var result = await service.ValidateRefreshTokenAsync("missing");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region RotateRefreshTokenAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RotateRefreshTokenAsync_ValidToken_RevokesOldCreatesNew()
    {
        // Arrange
        var oldTokenId = Guid.CreateVersion7();
        _jwtService.Setup(j => j.HashRefreshToken("old-token")).Returns("hash-old");
        _jwtService.Setup(j => j.GenerateRefreshToken()).Returns("new-token");
        _jwtService.Setup(j => j.HashRefreshToken("new-token")).Returns("hash-new");

        _repository.Setup(r => r.FindByHashAsync("hash-old", default))
            .ReturnsAsync(MakeRecord(
                id: oldTokenId,
                revokedAt: null,
                expiresAt: DateTime.UtcNow.AddHours(1)));

        var service = CreateService();

        // Act
        var result = await service.RotateRefreshTokenAsync("old-token");

        // Assert
        result.Should().Be("new-token");

        _repository.Verify(r => r.RevokeAsync(
            oldTokenId,
            "Rotated",
            It.IsAny<Guid?>(),
            default));

        _repository.Verify(r => r.CreateAsync(
            It.Is<RefreshTokenRecord>(rec =>
                rec.TokenHash == "hash-new" &&
                rec.SubjectId == _subjectId),
            default));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RotateRefreshTokenAsync_ReuseOutsideGracePeriod_RevokesFamily()
    {
        // Arrange — revoked token reused after 5 minutes (well past grace period)
        _jwtService.Setup(j => j.HashRefreshToken("stolen")).Returns("hash-stolen");

        _repository.Setup(r => r.FindByHashAsync("hash-stolen", default))
            .ReturnsAsync(MakeRecord(
                revokedAt: DateTime.UtcNow.AddMinutes(-5),
                expiresAt: DateTime.UtcNow.AddHours(1),
                replacedByTokenId: Guid.CreateVersion7()));

        var service = CreateService();

        // Act
        await service.RotateRefreshTokenAsync("stolen");

        // Assert
        _repository.Verify(r => r.RevokeAllForSubjectAsync(
            _subjectId,
            "Token reuse detected",
            default));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RotateRefreshTokenAsync_ReuseOutsideGracePeriod_ReturnsNull()
    {
        // Arrange
        _jwtService.Setup(j => j.HashRefreshToken("stolen")).Returns("hash-stolen");

        _repository.Setup(r => r.FindByHashAsync("hash-stolen", default))
            .ReturnsAsync(MakeRecord(
                revokedAt: DateTime.UtcNow.AddMinutes(-5),
                expiresAt: DateTime.UtcNow.AddHours(1),
                replacedByTokenId: Guid.CreateVersion7()));

        var service = CreateService();

        // Act
        var result = await service.RotateRefreshTokenAsync("stolen");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RotateRefreshTokenAsync_ReuseWithinGracePeriod_ThrowsRaceException()
    {
        // Arrange — token rotated just 2 seconds ago (concurrent request race)
        _jwtService.Setup(j => j.HashRefreshToken("raced")).Returns("hash-raced");

        _repository.Setup(r => r.FindByHashAsync("hash-raced", default))
            .ReturnsAsync(MakeRecord(
                revokedAt: DateTime.UtcNow.AddSeconds(-2),
                expiresAt: DateTime.UtcNow.AddHours(1),
                replacedByTokenId: Guid.CreateVersion7()));

        var service = CreateService();

        // Act & Assert — throws instead of nuking all tokens
        await service.Invoking(s => s.RotateRefreshTokenAsync("raced"))
            .Should().ThrowAsync<TokenRotationRaceException>();

        // Family should NOT be revoked
        _repository.Verify(
            r => r.RevokeAllForSubjectAsync(It.IsAny<Guid>(), It.IsAny<string>(), default),
            Times.Never);
    }

    #endregion

    #region PruneExpiredRefreshTokensAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PruneExpiredRefreshTokensAsync_DefaultCutoff_Uses30Days()
    {
        // Arrange
        var before = DateTime.UtcNow.AddDays(-30);

        var service = CreateService();

        // Act
        await service.PruneExpiredRefreshTokensAsync(null);

        // Assert — cutoff should be approximately 30 days ago
        _repository.Verify(r => r.PruneExpiredAsync(
            It.Is<DateTime>(d => d >= before.AddSeconds(-5) && d <= before.AddSeconds(5)),
            default));
    }

    #endregion

    /// <summary>
    /// Helper to build a <see cref="RefreshTokenRecord"/> with sensible defaults.
    /// </summary>
    private RefreshTokenRecord MakeRecord(
        Guid? id = null,
        DateTime? revokedAt = null,
        DateTime? expiresAt = null,
        Guid? replacedByTokenId = null) =>
        new(
            Id: id ?? Guid.CreateVersion7(),
            TokenHash: "hash",
            SubjectId: _subjectId,
            OidcSessionId: null,
            DeviceDescription: null,
            IpAddress: null,
            UserAgent: null,
            IssuedAt: DateTime.UtcNow.AddHours(-1),
            ExpiresAt: expiresAt ?? DateTime.UtcNow.AddHours(1),
            RevokedAt: revokedAt,
            RevokedReason: revokedAt.HasValue ? "test" : null,
            ReplacedByTokenId: replacedByTokenId,
            LastUsedAt: null);
}
