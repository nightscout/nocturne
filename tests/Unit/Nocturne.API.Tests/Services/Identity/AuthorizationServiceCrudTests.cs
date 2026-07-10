using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
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
    private readonly SqliteConnection _connection;
    private readonly NocturneDbContext _dbContext;
    private readonly AuthorizationService _authorizationService;

    public AuthorizationServiceCrudTests()
    {
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<AuthorizationService>>();
        _mockSubjectService = new Mock<ISubjectService>();
        _mockRoleService = new Mock<IRoleService>();
        _mockJwtService = new Mock<IJwtService>();

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        _dbContext = new NocturneDbContext(dbOptions);
        _dbContext.Database.EnsureCreated();

        // Setup configuration
        _mockConfiguration
            .Setup(c => c["JwtSettings:SecretKey"])
            .Returns("TestSecretKeyForNightscout");

        _authorizationService = new AuthorizationService(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockSubjectService.Object,
            _mockRoleService.Object,
            _mockJwtService.Object,
            _dbContext
        );
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    #region Subject CRUD Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAllSubjectsAsync_ReturnsSubjectsFromService()
    {
        // Arrange
        var authSubjects = new List<AuthSubjectModel>
        {
            new AuthSubjectModel
            {
                Id = Guid.NewGuid(),
                Name = "Test Subject 1",
                Type = SubjectType.Device,
                IsActive = true,
                Roles = new List<AuthRoleModel>
                {
                    new AuthRoleModel { Name = "api", Permissions = new List<string> { "api:*" } }
                }
            },
            new AuthSubjectModel
            {
                Id = Guid.NewGuid(),
                Name = "Test Subject 2",
                Type = SubjectType.User,
                Email = "test@example.com",
                IsActive = true,
                Roles = new List<AuthRoleModel>()
            }
        };

        _mockSubjectService
            .Setup(s => s.GetSubjectsAsync(null))
            .ReturnsAsync(authSubjects);

        // Act
        var result = await _authorizationService.GetAllSubjectsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("Test Subject 1", result[0].Name);
        Assert.Equal("Test Subject 2", result[1].Name);
        _mockSubjectService.Verify(s => s.GetSubjectsAsync(null), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSubjectByIdAsync_WithValidId_ReturnsSubject()
    {
        // Arrange
        var subjectId = Guid.NewGuid();
        var authSubject = new AuthSubjectModel
        {
            Id = subjectId,
            Name = "Test Subject",
            Type = SubjectType.Device,
            IsActive = true,
            Roles = new List<AuthRoleModel>()
        };

        _mockSubjectService
            .Setup(s => s.GetSubjectByIdAsync(subjectId))
            .ReturnsAsync(authSubject);

        // Act
        var result = await _authorizationService.GetSubjectByIdAsync(subjectId.ToString());

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Subject", result.Name);
        Assert.Equal(subjectId.ToString(), result.Id);
        _mockSubjectService.Verify(s => s.GetSubjectByIdAsync(subjectId), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSubjectByIdAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        var subjectId = Guid.NewGuid();

        _mockSubjectService
            .Setup(s => s.GetSubjectByIdAsync(subjectId))
            .ReturnsAsync((AuthSubjectModel?)null);

        // Act
        var result = await _authorizationService.GetSubjectByIdAsync(subjectId.ToString());

        // Assert
        Assert.Null(result);
        _mockSubjectService.Verify(s => s.GetSubjectByIdAsync(subjectId), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSubjectByIdAsync_WithInvalidGuidFormat_ReturnsNull()
    {
        // Arrange
        var invalidId = "not-a-valid-guid";

        // Act
        var result = await _authorizationService.GetSubjectByIdAsync(invalidId);

        // Assert
        Assert.Null(result);
        _mockSubjectService.Verify(s => s.GetSubjectByIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateSubjectAsync_CreatesAndReturnsSubjectWithAccessToken()
    {
        // Arrange
        var legacySubject = new LegacySubject
        {
            Name = "New Device Subject",
            Roles = new List<string> { "api" }
        };

        var createdSubject = new AuthSubjectModel
        {
            Id = Guid.NewGuid(),
            Name = "New Device Subject",
            Type = SubjectType.Service,
            IsActive = true,
            Roles = new List<AuthRoleModel>()
        };

        var creationResult = new SubjectCreationResult
        {
            Subject = createdSubject,
            AccessToken = "generated-access-token-12345"
        };

        _mockSubjectService
            .Setup(s => s.CreateSubjectAsync(It.IsAny<AuthSubjectModel>()))
            .ReturnsAsync(creationResult);

        _mockSubjectService
            .Setup(s => s.AssignRoleAsync(createdSubject.Id, "api", null))
            .ReturnsAsync(true);

        _mockSubjectService
            .Setup(s => s.GetSubjectRolesAsync(createdSubject.Id))
            .ReturnsAsync(new List<string> { "api" });

        // Act
        var result = await _authorizationService.CreateSubjectAsync(legacySubject);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("New Device Subject", result.Name);
        Assert.Equal("generated-access-token-12345", result.AccessToken);
        Assert.Contains("api", result.Roles);
        _mockSubjectService.Verify(s => s.CreateSubjectAsync(It.IsAny<AuthSubjectModel>()), Times.Once);
        _mockSubjectService.Verify(s => s.AssignRoleAsync(createdSubject.Id, "api", null), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateSubjectAsync_WithExistingSubject_ReturnsUpdated()
    {
        // Arrange
        var subjectId = Guid.NewGuid();
        var legacySubject = new LegacySubject
        {
            Id = subjectId.ToString(),
            Name = "Updated Subject Name",
            Notes = "Updated notes",
            Roles = new List<string> { "admin" }
        };

        var existingSubject = new AuthSubjectModel
        {
            Id = subjectId,
            Name = "Original Subject Name",
            Type = SubjectType.Device,
            IsActive = true,
            Roles = new List<AuthRoleModel>()
        };

        var updatedSubject = new AuthSubjectModel
        {
            Id = subjectId,
            Name = "Updated Subject Name",
            Notes = "Updated notes",
            Type = SubjectType.Device,
            IsActive = true,
            Roles = new List<AuthRoleModel>()
        };

        _mockSubjectService
            .Setup(s => s.GetSubjectByIdAsync(subjectId))
            .ReturnsAsync(existingSubject);

        _mockSubjectService
            .Setup(s => s.UpdateSubjectAsync(It.IsAny<AuthSubjectModel>()))
            .ReturnsAsync(updatedSubject);

        _mockSubjectService
            .Setup(s => s.GetSubjectRolesAsync(subjectId))
            .ReturnsAsync(new List<string>());

        _mockSubjectService
            .Setup(s => s.AssignRoleAsync(subjectId, "admin", null))
            .ReturnsAsync(true);

        // Act
        var result = await _authorizationService.UpdateSubjectAsync(legacySubject);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Updated Subject Name", result.Name);
        _mockSubjectService.Verify(s => s.UpdateSubjectAsync(It.IsAny<AuthSubjectModel>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateSubjectAsync_WithNonExistentSubject_ReturnsNull()
    {
        // Arrange
        var subjectId = Guid.NewGuid();
        var legacySubject = new LegacySubject
        {
            Id = subjectId.ToString(),
            Name = "Non-existent Subject"
        };

        _mockSubjectService
            .Setup(s => s.GetSubjectByIdAsync(subjectId))
            .ReturnsAsync((AuthSubjectModel?)null);

        // Act
        var result = await _authorizationService.UpdateSubjectAsync(legacySubject);

        // Assert
        Assert.Null(result);
        _mockSubjectService.Verify(s => s.UpdateSubjectAsync(It.IsAny<AuthSubjectModel>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeleteSubjectAsync_WithValidId_ReturnsTrue()
    {
        // Arrange
        var subjectId = Guid.NewGuid();

        _mockSubjectService
            .Setup(s => s.DeleteSubjectAsync(subjectId))
            .ReturnsAsync(true);

        // Act
        var result = await _authorizationService.DeleteSubjectAsync(subjectId.ToString());

        // Assert
        Assert.True(result);
        _mockSubjectService.Verify(s => s.DeleteSubjectAsync(subjectId), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeleteSubjectAsync_WithNonExistentId_ReturnsFalse()
    {
        // Arrange
        var subjectId = Guid.NewGuid();

        _mockSubjectService
            .Setup(s => s.DeleteSubjectAsync(subjectId))
            .ReturnsAsync(false);

        // Act
        var result = await _authorizationService.DeleteSubjectAsync(subjectId.ToString());

        // Assert
        Assert.False(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DeleteSubjectAsync_WithInvalidGuidFormat_ReturnsFalse()
    {
        // Arrange
        var invalidId = "not-a-valid-guid";

        // Act
        var result = await _authorizationService.DeleteSubjectAsync(invalidId);

        // Assert
        Assert.False(result);
        _mockSubjectService.Verify(s => s.DeleteSubjectAsync(It.IsAny<Guid>()), Times.Never);
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
