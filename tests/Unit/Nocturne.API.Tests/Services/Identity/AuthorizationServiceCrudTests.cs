using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using FluentAssertions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using AuthSubjectModel = Nocturne.Core.Models.Authorization.Subject;
using AuthRoleModel = Nocturne.Core.Models.Authorization.Role;
using LegacySubject = Nocturne.Core.Models.Subject;
using LegacyRole = Nocturne.Core.Models.Role;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// Tests for authorization service CRUD operations
/// </summary>
public class AuthorizationServiceCrudTests : IDisposable
{
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<ILogger<AuthorizationService>> _mockLogger;
    private readonly Mock<ISubjectService> _mockSubjectService;
    private readonly Mock<IRoleService> _mockRoleService;
    private readonly Mock<IJwtService> _mockJwtService;
    private readonly Mock<IDirectGrantService> _mockDirectGrantService;
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly AuthorizationService _authorizationService;

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _deviceSubjectId;

    public AuthorizationServiceCrudTests()
    {
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<AuthorizationService>>();
        _mockSubjectService = new Mock<ISubjectService>();
        _mockRoleService = new Mock<IRoleService>();
        _mockJwtService = new Mock<IJwtService>();
        _mockDirectGrantService = new Mock<IDirectGrantService>();

        _db = TestDbContextFactory.CreateSqlite();

        // Grants are tenant-scoped, so the context has to carry a tenant before anything can be
        // written to oauth_grants at all.
        _dbContext = _db.CreateContext(_tenantId);
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "default",
            DisplayName = "Default",
            IsActive = true,
        });
        _dbContext.SaveChanges();

        _deviceSubjectId = SeedDeviceSubject();

        // Setup configuration
        _mockConfiguration
            .Setup(c => c["JwtSettings:SecretKey"])
            .Returns("TestSecretKeyForNightscout");

        _authorizationService = new AuthorizationService(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockSubjectService.Object,
            _mockRoleService.Object,
            _mockDirectGrantService.Object,
            _mockJwtService.Object,
            _dbContext
        );
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
    }

    #region Subject CRUD Tests
    /// <summary>
    /// Gives the context's tenant the device subject a newly minted token is issued to, matching
    /// what <see cref="DeviceSubjectFilter"/> creates on demand.
    /// </summary>
    private Guid SeedDeviceSubject()
    {
        var subjectId = Guid.CreateVersion7();

        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = "Devices",
            IsActive = true,
            IsSystemSubject = true,
            ApprovalStatus = "Approved",
        });
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = subjectId,
            DirectPermissions = [Scope.FullAccess],
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });

        _dbContext.SaveChanges();
        return subjectId;
    }

    /// <summary>
    /// Puts a live direct grant on the context's tenant, which is what the Nightscout-compatible
    /// subjects API reads and writes now that a "subject" is a token rather than an account.
    /// </summary>
    private async Task<OAuthGrantEntity> SeedGrantAsync(
        string label, List<string> scopes, DateTime? revokedAt = null)
    {
        var grant = new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = _deviceSubjectId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = scopes,
            Label = label,
            RevokedAt = revokedAt,
            CreatedAt = DateTime.UtcNow,
        };

        _dbContext.OAuthGrants.Add(grant);
        await _dbContext.SaveChangesAsync();
        return grant;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAllSubjectsAsync_ReturnsLiveGrantsAsSubjects()
    {
        await SeedGrantAsync("Pump uploader", [Scope.GlucoseRead]);
        await SeedGrantAsync("Retired phone", [Scope.GlucoseRead], revokedAt: DateTime.UtcNow);

        var result = await _authorizationService.GetAllSubjectsAsync();

        // A revoked grant is gone as far as the API is concerned, but the row stays for audit.
        result.Should().ContainSingle().Which.Name.Should().Be("Pump uploader");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAllSubjectsAsync_RendersScopesAsLegacyPermissions()
    {
        await SeedGrantAsync("Pump uploader", [Scope.GlucoseRead]);

        var result = await _authorizationService.GetAllSubjectsAsync();

        // Round-trippable: what the list reports can be written straight back through
        // UpdateSubjectAsync without the grant losing authority.
        result.Single().Roles.Should().Contain("api:entries:read");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAllSubjectsAsync_DoesNotReportAnotherMembersOwnTokens()
    {
        var member = Guid.CreateVersion7();
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = member,
            Name = "Mum",
            IsActive = true,
            ApprovalStatus = "Approved",
        });
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = member,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = [Scope.GlucoseRead],
            Label = "Mum's phone",
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _authorizationService.GetAllSubjectsAsync();

        // A member's own tokens are theirs to manage. Listing them on an admin screen would also
        // put them within reach of DeleteSubjectAsync, which revokes whatever it is handed.
        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeleteSubjectAsync_WillNotRevokeAnotherMembersToken()
    {
        var otherMembersGrant = Guid.CreateVersion7();

        var result = await _authorizationService.DeleteSubjectAsync(otherMembersGrant.ToString());

        result.Should().BeFalse();
        _mockDirectGrantService.Verify(
            d => d.RevokeAsync(
                It.IsAny<NocturneDbContext>(), otherMembersGrant, It.Is<Guid?>(id => id == null),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<AuthAuditActor>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateSubjectAsync_WithNoTranslatableRoles_IsRejectedRatherThanMinted()
    {
        _mockRoleService.Setup(r => r.GetAllRolesAsync()).ReturnsAsync([]);

        // Nightscout will create a subject that holds nothing. A grant cannot, so the caller has to
        // hear about it rather than receive a token that refuses every request it is used for.
        var create = () => _authorizationService.CreateSubjectAsync(new LegacySubject
        {
            Name = "Holds nothing",
            Roles = ["a-role-nocturne-cannot-translate"],
        });

        await create.Should().ThrowAsync<ArgumentException>();
        _mockDirectGrantService.Verify(
            d => d.CreateAsync(
                It.IsAny<NocturneDbContext>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<AuthAuditActor>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSubjectByIdAsync_WithValidId_ReturnsGrant()
    {
        var grant = await SeedGrantAsync("Pump uploader", [Scope.GlucoseRead]);

        var result = await _authorizationService.GetSubjectByIdAsync(grant.Id.ToString());

        result.Should().NotBeNull();
        result!.Name.Should().Be("Pump uploader");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSubjectByIdAsync_WithNonExistentId_ReturnsNull()
    {
        var result = await _authorizationService.GetSubjectByIdAsync(Guid.NewGuid().ToString());

        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSubjectByIdAsync_WithInvalidGuidFormat_ReturnsNull()
    {
        var result = await _authorizationService.GetSubjectByIdAsync("not-a-guid");

        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateSubjectAsync_MintsAGrantOnTheDeviceSubjectAndReturnsTheTokenOnce()
    {
        var grantId = Guid.CreateVersion7();

        _mockRoleService
            .Setup(r => r.GetAllRolesAsync())
            .ReturnsAsync([new AuthRoleModel
            {
                Id = Guid.NewGuid(),
                Name = "readable",
                Permissions = ["*:*:read"],
            }]);

        _mockDirectGrantService
            .Setup(d => d.CreateAsync(
                It.IsAny<NocturneDbContext>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<AuthAuditActor>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectGrantCreationResult.Created(new CreateDirectGrantResponse
            {
                Id = grantId,
                Token = "noc_generated-token",
                Label = "New Device Subject",
                Scopes = [Scope.GlucoseRead],
                CreatedAt = DateTime.UtcNow,
            }));

        var result = await _authorizationService.CreateSubjectAsync(new LegacySubject
        {
            Name = "New Device Subject",
            Roles = ["readable"],
        });

        result.Id.Should().Be(grantId.ToString());
        result.AccessToken.Should().Be("noc_generated-token");

        // The token hangs off the device subject, which is a member, so MemberScopeMiddleware has a
        // membership to intersect the grant's scopes with. A grant on a non-member would
        // authenticate and then be dropped straight back to unauthenticated.
        _mockDirectGrantService.Verify(
            d => d.CreateAsync(
                It.IsAny<NocturneDbContext>(),
                _deviceSubjectId,
                "New Device Subject",
                It.Is<IReadOnlyCollection<string>>(scopes => scopes.Contains(Scope.GlucoseRead)),
                null, null, null, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateSubjectAsync_WithExistingGrant_RelabelsAndRescopesIt()
    {
        var grant = await SeedGrantAsync("Old name", [Scope.GlucoseRead]);

        _mockRoleService.Setup(r => r.GetAllRolesAsync()).ReturnsAsync([]);

        var result = await _authorizationService.UpdateSubjectAsync(new LegacySubject
        {
            Id = grant.Id.ToString(),
            Name = "New name",
            Roles = ["api:treatments:read"],
        });

        result.Should().NotBeNull();
        result!.Name.Should().Be("New name");

        var reloaded = await _dbContext.OAuthGrants.SingleAsync(g => g.Id == grant.Id);
        reloaded.Scopes.Should().Equal(Scope.TreatmentsRead);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateSubjectAsync_WithoutRoles_LeavesTheGrantsAuthorityAlone()
    {
        var grant = await SeedGrantAsync("Pump uploader", [Scope.GlucoseRead]);

        var result = await _authorizationService.UpdateSubjectAsync(new LegacySubject
        {
            Id = grant.Id.ToString(),
            Name = "Renamed",
        });

        result.Should().NotBeNull();

        // Omitting roles is not the same as asking for none; a client that only renames must not
        // silently strip the token of everything it could do.
        var reloaded = await _dbContext.OAuthGrants.SingleAsync(g => g.Id == grant.Id);
        reloaded.Scopes.Should().Equal(Scope.GlucoseRead);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateSubjectAsync_WithNoTranslatableRoles_IsRejectedRatherThanZeroed()
    {
        var grant = await SeedGrantAsync("Pump uploader", [Scope.GlucoseRead]);
        _mockRoleService.Setup(r => r.GetAllRolesAsync()).ReturnsAsync([]);

        var update = () => _authorizationService.UpdateSubjectAsync(new LegacySubject
        {
            Id = grant.Id.ToString(),
            Name = "Pump uploader",
            Roles = ["a-role-nocturne-cannot-translate"],
        });

        await update.Should().ThrowAsync<ArgumentException>();

        // Answering 200 here leaves a token that authenticates and then refuses every request it is
        // used for, with nothing said about why.
        var reloaded = await _dbContext.OAuthGrants.SingleAsync(g => g.Id == grant.Id);
        reloaded.Scopes.Should().Equal(Scope.GlucoseRead);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateSubjectAsync_WithNonExistentSubject_ReturnsNull()
    {
        var result = await _authorizationService.UpdateSubjectAsync(new LegacySubject
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Nobody",
        });

        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeleteSubjectAsync_WithValidId_RevokesTheGrant()
    {
        var grantId = Guid.CreateVersion7();

        _mockDirectGrantService
            .Setup(d => d.RevokeAsync(
                It.IsAny<NocturneDbContext>(), grantId, _deviceSubjectId, null, null,
                It.IsAny<AuthAuditActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _authorizationService.DeleteSubjectAsync(grantId.ToString());

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeleteSubjectAsync_WithNonExistentId_ReturnsFalse()
    {
        _mockDirectGrantService
            .Setup(d => d.RevokeAsync(
                It.IsAny<NocturneDbContext>(), It.IsAny<Guid>(), _deviceSubjectId, null, null,
                It.IsAny<AuthAuditActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _authorizationService.DeleteSubjectAsync(Guid.NewGuid().ToString());

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeleteSubjectAsync_WithInvalidGuidFormat_ReturnsFalse()
    {
        var result = await _authorizationService.DeleteSubjectAsync("not-a-guid");

        result.Should().BeFalse();
    }

    #endregion

    #region Role CRUD Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAllRolesAsync_ReturnsRolesFromService()
    {
        // Arrange
        var authRoles = new List<AuthRoleModel>
        {
            new AuthRoleModel
            {
                Id = Guid.NewGuid(),
                Name = "admin",
                Description = "Full administrative access",
                Permissions = new List<string> { "*" },
                IsSystemRole = true
            },
            new AuthRoleModel
            {
                Id = Guid.NewGuid(),
                Name = "readable",
                Description = "Read-only access",
                Permissions = new List<string> { "api:*:read" },
                IsSystemRole = true
            }
        };

        _mockRoleService
            .Setup(r => r.GetAllRolesAsync())
            .ReturnsAsync(authRoles);

        // Act
        var result = await _authorizationService.GetAllRolesAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("admin", result[0].Name);
        Assert.Equal("readable", result[1].Name);
        _mockRoleService.Verify(r => r.GetAllRolesAsync(), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetRoleByIdAsync_WithValidId_ReturnsRole()
    {
        // Arrange
        var roleId = Guid.NewGuid();
        var authRole = new AuthRoleModel
        {
            Id = roleId,
            Name = "custom-role",
            Description = "A custom role",
            Permissions = new List<string> { "api:entries:read" },
            IsSystemRole = false
        };

        _mockRoleService
            .Setup(r => r.GetRoleByIdAsync(roleId))
            .ReturnsAsync(authRole);

        // Act
        var result = await _authorizationService.GetRoleByIdAsync(roleId.ToString());

        // Assert
        Assert.NotNull(result);
        Assert.Equal("custom-role", result.Name);
        Assert.Equal(roleId.ToString(), result.Id);
        _mockRoleService.Verify(r => r.GetRoleByIdAsync(roleId), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetRoleByIdAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        var roleId = Guid.NewGuid();

        _mockRoleService
            .Setup(r => r.GetRoleByIdAsync(roleId))
            .ReturnsAsync((AuthRoleModel?)null);

        // Act
        var result = await _authorizationService.GetRoleByIdAsync(roleId.ToString());

        // Assert
        Assert.Null(result);
        _mockRoleService.Verify(r => r.GetRoleByIdAsync(roleId), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetRoleByIdAsync_WithInvalidGuidFormat_ReturnsNull()
    {
        // Arrange
        var invalidId = "not-a-valid-guid";

        // Act
        var result = await _authorizationService.GetRoleByIdAsync(invalidId);

        // Assert
        Assert.Null(result);
        _mockRoleService.Verify(r => r.GetRoleByIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateRoleAsync_CreatesAndReturnsRole()
    {
        // Arrange
        var legacyRole = new LegacyRole
        {
            Name = "editor",
            Permissions = new List<string> { "api:treatments:*", "api:entries:read" },
            Notes = "Editor role description"
        };

        var createdRole = new AuthRoleModel
        {
            Id = Guid.NewGuid(),
            Name = "editor",
            Description = "Editor role description",
            Permissions = new List<string> { "api:treatments:*", "api:entries:read" },
            IsSystemRole = false
        };

        _mockRoleService
            .Setup(r => r.CreateRoleAsync(It.IsAny<AuthRoleModel>()))
            .ReturnsAsync(createdRole);

        // Act
        var result = await _authorizationService.CreateRoleAsync(legacyRole);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("editor", result.Name);
        Assert.NotNull(result.Id);
        _mockRoleService.Verify(r => r.CreateRoleAsync(It.Is<AuthRoleModel>(role =>
            role.Name == "editor" &&
            role.Description == "Editor role description" &&
            role.IsSystemRole == false
        )), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateRoleAsync_WithExistingRole_ReturnsUpdated()
    {
        // Arrange
        var roleId = Guid.NewGuid();
        var legacyRole = new LegacyRole
        {
            Id = roleId.ToString(),
            Name = "updated-role",
            Permissions = new List<string> { "api:*:read" },
            Notes = "Updated description"
        };

        var existingRole = new AuthRoleModel
        {
            Id = roleId,
            Name = "original-role",
            Description = "Original description",
            Permissions = new List<string> { "api:entries:read" },
            IsSystemRole = false
        };

        var updatedRole = new AuthRoleModel
        {
            Id = roleId,
            Name = "updated-role",
            Description = "Updated description",
            Permissions = new List<string> { "api:*:read" },
            IsSystemRole = false
        };

        _mockRoleService
            .Setup(r => r.GetRoleByIdAsync(roleId))
            .ReturnsAsync(existingRole);

        _mockRoleService
            .Setup(r => r.UpdateRoleAsync(It.IsAny<AuthRoleModel>()))
            .ReturnsAsync(updatedRole);

        // Act
        var result = await _authorizationService.UpdateRoleAsync(legacyRole);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("updated-role", result.Name);
        _mockRoleService.Verify(r => r.UpdateRoleAsync(It.IsAny<AuthRoleModel>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateRoleAsync_WithNonExistentRole_ReturnsNull()
    {
        // Arrange
        var roleId = Guid.NewGuid();
        var legacyRole = new LegacyRole
        {
            Id = roleId.ToString(),
            Name = "non-existent-role"
        };

        _mockRoleService
            .Setup(r => r.GetRoleByIdAsync(roleId))
            .ReturnsAsync((AuthRoleModel?)null);

        // Act
        var result = await _authorizationService.UpdateRoleAsync(legacyRole);

        // Assert
        Assert.Null(result);
        _mockRoleService.Verify(r => r.UpdateRoleAsync(It.IsAny<AuthRoleModel>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeleteRoleAsync_WithValidId_ReturnsTrue()
    {
        // Arrange
        var roleId = Guid.NewGuid();

        _mockRoleService
            .Setup(r => r.DeleteRoleAsync(roleId))
            .ReturnsAsync(true);

        // Act
        var result = await _authorizationService.DeleteRoleAsync(roleId.ToString());

        // Assert
        Assert.True(result);
        _mockRoleService.Verify(r => r.DeleteRoleAsync(roleId), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeleteRoleAsync_WithSystemRole_ReturnsFalse()
    {
        // Arrange
        var roleId = Guid.NewGuid();

        // The RoleService returns false for system roles
        _mockRoleService
            .Setup(r => r.DeleteRoleAsync(roleId))
            .ReturnsAsync(false);

        // Act
        var result = await _authorizationService.DeleteRoleAsync(roleId.ToString());

        // Assert
        Assert.False(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeleteRoleAsync_WithInvalidGuidFormat_ReturnsFalse()
    {
        // Arrange
        var invalidId = "not-a-valid-guid";

        // Act
        var result = await _authorizationService.DeleteRoleAsync(invalidId);

        // Assert
        Assert.False(result);
        _mockRoleService.Verify(r => r.DeleteRoleAsync(It.IsAny<Guid>()), Times.Never);
    }

    #endregion
}
