using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Xunit;
using Subject = Nocturne.Core.Models.Authorization.Subject;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// <see cref="OidcAuthService.RefreshSessionAsync"/> on both refresh paths, through a real
/// <see cref="SessionService"/>, so the two paths are held to one rule on who may refresh.
/// </summary>
[Trait("Category", "Unit")]
public class OidcAuthServiceRefreshTests
{
    private const string Presented = "presented";

    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Mock<ISubjectService> _subjectService = new();
    private readonly Mock<IJwtService> _jwtService = new();
    private readonly Mock<IRefreshTokenService> _refreshTokenService = new();

    public OidcAuthServiceRefreshTests()
    {
        var owner = new RefreshTokenSession(_subjectId, "session-a");
        _refreshTokenService.Setup(r => r.FindOwnerAsync(Presented)).ReturnsAsync(owner);
        _refreshTokenService.Setup(r => r.ValidateRefreshTokenAsync(Presented)).ReturnsAsync(owner);
        _refreshTokenService.Setup(r => r.RotateRefreshTokenAsync(Presented, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("successor");

        _subjectService.Setup(s => s.GetSubjectRolesAsync(_subjectId)).ReturnsAsync(["admin"]);
        _subjectService.Setup(s => s.GetSubjectPermissionsAsync(_subjectId)).ReturnsAsync(["api:read"]);

        _jwtService.Setup(j => j.GenerateAccessToken(
                It.IsAny<SubjectInfo>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>()))
            .Returns("access");
        _jwtService.Setup(j => j.GetAccessTokenLifetime()).Returns(TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task A_missing_or_inactive_subject_is_refused_and_its_token_revoked(bool rotate, bool exists)
    {
        _subjectService.Setup(s => s.GetSubjectByIdAsync(_subjectId))
            .ReturnsAsync(exists ? new Subject { Id = _subjectId, Name = "Gone", IsActive = false } : null);

        var result = await Service(rotate).RefreshSessionAsync(Presented);

        result.Should().BeNull();
        _refreshTokenService.Verify(r => r.RevokeRefreshTokenAsync(Presented, It.IsAny<string>()), Times.Once);
        _refreshTokenService.Verify(r => r.RotateRefreshTokenAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        _jwtService.Verify(j => j.GenerateAccessToken(
            It.IsAny<SubjectInfo>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(),
            It.IsAny<string?>(), It.IsAny<TimeSpan?>()), Times.Never);
    }

    [Theory]
    [InlineData(true, "successor")]
    [InlineData(false, Presented)]
    public async Task An_active_subject_refreshes(bool rotate, string refreshToken)
    {
        _subjectService.Setup(s => s.GetSubjectByIdAsync(_subjectId))
            .ReturnsAsync(new Subject { Id = _subjectId, Name = "Owner", IsActive = true });

        var result = await Service(rotate).RefreshSessionAsync(Presented);

        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("access");
        result.RefreshToken.Should().Be(refreshToken);
        result.SubjectId.Should().Be(_subjectId);
        _refreshTokenService.Verify(r => r.RevokeRefreshTokenAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private OidcAuthService Service(bool rotate) =>
        new(
            Mock.Of<IOidcProviderService>(),
            _subjectService.Object,
            new SessionService(_jwtService.Object, _subjectService.Object, _refreshTokenService.Object),
            _jwtService.Object,
            _refreshTokenService.Object,
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<ITenantMemberService>(),
            Mock.Of<IMemberInviteService>(),
            Mock.Of<ITotpService>(),
            new EphemeralDataProtectionProvider(),
            Options.Create(new OidcOptions { Session = new() { RotateRefreshTokens = rotate } }),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            NullLogger<OidcAuthService>.Instance);
}
