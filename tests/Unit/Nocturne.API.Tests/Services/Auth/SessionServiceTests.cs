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
                It.IsAny<string?>(),
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
        _refreshTokenService.Setup(r => r.FindOwnerAsync(TestRefreshToken))
            .ReturnsAsync(new RefreshTokenSession(_subjectId, _context.OidcSessionId!));
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
    public async Task IssueSessionAsync_NoProviderSessionId_GeneratesOne()
    {
        // Arrange — the identity provider supplied no session id (Google omits `sid`; the
        // passkey/TOTP/setup paths have none). Without a generated id the token chain could
        // only be revoked subject-wide.
        var contextWithoutSession = _context with { OidcSessionId = null };
        string? capturedSessionId = null;
        _refreshTokenService.Setup(r => r.CreateRefreshTokenAsync(
                _subjectId, It.IsAny<string?>(), contextWithoutSession.DeviceDescription,
                contextWithoutSession.IpAddress, contextWithoutSession.UserAgent))
            .Callback<Guid, string?, string?, string?, string?>(
                (_, sid, _, _, _) => capturedSessionId = sid)
            .ReturnsAsync(TestRefreshToken);

        var service = CreateService();

        // Act
        await service.IssueSessionAsync(_subjectId, contextWithoutSession);

        // Assert — a stable session id is minted so the chain can be revoked on its own.
        capturedSessionId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IssueSessionAsync_ProviderSessionId_IsPreserved()
    {
        // Arrange — when the provider does supply a session id, keep it (ties us to the
        // provider's session for front-channel logout).
        string? capturedSessionId = null;
        _refreshTokenService.Setup(r => r.CreateRefreshTokenAsync(
                _subjectId, It.IsAny<string?>(), _context.DeviceDescription,
                _context.IpAddress, _context.UserAgent))
            .Callback<Guid, string?, string?, string?, string?>(
                (_, sid, _, _, _) => capturedSessionId = sid)
            .ReturnsAsync(TestRefreshToken);

        var service = CreateService();

        // Act
        await service.IssueSessionAsync(_subjectId, _context);

        // Assert
        capturedSessionId.Should().Be("oidc-session-123");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IssueSessionAsync_AccessTokenNamesTheRefreshTokenFamily()
    {
        string? family = null;
        _refreshTokenService.Setup(r => r.CreateRefreshTokenAsync(
                _subjectId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<Guid, string?, string?, string?, string?>((_, sid, _, _, _) => family = sid)
            .ReturnsAsync(TestRefreshToken);
        string? claimed = null;
        _jwtService.Setup(j => j.GenerateAccessToken(
                It.IsAny<SubjectInfo>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>()))
            .Callback<SubjectInfo, IEnumerable<string>, IEnumerable<string>, string?, TimeSpan?>(
                (_, _, _, sid, _) => claimed = sid)
            .Returns(TestAccessToken);

        await CreateService().IssueSessionAsync(_subjectId, _context with { OidcSessionId = null });

        family.Should().NotBeNullOrEmpty();
        claimed.Should().Be(family);
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
        result.SubjectId.Should().Be(_subjectId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RotateSessionAsync_NeverReadsTheRotatedToken()
    {
        var service = CreateService();

        await service.RotateSessionAsync(TestRefreshToken, _context);

        _refreshTokenService.Verify(r => r.ValidateRefreshTokenAsync(It.IsAny<string>()), Times.Never);
        _refreshTokenService.Verify(r => r.FindOwnerAsync(TestNewRefreshToken), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RotateSessionAsync_UnknownToken_ReturnsNullWithoutRotating()
    {
        // Arrange
        var service = CreateService();

        // Act
        var result = await service.RotateSessionAsync("invalid-token", _context);

        // Assert
        result.Should().BeNull();
        _refreshTokenService.Verify(r => r.RotateRefreshTokenAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RotateSessionAsync_RotationRefused_ReturnsNull()
    {
        _refreshTokenService.Setup(r => r.RotateRefreshTokenAsync(
                TestRefreshToken, _context.IpAddress, _context.UserAgent))
            .ReturnsAsync((string?)null);
        var service = CreateService();

        var result = await service.RotateSessionAsync(TestRefreshToken, _context);

        result.Should().BeNull();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RotateSessionAsync_SubjectMissingOrInactive_RevokesThePresentedTokenWithoutRotating(bool exists)
    {
        _subjectService.Setup(s => s.GetSubjectByIdAsync(_subjectId))
            .ReturnsAsync(exists ? new Subject { Id = _subjectId, Name = "Gone", IsActive = false } : null);
        var service = CreateService();

        var result = await service.RotateSessionAsync(TestRefreshToken, _context);

        result.Should().BeNull();
        _refreshTokenService.Verify(r => r.RevokeRefreshTokenAsync(TestRefreshToken, It.IsAny<string>()), Times.Once);
        _refreshTokenService.Verify(r => r.RotateRefreshTokenAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        _jwtService.Verify(j => j.GenerateAccessToken(
            It.IsAny<SubjectInfo>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(),
            It.IsAny<string?>(), It.IsAny<TimeSpan?>()), Times.Never);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("subject")]
    [InlineData("roles")]
    [InlineData("permissions")]
    public async Task RotateSessionAsync_ReadFails_PresentedTokenStillRotatesOnRetry(string failingRead)
    {
        var outage = new TimeoutException("database unavailable");
        switch (failingRead)
        {
            case "subject":
                _subjectService.SetupSequence(s => s.GetSubjectByIdAsync(_subjectId))
                    .ThrowsAsync(outage).ReturnsAsync(_subject);
                break;
            case "roles":
                _subjectService.SetupSequence(s => s.GetSubjectRolesAsync(_subjectId))
                    .ThrowsAsync(outage).ReturnsAsync(new List<string> { "admin" });
                break;
            case "permissions":
                _subjectService.SetupSequence(s => s.GetSubjectPermissionsAsync(_subjectId))
                    .ThrowsAsync(outage).ReturnsAsync(new List<string> { "api:read" });
                break;
        }
        var service = CreateService();

        var failed = () => service.RotateSessionAsync(TestRefreshToken, _context);
        await failed.Should().ThrowAsync<TimeoutException>();

        _refreshTokenService.Verify(r => r.RotateRefreshTokenAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);

        var retried = await service.RotateSessionAsync(TestRefreshToken, _context);

        retried!.RefreshToken.Should().Be(TestNewRefreshToken);
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RotateSessionAsync_AccessTokenCarriesThePresentedTokensSession()
    {
        _refreshTokenService.Setup(r => r.FindOwnerAsync(TestRefreshToken))
            .ReturnsAsync(new RefreshTokenSession(_subjectId, "family-a"));
        var service = CreateService();

        await service.RotateSessionAsync(TestRefreshToken, _context);

        _jwtService.Verify(j => j.GenerateAccessToken(
            It.IsAny<SubjectInfo>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(),
            "family-a", It.IsAny<TimeSpan?>()), Times.Once);
    }

    #endregion
}
