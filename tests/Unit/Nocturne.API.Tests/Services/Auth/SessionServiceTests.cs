using Nocturne.API.Services.Auth;
using Subject = Nocturne.Core.Models.Authorization.Subject;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Unit tests for SessionService covering session issuance and refresh
/// token rotation.
/// </summary>
public class SessionServiceTests
{
    private readonly Mock<IJwtService> _jwtService = new();
    private readonly Mock<ISubjectService> _subjectService = new();
    private readonly Mock<IRefreshTokenService> _refreshTokenService = new();

    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Subject _subject;
    private readonly SessionContext _context = new(
        OidcSessionId: "oidc-session-123",
        DeviceDescription: "Chrome on Windows",
        IpAddress: "192.168.1.1",
        UserAgent: "Mozilla/5.0");

    private const string TestAccessToken = "test-access-token";
    private const string TestRefreshToken = "test-refresh-token";
    private const string TestNewRefreshToken = "test-new-refresh-token";

    public SessionServiceTests()
    {
        _subject = new Subject
        {
            Id = _subjectId,
            Name = "Test User",
            Email = "test@example.com",
        };

        SetupDefaultMocks();
    }

    private void SetupDefaultMocks()
    {
        _subjectService.Setup(s => s.GetSubjectByIdAsync(_subjectId))
            .ReturnsAsync(_subject);
        _subjectService.Setup(s => s.GetSubjectRolesAsync(_subjectId))
            .ReturnsAsync(new List<string> { "admin" });
        _subjectService.Setup(s => s.GetSubjectPermissionsAsync(_subjectId))
            .ReturnsAsync(new List<string> { "api:read", "api:write" });

        _jwtService.Setup(j => j.GenerateAccessToken(
                It.IsAny<SubjectInfo>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<TimeSpan?>()))
            .Returns(TestAccessToken);
        _jwtService.Setup(j => j.GetAccessTokenLifetime())
            .Returns(TimeSpan.FromMinutes(15));

        _refreshTokenService.Setup(r => r.CreateRefreshTokenAsync(
                _subjectId, _context.OidcSessionId, _context.DeviceDescription,
                _context.IpAddress, _context.UserAgent))
            .ReturnsAsync(TestRefreshToken);

        _refreshTokenService.Setup(r => r.RotateRefreshTokenAsync(
                TestRefreshToken, _context.IpAddress, _context.UserAgent))
            .ReturnsAsync(TestNewRefreshToken);
        _refreshTokenService.Setup(r => r.ValidateRefreshTokenAsync(TestNewRefreshToken))
            .ReturnsAsync(_subjectId);
    }

    private SessionService CreateService() =>
        new(_jwtService.Object, _subjectService.Object, _refreshTokenService.Object);

    #region IssueSessionAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IssueSessionAsync_ResolvesSubjectAndMintsTokens()
    {
        // Arrange
        var service = CreateService();

        // Act
        var result = await service.IssueSessionAsync(_subjectId, _context);

        // Assert
        result.AccessToken.Should().Be(TestAccessToken);
        result.RefreshToken.Should().Be(TestRefreshToken);
        result.ExpiresInSeconds.Should().Be(900);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IssueSessionAsync_SubjectNotFound_Throws()
    {
        // Arrange
        var unknownId = Guid.CreateVersion7();
        _subjectService.Setup(s => s.GetSubjectByIdAsync(unknownId))
            .ReturnsAsync((Subject?)null);

        var service = CreateService();

        // Act
        var act = () => service.IssueSessionAsync(unknownId, _context);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{unknownId}*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IssueSessionAsync_PassesContextToRefreshTokenService()
    {
        // Arrange
        var service = CreateService();

        // Act
        await service.IssueSessionAsync(_subjectId, _context);

        // Assert
        _refreshTokenService.Verify(r => r.CreateRefreshTokenAsync(
            _subjectId,
            _context.OidcSessionId,
            _context.DeviceDescription,
            _context.IpAddress,
            _context.UserAgent), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IssueSessionAsync_ExpiresInSeconds_MatchesJwtLifetime()
    {
        // Arrange
        _jwtService.Setup(j => j.GetAccessTokenLifetime())
            .Returns(TimeSpan.FromMinutes(15));

        var service = CreateService();

        // Act
        var result = await service.IssueSessionAsync(_subjectId, _context);

        // Assert
        result.ExpiresInSeconds.Should().Be(900);
    }

    #endregion

    #region RotateSessionAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RotateSessionAsync_ValidToken_ReturnsNewPair()
    {
        // Arrange
        var service = CreateService();

        // Act
        var result = await service.RotateSessionAsync(TestRefreshToken, _context);

        // Assert
        result.Should().NotBeNull();
        result!.AccessToken.Should().Be(TestAccessToken);
        result.RefreshToken.Should().Be(TestNewRefreshToken);
        result.ExpiresInSeconds.Should().Be(900);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RotateSessionAsync_InvalidToken_ReturnsNull()
    {
        // Arrange
        _refreshTokenService.Setup(r => r.RotateRefreshTokenAsync(
                "invalid-token", _context.IpAddress, _context.UserAgent))
            .ReturnsAsync((string?)null);

        var service = CreateService();

        // Act
        var result = await service.RotateSessionAsync("invalid-token", _context);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RotateSessionAsync_RefreshesRolesAndPermissions()
    {
        // Arrange
        var service = CreateService();

        // Act
        await service.RotateSessionAsync(TestRefreshToken, _context);

        // Assert
        _subjectService.Verify(s => s.GetSubjectRolesAsync(_subjectId), Times.Once);
        _subjectService.Verify(s => s.GetSubjectPermissionsAsync(_subjectId), Times.Once);
    }

    #endregion
}
