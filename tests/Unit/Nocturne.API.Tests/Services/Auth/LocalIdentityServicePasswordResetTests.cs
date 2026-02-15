using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services;
using Nocturne.Core.Models.Configuration;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Unit tests for admin password reset flows
/// </summary>
public class LocalIdentityServicePasswordResetTests
{
    private readonly NocturneDbContext _dbContext;
    private readonly LocalIdentityService _service;

    public LocalIdentityServicePasswordResetTests()
    {
        _dbContext = TestDbContextFactory.CreateInMemoryContext();

        var subjectService = new Mock<ISubjectService>();
        var emailService = new Mock<IEmailService>();
        var signalRBroadcastService = new Mock<ISignalRBroadcastService>();

        var options = Options.Create(
            new LocalIdentityOptions
            {
                Password = new PasswordSettings
                {
                    MinLength = 4,
                    MaxLength = 128,
                    RequireUppercase = false,
                    RequireLowercase = false,
                    RequireDigit = false,
                },
            }
        );
        var emailOptions = Options.Create(new EmailOptions());
        var logger = new Mock<ILogger<LocalIdentityService>>();

        _service = new LocalIdentityService(
            _dbContext,
            subjectService.Object,
            emailService.Object,
            signalRBroadcastService.Object,
            options,
            emailOptions,
            logger.Object
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetTemporaryPasswordAsync_ValidUser_SetsPasswordAndRequiresChange()
    {
        var user = CreateTestUser();
        var adminId = Guid.NewGuid();

        var result = await _service.SetTemporaryPasswordAsync(user.Id, "temp123", adminId);

        result.Should().BeTrue();
        var updatedUser = await _dbContext.LocalUsers.FindAsync(user.Id);
        updatedUser.Should().NotBeNull();
        updatedUser!.RequirePasswordChange.Should().BeTrue();
        updatedUser.PasswordHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetTemporaryPasswordAsync_EmptyPassword_AllowsEmptyAndRequiresChange()
    {
        var user = CreateTestUser();
        var adminId = Guid.NewGuid();

        var result = await _service.SetTemporaryPasswordAsync(user.Id, "", adminId);

        result.Should().BeTrue();
        var updatedUser = await _dbContext.LocalUsers.FindAsync(user.Id);
        updatedUser.Should().NotBeNull();
        updatedUser!.RequirePasswordChange.Should().BeTrue();
        updatedUser.PasswordHash.Should().NotBeNullOrEmpty();
    }

    private LocalUserEntity CreateTestUser()
    {
        var email = "user@example.com";
        var user = new LocalUserEntity
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Test User",
            IsActive = true,
            EmailVerified = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _dbContext.LocalUsers.Add(user);
        _dbContext.SaveChanges();

        return user;
    }
}
