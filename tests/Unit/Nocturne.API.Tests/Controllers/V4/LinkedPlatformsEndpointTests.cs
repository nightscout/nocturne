using Nocturne.Tests.Shared.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Chat;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class LinkedPlatformsEndpointTests : IDisposable
{
    private readonly Mock<ITenantAccessor> _tenantAccessorMock = new();
    private readonly Mock<ILogger<ChatIdentityDirectoryService>> _loggerMock = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;

    public LinkedPlatformsEndpointTests()
    {
        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _tenantAccessorMock.Setup(t => t.TenantId).Returns(_tenantId);
    }

    public void Dispose()
    {
        // In-memory database is disposed when options go out of scope
    }

    private NocturneDbContext CreateDbContext() => new(_dbOptions);

    private LinkedPlatformsController CreateController(
        ChatIdentityDirectoryService directory)
    {
        var controller = new LinkedPlatformsController(directory, _tenantAccessorMock.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private ChatIdentityDirectoryService CreateDirectoryService()
    {
        var factoryMock = new Mock<IDbContextFactory<NocturneDbContext>>();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateDbContext());
        return new ChatIdentityDirectoryService(factoryMock.Object.AsNocturneFactory(), TimeProvider.System, _loggerMock.Object);
    }

    [Fact]
    public async Task GetLinkedPlatforms_ReturnsDistinctPlatforms_WhenEntriesExist()
    {
        // Arrange
        await using (var db = CreateDbContext())
        {
            db.ChatIdentityDirectory.AddRange(
                new ChatIdentityDirectoryEntry
                {
                    Id = Guid.NewGuid(),
                    Platform = "discord",
                    PlatformUserId = "user1",
                    TenantId = _tenantId,
                    NocturneUserId = Guid.NewGuid(),
                    Label = "main",
                    DisplayName = "Main",
                    IsDefault = true,
                    IsActive = true,
                },
                new ChatIdentityDirectoryEntry
                {
                    Id = Guid.NewGuid(),
                    Platform = "discord",
                    PlatformUserId = "user2",
                    TenantId = _tenantId,
                    NocturneUserId = Guid.NewGuid(),
                    Label = "alt",
                    DisplayName = "Alt",
                    IsDefault = false,
                    IsActive = true,
                },
                new ChatIdentityDirectoryEntry
                {
                    Id = Guid.NewGuid(),
                    Platform = "telegram",
                    PlatformUserId = "user3",
                    TenantId = _tenantId,
                    NocturneUserId = Guid.NewGuid(),
                    Label = "tg",
                    DisplayName = "Telegram",
                    IsDefault = true,
                    IsActive = true,
                });
            await db.SaveChangesAsync();
        }

        var directory = CreateDirectoryService();
        var controller = CreateController(directory);

        // Act
        var result = await controller.GetLinkedPlatforms(CancellationToken.None);

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<LinkedPlatformsResponse>().Subject;
        response.Platforms.Should().HaveCount(2);
        response.Platforms.Should().Contain("discord");
        response.Platforms.Should().Contain("telegram");
    }

    [Fact]
    public async Task GetLinkedPlatforms_ReturnsEmptyList_WhenNoEntriesExist()
    {
        // Arrange
        var directory = CreateDirectoryService();
        var controller = CreateController(directory);

        // Act
        var result = await controller.GetLinkedPlatforms(CancellationToken.None);

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<LinkedPlatformsResponse>().Subject;
        response.Platforms.Should().BeEmpty();
    }
}
