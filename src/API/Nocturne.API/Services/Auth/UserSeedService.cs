using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Background service that seeds configured user accounts on startup
/// Users are created from configuration if they don't already exist
/// </summary>
public class UserSeedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LocalIdentityOptions _options;
    private readonly ILogger<UserSeedService> _logger;

    public UserSeedService(
        IServiceProvider serviceProvider,
        IOptions<LocalIdentityOptions> options,
        ILogger<UserSeedService> logger
    )
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.SeedUsers.Count == 0)
        {
            _logger.LogDebug("No seed users configured, skipping user account seeding");
            return;
        }

        _logger.LogInformation("Seeding {Count} configured user accounts", _options.SeedUsers.Count);

        foreach (var userConfig in _options.SeedUsers)
        {
            await SeedUserAsync(userConfig);
        }
    }

    private async Task SeedUserAsync(SeedUserOptions userConfig)
    {
        if (string.IsNullOrEmpty(userConfig.Email) || string.IsNullOrEmpty(userConfig.Password))
        {
            _logger.LogWarning("Seed user configuration is incomplete (missing email or password), skipping");
            return;
        }

        // Validate password meets minimum length
        if (userConfig.Password.Length < _options.Password.MinLength)
        {
            _logger.LogError(
                "Seed user password for {Email} does not meet minimum length requirement ({MinLength} characters)",
                userConfig.Email,
                _options.Password.MinLength
            );
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var localIdentityService = scope.ServiceProvider.GetRequiredService<ILocalIdentityService>();
        var subjectService = scope.ServiceProvider.GetRequiredService<ISubjectService>();
        var roleService = scope.ServiceProvider.GetRequiredService<IRoleService>();

        try
        {
            // Check if user already exists
            var existingUser = await localIdentityService.GetUserByEmailAsync(userConfig.Email);
            if (existingUser != null)
            {
                _logger.LogDebug("User account already exists for {Email}", userConfig.Email);
                return;
            }

            _logger.LogInformation(
                "Creating user account for {Email} (Admin: {IsAdmin}, Roles: {Roles})",
                userConfig.Email,
                userConfig.IsAdmin,
                string.Join(", ", userConfig.Roles)
            );

            // Register the user (bypassing allowlist checks)
            var result = await localIdentityService.RegisterAsync(
                userConfig.Email,
                userConfig.Password,
                userConfig.DisplayName,
                skipAllowlistCheck: true,
                autoVerifyEmail: true
            );

            if (!result.Success)
            {
                _logger.LogError("Failed to create user account for {Email}: {Error}", userConfig.Email, result.Error);
                return;
            }

            if (!result.SubjectId.HasValue)
            {
                _logger.LogError("User created but no SubjectId returned for {Email}", userConfig.Email);
                return;
            }

            // Assign admin role if IsAdmin is true
            if (userConfig.IsAdmin)
            {
                // Ensure admin role exists
                var adminRole = await roleService.GetRoleByNameAsync("admin");
                if (adminRole == null)
                {
                    _logger.LogWarning("Admin role does not exist, creating it");
                    adminRole = await roleService.CreateRoleAsync(
                        new Role
                        {
                            Name = "admin",
                            Description = "Full administrative access",
                            Permissions = new List<string> { "*" },
                        }
                    );
                }

                await subjectService.AssignRoleAsync(result.SubjectId.Value, "admin");
                _logger.LogInformation("Assigned admin role to {Email}", userConfig.Email);
            }

            // Assign additional roles
            foreach (var roleName in userConfig.Roles)
            {
                // Ensure role exists
                var role = await roleService.GetRoleByNameAsync(roleName);
                if (role == null)
                {
                    _logger.LogWarning("Role '{RoleName}' does not exist, creating it", roleName);
                    role = await roleService.CreateRoleAsync(
                        new Role
                        {
                            Name = roleName,
                            Description = $"Auto-created role: {roleName}",
                            Permissions = new List<string>(),
                        }
                    );
                }

                await subjectService.AssignRoleAsync(result.SubjectId.Value, roleName);
                _logger.LogDebug("Assigned role '{RoleName}' to {Email}", roleName, userConfig.Email);
            }

            _logger.LogInformation("Successfully seeded user account for {Email}", userConfig.Email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding user account for {Email}", userConfig.Email);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
