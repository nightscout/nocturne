using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Infrastructure.Cache.Abstractions;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Unit tests for OAuthTokenRevocationCache verifying cache interactions
/// for token revocation tracking.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "OAuth")]
public class OAuthTokenRevocationCacheTests
{
    private readonly Mock<ICacheService> _mockCache;
    private readonly Mock<ILogger<OAuthTokenRevocationCache>> _mockLogger;
    private readonly OAuthTokenRevocationCache _cache;

    public OAuthTokenRevocationCacheTests()
    {
        _mockCache = new Mock<ICacheService>();
        _mockLogger = new Mock<ILogger<OAuthTokenRevocationCache>>();
        _cache = new OAuthTokenRevocationCache(_mockCache.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task RevokeAsync_ValidJti_CallsSetAsyncWithCorrectKeyAndTtl()
    {
        // Arrange
        var jti = "test-jti-123";
        var remainingLifetime = TimeSpan.FromMinutes(30);

        // Act
        await _cache.RevokeAsync(jti, remainingLifetime);

        // Assert
        _mockCache.Verify(
            c => c.SetAsync(
                "oauth:revoked:test-jti-123",
                It.IsAny<RevokedTokenMarker>(),
                remainingLifetime,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RevokeAsync_EmptyOrNullJti_DoesNothing(string? jti)
    {
        // Act
        await _cache.RevokeAsync(jti!, TimeSpan.FromMinutes(30));

        // Assert
        _mockCache.Verify(
            c => c.SetAsync(
                It.IsAny<string>(),
                It.IsAny<RevokedTokenMarker>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-60)]
    public async Task RevokeAsync_ZeroOrNegativeRemainingLifetime_DoesNothing(int seconds)
    {
        // Arrange
        var remainingLifetime = TimeSpan.FromSeconds(seconds);

        // Act
        await _cache.RevokeAsync("valid-jti", remainingLifetime);

        // Assert
        _mockCache.Verify(
            c => c.SetAsync(
                It.IsAny<string>(),
                It.IsAny<RevokedTokenMarker>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task IsRevokedAsync_TokenIsRevoked_ReturnsTrue()
    {
        // Arrange
        var jti = "revoked-jti";
        _mockCache
            .Setup(c => c.ExistsAsync("oauth:revoked:revoked-jti", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _cache.IsRevokedAsync(jti);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsRevokedAsync_TokenIsNotRevoked_ReturnsFalse()
    {
        // Arrange
        var jti = "active-jti";
        _mockCache
            .Setup(c => c.ExistsAsync("oauth:revoked:active-jti", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _cache.IsRevokedAsync(jti);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task IsRevokedAsync_EmptyOrNullJti_ReturnsFalse(string? jti)
    {
        // Act
        var result = await _cache.IsRevokedAsync(jti!);

        // Assert
        result.Should().BeFalse();
        _mockCache.Verify(
            c => c.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
