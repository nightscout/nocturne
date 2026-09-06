using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V2;
using Nocturne.Core.Contracts.Identity;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Tests for authorization management endpoints in AuthorizationController
/// </summary>
public class AuthorizationManagementControllerTests
{
    private readonly Mock<IAuthorizationService> _mockAuthorizationService;
    private readonly Mock<ILogger<AuthorizationController>> _mockLogger;
    private readonly AuthorizationController _controller;

    public AuthorizationManagementControllerTests()
    {
        _mockAuthorizationService = new Mock<IAuthorizationService>();
        _mockLogger = new Mock<ILogger<AuthorizationController>>();
        _controller = new AuthorizationController(
            _mockAuthorizationService.Object,
            _mockLogger.Object
        );
    }

    #region Subject Management Tests

    [Fact]
    public async Task GetAllSubjects_ReturnsListOfSubjects()
    {
        // Arrange
        var subjects = new List<Subject>
        {
            new()
            {
                Id = "1",
                Name = "Test Subject 1",
                AccessToken = "token1",
            },
            new()
            {
                Id = "2",
                Name = "Test Subject 2",
                AccessToken = "token2",
            },
        };
        _mockAuthorizationService.Setup(s => s.GetAllSubjectsAsync()).ReturnsAsync(subjects);

        // Act
        var result = await _controller.GetAllSubjects();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedSubjects = Assert.IsType<List<Subject>>(okResult.Value);
        Assert.Equal(2, returnedSubjects.Count);
        Assert.Equal("Test Subject 1", returnedSubjects[0].Name);
    }

    [Fact]
    public async Task CreateSubject_WithValidSubject_ReturnsCreatedSubject()
    {
        // Arrange
        var inputSubject = new Subject
        {
            Name = "New Subject",
            Roles = new List<string> { "admin" },
        };
        var createdSubject = new Subject
        {
            Id = "12345",
            Name = "New Subject",
            Roles = new List<string> { "admin" },
            AccessToken = "generated-token",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        };
        _mockAuthorizationService
            .Setup(s => s.CreateSubjectAsync(It.IsAny<Subject>()))
            .ReturnsAsync(createdSubject);

        // Act
        var result = await _controller.CreateSubject(inputSubject);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var returnedSubject = Assert.IsType<Subject>(createdResult.Value);
        Assert.Equal("New Subject", returnedSubject.Name);
        Assert.Equal("generated-token", returnedSubject.AccessToken);
        Assert.NotNull(returnedSubject.Id);
    }

