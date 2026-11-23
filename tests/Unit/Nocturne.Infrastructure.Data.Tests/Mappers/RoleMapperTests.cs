using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

public class RoleMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_CopiesEveryField()
    {
        var created = new DateTime(2026, 3, 1, 1, 2, 3, DateTimeKind.Utc);
        var updated = created.AddDays(1);
        var entity = new RoleEntity
        {
            Id = Guid.CreateVersion7(),
            Name = "careportal",
            Description = "Records treatments",
            Permissions = ["api:treatments:create", "api:entries:read"],
            IsSystemRole = true,
            CreatedAt = created,
            UpdatedAt = updated,
        };

        var role = RoleMapper.ToDomainModel(entity);

        role.Id.Should().Be(entity.Id);
        role.Name.Should().Be("careportal");
        role.Description.Should().Be("Records treatments");
        role.Permissions.Should().Equal("api:treatments:create", "api:entries:read");
        role.IsSystemRole.Should().BeTrue();
        role.CreatedAt.Should().Be(created);
        role.UpdatedAt.Should().Be(updated);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_ReadsANullPermissionListAsNoPermissions()
    {
        var entity = new RoleEntity { Id = Guid.CreateVersion7(), Name = "legacy", Permissions = null! };

        var role = RoleMapper.ToDomainModel(entity);

        role.Permissions.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_CopiesThePermissionList()
    {
        var entity = new RoleEntity { Id = Guid.CreateVersion7(), Name = "r", Permissions = ["a"] };

        var role = RoleMapper.ToDomainModel(entity);
        entity.Permissions.Add("b");

        role.Permissions.Should().Equal("a");
    }
}
