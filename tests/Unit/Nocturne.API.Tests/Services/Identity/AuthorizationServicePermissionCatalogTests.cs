using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The v2 permission catalogue is read from the roles, so a role store that fails must surface as a
/// server error, not as a 200 listing no permissions at all.
/// </summary>
public class AuthorizationServicePermissionCatalogTests : IDisposable
{
    private const string Failure = "role store unavailable";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly AuthorizationService _authorizationService;

    public AuthorizationServicePermissionCatalogTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _dbContext = _db.CreateContext();

        var roles = new Mock<IRoleService>();
        roles.Setup(r => r.GetAllRolesAsync()).ThrowsAsync(new InvalidOperationException(Failure));

        _authorizationService = new AuthorizationService(
            new Mock<IConfiguration>().Object,
            Mock.Of<ILogger<AuthorizationService>>(),
            new Mock<ISubjectService>().Object,
            roles.Object,
            new Mock<IDirectGrantService>().Object,
            new OAuthGrantRepository(_dbContext.AsFactory()),
            new Mock<IJwtService>().Object,
            new SubjectRepository(_dbContext.AsFactory()),
            new Nocturne.Infrastructure.Data.Services.CategoryReadContext(),
            TimeProvider.System
        );
    }

    public void Dispose()
    {
        _authorizationService.Dispose();
        _dbContext.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task GetAllPermissionsAsync_throws_when_the_roles_cannot_be_read()
    {
        var read = () => _authorizationService.GetAllPermissionsAsync();

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage(Failure);
    }

    [Fact]
    public async Task GetPermissionTrieAsync_throws_when_the_roles_cannot_be_read()
    {
        var read = () => _authorizationService.GetPermissionTrieAsync();

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage(Failure);
    }
}
