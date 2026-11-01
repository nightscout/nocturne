using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Role = Nocturne.Core.Models.Authorization.Role;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Seeding the default roles creates the roles that are missing and leaves every existing role as an admin set it.
/// </summary>
[Trait("Category", "Unit")]
public class RoleServiceTests
{
    [Fact]
    public async Task InitializeDefaultRoles_CreatesMissingRolesAndKeepsExistingSystemRolePermissions()
    {
        var edited = new Role
        {
            Id = Guid.CreateVersion7(),
            Name = "readable",
            Description = "Edited",
            Permissions = ["api:entries:read"],
            IsSystemRole = true,
        };
        var roles = new Mock<IRoleRepository>();
        roles.Setup(r => r.FindByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) => name == "readable" ? edited : null);
        roles.Setup(r => r.CreateAsync(It.IsAny<NewRole>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NewRole n, CancellationToken _) => new Role { Id = Guid.CreateVersion7(), Name = n.Name });
        var service = new RoleService(roles.Object, NullLogger<RoleService>.Instance);

        var created = await service.InitializeDefaultRolesAsync();

        created.Should().Be(5);
        roles.Verify(r => r.CreateAsync(It.Is<NewRole>(n => n.Name == "admin" && n.IsSystemRole), It.IsAny<CancellationToken>()), Times.Once);
        roles.Verify(r => r.CreateAsync(It.Is<NewRole>(n => n.Name == "readable"), It.IsAny<CancellationToken>()), Times.Never);
        roles.Verify(r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
