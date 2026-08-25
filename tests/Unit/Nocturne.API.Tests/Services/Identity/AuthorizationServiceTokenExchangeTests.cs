using Nocturne.Connectors.Core.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;
using AuthSubject = Nocturne.Core.Models.Authorization.Subject;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// Tests for the token-exchange path (/api/v2/authorization/request/{accessToken}):
/// legacy subject access tokens resolve against subjects, noc_ direct-grant tokens
/// resolve against oauth_grants.
/// </summary>
public class AuthorizationServiceTokenExchangeTests : IDisposable
{
    private readonly Mock<ISubjectService> _mockSubjectService;
    private readonly Mock<IJwtService> _mockJwtService;
    private readonly SqliteConnection _connection;
    private readonly NocturneDbContext _dbContext;
    private readonly AuthorizationService _authorizationService;

    private readonly Guid _testTenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    public AuthorizationServiceTokenExchangeTests()
    {
        _mockSubjectService = new Mock<ISubjectService>();
        _mockJwtService = new Mock<IJwtService>();

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(dbOptions) { TenantId = _testTenantId };
        _dbContext.Database.EnsureCreated();
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _testTenantId,
            Slug = "default",
            DisplayName = "Default",
            IsActive = true,
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = _subjectId,
            Name = "aaps-uploader",
            IsActive = true,
        });
        _dbContext.SaveChanges();

        _authorizationService = new AuthorizationService(
            new Mock<IConfiguration>().Object,
            Mock.Of<ILogger<AuthorizationService>>(),
            _mockSubjectService.Object,
            new Mock<IRoleService>().Object,
            _mockJwtService.Object,
            _dbContext
        );
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private void SeedGrant(
        string token,
        DateTime? revokedAt = null,
        List<string>? scopes = null,
        DateTime? expiresAt = null,
        Guid? tenantId = null)
    {
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = _subjectId,
            TenantId = tenantId ?? _testTenantId,
            GrantType = OAuthGrantTypes.Direct,
            TokenHash = HashUtils.Sha256Hex(token),
            Scopes = scopes ?? ["glucose.read", "treatments.readwrite"],
            CreatedAt = DateTime.UtcNow,
            RevokedAt = revokedAt,
            ExpiresAt = expiresAt,
        });
        _dbContext.SaveChanges();
    }

    private void SetupActiveSubject()
    {
        _mockSubjectService
            .Setup(s => s.GetSubjectByIdAsync(_subjectId))
            .ReturnsAsync(new AuthSubject
            {
                Id = _subjectId,
                Name = "aaps-uploader",
                IsActive = true,
            });
    }

    private void SetupMintedJwt(string jwt = "minted.jwt.token")
    {
        _mockJwtService
            .Setup(j => j.GenerateAccessToken(
                It.IsAny<SubjectInfo>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<Guid?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<Guid?>()))
            .Returns(jwt);
    }

    [Fact]
    public async Task GenerateJwtFromAccessTokenAsync_NocToken_MintsJwtWithGrantScopesAndTenantPin()
    {
        var token = "noc_uploadertoken123";
        SeedGrant(token);
        SetupActiveSubject();
        SetupMintedJwt();

        var result = await _authorizationService.GenerateJwtFromAccessTokenAsync(token);

        Assert.NotNull(result);
        Assert.Equal("minted.jwt.token", result!.Token);
        Assert.Equal("aaps-uploader", result.Sub);

        _mockJwtService.Verify(j => j.GenerateAccessToken(
            It.Is<SubjectInfo>(s => s.Id == _subjectId && s.Name == "aaps-uploader"),
            It.Is<IEnumerable<string>>(p => !p.Any()),
            It.Is<IEnumerable<string>>(r => !r.Any()),
            It.Is<IEnumerable<string>>(sc =>
                sc.Contains("glucose.read") && sc.Contains("treatments.readwrite")),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            _testTenantId,
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task GenerateJwtFromAccessTokenAsync_NocToken_RevokedGrant_ReturnsNull()
    {
        var token = "noc_revokedtoken456";
        SeedGrant(token, revokedAt: DateTime.UtcNow);
        SetupActiveSubject();
        SetupMintedJwt();

        var result = await _authorizationService.GenerateJwtFromAccessTokenAsync(token);

        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateJwtFromAccessTokenAsync_NocToken_UnknownToken_ReturnsNull()
    {
        var result = await _authorizationService.GenerateJwtFromAccessTokenAsync("noc_nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateJwtFromAccessTokenAsync_NocToken_InactiveSubject_ReturnsNull()
    {
        var token = "noc_inactivesubject789";
        SeedGrant(token);
        _mockSubjectService
            .Setup(s => s.GetSubjectByIdAsync(_subjectId))
            .ReturnsAsync(new AuthSubject
            {
                Id = _subjectId,
                Name = "aaps-uploader",
                IsActive = false,
            });

        var result = await _authorizationService.GenerateJwtFromAccessTokenAsync(token);

        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateJwtFromAccessTokenAsync_LegacyToken_StillResolvesViaSubjects()
    {
        var legacyToken = "uploader-0123456789abcdef";
        var subject = new AuthSubject
        {
            Id = _subjectId,
            Name = "aaps-uploader",
            IsActive = true,
        };
        _mockSubjectService
            .Setup(s => s.GetSubjectByAccessTokenHashAsync(It.IsAny<string>()))
            .ReturnsAsync(subject);
        _mockSubjectService
            .Setup(s => s.GetSubjectPermissionsAsync(_subjectId))
            .ReturnsAsync(["api:*:read"]);
        _mockSubjectService
            .Setup(s => s.GetSubjectRolesAsync(_subjectId))
            .ReturnsAsync(["readable"]);
        _mockJwtService
            .Setup(j => j.GenerateAccessToken(
                It.IsAny<SubjectInfo>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<TimeSpan?>()))
            .Returns("legacy.jwt.token");

        var result = await _authorizationService.GenerateJwtFromAccessTokenAsync(legacyToken);

        Assert.NotNull(result);
        Assert.Equal("legacy.jwt.token", result!.Token);
        Assert.Equal("aaps-uploader", result.Sub);
    }

    /// <summary>
    /// The exchange restated the "usable grant" predicate and left the expiry term out, so a grant
    /// the bearer handler and both hubs refused could still be traded here for a fresh one-hour
    /// JWT — outliving its own expiry by the lifetime of whatever it minted.
    /// </summary>
    [Fact]
    public async Task Expired_direct_grant_is_not_exchangeable()
    {
        const string token = "noc_expired";
        SeedGrant(token, expiresAt: DateTime.UtcNow.AddMinutes(-1));
        SetupActiveSubject();
        SetupMintedJwt();

        var result = await _authorizationService.GenerateJwtFromAccessTokenAsync(token);

        result.Should().BeNull("an expired grant must not mint a token that outlives it");
    }

    /// <summary>
    /// The exchange scopes its grant lookup by the context's global query filter rather than by an
    /// explicit tenant id, so nothing in the query itself names the tenant. That filter is the only
    /// thing standing between a grant minted on one tenant and a JWT issued on another, and
    /// dropping it passed the rest of the suite unnoticed.
    /// </summary>
    [Fact]
    public async Task A_grant_belonging_to_another_tenant_is_not_exchangeable()
    {
        const string token = "noc_othertenant";
        var otherTenantId = Guid.CreateVersion7();
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = otherTenantId,
            Slug = "other",
            DisplayName = "Other",
            IsActive = true,
        });
        _dbContext.SaveChanges();

        SeedGrant(token, tenantId: otherTenantId);
        SetupActiveSubject();
        SetupMintedJwt();

        var result = await _authorizationService.GenerateJwtFromAccessTokenAsync(token);

        result.Should().BeNull(
            "a grant minted on another tenant must not mint a token on the tenant this request resolved to");
    }

    [Fact]
    public async Task Direct_grant_expiring_in_the_future_is_still_exchangeable()
    {
        const string token = "noc_live";
        SeedGrant(token, expiresAt: DateTime.UtcNow.AddHours(1));
        SetupActiveSubject();
        SetupMintedJwt();

        var result = await _authorizationService.GenerateJwtFromAccessTokenAsync(token);

        result.Should().NotBeNull("an unexpired grant is still usable");
    }
}
