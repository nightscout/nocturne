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

    private void SeedGrant(string token, DateTime? revokedAt = null, List<string>? scopes = null)
    {
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = _subjectId,
            TenantId = _testTenantId,
            GrantType = OAuthGrantTypes.Direct,
            TokenHash = DirectGrantTokenHandler.ComputeSha256Hex(token),
            Scopes = scopes ?? ["glucose.read", "treatments.readwrite"],
            CreatedAt = DateTime.UtcNow,
            RevokedAt = revokedAt,
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
}