    [Fact]
    public async Task CreateSubject_WithInvalidModel_ReturnsBadRequest()
    {
        // Arrange
        var invalidSubject = new Subject(); // Missing required Name
        _controller.ModelState.AddModelError("Name", "The Name field is required.");

        // Act
        var result = await _controller.CreateSubject(invalidSubject);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateSubject_WithValidSubject_ReturnsUpdatedSubject()
    {
        // Arrange
        var subjectToUpdate = new Subject
        {
            Id = "12345",
            Name = "Updated Subject",
            Roles = new List<string> { "user" },
        };
        _mockAuthorizationService
            .Setup(s => s.UpdateSubjectAsync(It.IsAny<Subject>()))
            .ReturnsAsync(subjectToUpdate);

        // Act
        var result = await _controller.UpdateSubject(subjectToUpdate);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedSubject = Assert.IsType<Subject>(okResult.Value);
        Assert.Equal("Updated Subject", returnedSubject.Name);
        Assert.Equal("12345", returnedSubject.Id);
    }

    [Fact]
    public async Task UpdateSubject_WithMissingId_ReturnsBadRequest()
    {
        // Arrange
        var subjectWithoutId = new Subject { Name = "Test Subject" };

        // Act
        var result = await _controller.UpdateSubject(subjectWithoutId);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task UpdateSubject_WhenSubjectNotFound_ReturnsNotFound()
    {
        // Arrange
        var subjectToUpdate = new Subject { Id = "nonexistent", Name = "Test Subject" };
        _mockAuthorizationService
            .Setup(s => s.UpdateSubjectAsync(It.IsAny<Subject>()))
            .ReturnsAsync((Subject?)null);

        // Act
        var result = await _controller.UpdateSubject(subjectToUpdate);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task DeleteSubject_WithExistingId_ReturnsNoContent()
    {
        // Arrange
        var subjectId = "12345";
        _ = _mockAuthorizationService
            .Setup(s => s.DeleteSubjectAsync(subjectId))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.DeleteSubject(subjectId);

        // Assert
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteSubject_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        var subjectId = "nonexistent";
        _mockAuthorizationService.Setup(s => s.DeleteSubjectAsync(subjectId)).ReturnsAsync(false);

        // Act
        var result = await _controller.DeleteSubject(subjectId);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    #endregion

    #region Role Management Tests

    [Fact]
    public async Task GetAllRoles_ReturnsListOfRoles()
    {
        // Arrange
        var roles = new List<Role>
        {
            new()
            {
                Id = "1",
                Name = "admin",
                Permissions = new List<string> { "*" },
            },
            new()
            {
                Id = "2",
                Name = "user",
                Permissions = new List<string> { "api:*:read" },
            },
        };
        _mockAuthorizationService.Setup(s => s.GetAllRolesAsync()).ReturnsAsync(roles);

        // Act
        var result = await _controller.GetAllRoles();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedRoles = Assert.IsType<List<Role>>(okResult.Value);
        Assert.Equal(2, returnedRoles.Count);
        Assert.Equal("admin", returnedRoles[0].Name);
    }

    [Fact]
    public async Task CreateRole_WithValidRole_ReturnsCreatedRole()
    {
        // Arrange
        var inputRole = new Role
        {
            Name = "moderator",
            Permissions = new List<string> { "api:*:read", "api:treatments:*" },
        };
        var createdRole = new Role
        {
            Id = "67890",
            Name = "moderator",
            Permissions = new List<string> { "api:*:read", "api:treatments:*" },
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        };
        _mockAuthorizationService
            .Setup(s => s.CreateRoleAsync(It.IsAny<Role>()))
            .ReturnsAsync(createdRole);

        // Act
        var result = await _controller.CreateRole(inputRole);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var returnedRole = Assert.IsType<Role>(createdResult.Value);
        Assert.Equal("moderator", returnedRole.Name);
        Assert.NotNull(returnedRole.Id);
    }

    [Fact]
    public async Task CreateRole_WithInvalidModel_ReturnsBadRequest()
    {
        // Arrange
        var invalidRole = new Role(); // Missing required Name
        _controller.ModelState.AddModelError("Name", "The Name field is required.");

        // Act
        var result = await _controller.CreateRole(invalidRole);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateRole_WithValidRole_ReturnsUpdatedRole()
    {
        // Arrange
        var roleToUpdate = new Role
        {
            Id = "67890",
            Name = "updated-moderator",
            Permissions = new List<string> { "api:*:read" },
        };
        _mockAuthorizationService
            .Setup(s => s.UpdateRoleAsync(It.IsAny<Role>()))
            .ReturnsAsync(roleToUpdate);

        // Act
        var result = await _controller.UpdateRole(roleToUpdate);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedRole = Assert.IsType<Role>(okResult.Value);
        Assert.Equal("updated-moderator", returnedRole.Name);
        Assert.Equal("67890", returnedRole.Id);
    }

    [Fact]
    public async Task UpdateRole_WithMissingId_ReturnsBadRequest()
    {
        // Arrange
        var roleWithoutId = new Role { Name = "Test Role" };

        // Act
        var result = await _controller.UpdateRole(roleWithoutId);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task UpdateRole_WhenRoleNotFound_ReturnsNotFound()
    {
        // Arrange
        var roleToUpdate = new Role { Id = "nonexistent", Name = "Test Role" };
        _mockAuthorizationService
            .Setup(s => s.UpdateRoleAsync(It.IsAny<Role>()))
            .ReturnsAsync((Role?)null);

        // Act
        var result = await _controller.UpdateRole(roleToUpdate);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task DeleteRole_WithExistingId_ReturnsNoContent()
    {
        // Arrange
        var roleId = "67890";
        _mockAuthorizationService.Setup(s => s.DeleteRoleAsync(roleId)).ReturnsAsync(true);

        // Act
        var result = await _controller.DeleteRole(roleId);

        // Assert
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteRole_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        var roleId = "nonexistent";
        _mockAuthorizationService.Setup(s => s.DeleteRoleAsync(roleId)).ReturnsAsync(false);

        // Act
        var result = await _controller.DeleteRole(roleId);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    #endregion
}
