using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Service to seed default tracker definitions for new users
/// </summary>
public interface ITrackerSeedService
{
    /// <summary>
    /// Create default tracker definitions for a new user
    /// </summary>
    Task SeedDefaultDefinitionsAsync(string userId, CancellationToken cancellationToken = default);
}

public class TrackerSeedService : ITrackerSeedService
{
    private readonly TrackerRepository _repository;
    private readonly ILogger<TrackerSeedService> _logger;

    public TrackerSeedService(TrackerRepository repository, ILogger<TrackerSeedService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task SeedDefaultDefinitionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        // Check if user already has definitions
        var existing = await _repository.GetDefinitionsForUserAsync(userId, cancellationToken);
        if (existing.Any())
        {
            _logger.LogDebug("User {UserId} already has tracker definitions, skipping seed", userId);
            return;
        }

        _logger.LogInformation("Seeding default tracker definitions for user {UserId}", userId);

        var defaults = GetDefaultDefinitions(userId);
        foreach (var definition in defaults)
        {
            await _repository.CreateDefinitionAsync(definition, cancellationToken);
        }

        _logger.LogInformation("Seeded {Count} default tracker definitions for user {UserId}", defaults.Length, userId);
    }

    private static TrackerDefinitionEntity[] GetDefaultDefinitions(string userId) =>
    [
        // CGM Sensors
        new TrackerDefinitionEntity
        {
            UserId = userId,
            Name = "Dexcom G7 Sensor",
            Description = "10-day CGM sensor",
            Category = TrackerCategory.Consumable,
            Icon = "activity",
            TriggerEventTypes = "[\"Sensor Start\",\"Sensor Change\"]",
            LifespanHours = 240, // 10 days
            InfoHours = 192,    // Day 8
            WarnHours = 216,    // Day 9
            HazardHours = 234,  // Day 9.75
            UrgentHours = 240,  // Day 10
            IsFavorite = true,
        },
        new TrackerDefinitionEntity
        {
            UserId = userId,
            Name = "Dexcom G6 Sensor",
            Description = "10-day CGM sensor",
            Category = TrackerCategory.Consumable,
            Icon = "activity",
            TriggerEventTypes = "[\"Sensor Start\",\"Sensor Change\"]",
            LifespanHours = 240,
            InfoHours = 192,
            WarnHours = 216,
            HazardHours = 234,
            UrgentHours = 240,
            IsFavorite = false,
        },
        new TrackerDefinitionEntity
        {
            UserId = userId,
            Name = "Libre 3 Sensor",
            Description = "14-day CGM sensor",
            Category = TrackerCategory.Consumable,
            Icon = "activity",
            TriggerEventTypes = "[\"Sensor Start\",\"Sensor Change\"]",
            LifespanHours = 336, // 14 days
            InfoHours = 288,    // Day 12
            WarnHours = 312,    // Day 13
            HazardHours = 330,  // Day 13.75
            UrgentHours = 336,  // Day 14
            IsFavorite = false,
        },

        // Pump Consumables
        new TrackerDefinitionEntity
        {
            UserId = userId,
            Name = "Omnipod 5",
            Description = "3-day insulin pod",
            Category = TrackerCategory.Consumable,
            Icon = "syringe",
            TriggerEventTypes = "[\"Site Change\",\"Pump Resume\"]",
            LifespanHours = 80, // Pod expires at 80h
            InfoHours = 60,    // 2.5 days
            WarnHours = 72,    // 3 days
            HazardHours = 76,  // 3.17 days
            UrgentHours = 79,  // Just before expiry
            IsFavorite = true,
        },
        new TrackerDefinitionEntity
        {
            UserId = userId,
            Name = "Tandem Infusion Set",
            Description = "3-day infusion set",
            Category = TrackerCategory.Consumable,
            Icon = "syringe",
            TriggerEventTypes = "[\"Site Change\",\"Cannula Change\"]",
            LifespanHours = 72,
            InfoHours = 48,
            WarnHours = 66,
            HazardHours = 70,
            UrgentHours = 72,
            IsFavorite = false,
        },
        new TrackerDefinitionEntity
        {
            UserId = userId,
            Name = "Insulin Reservoir",
            Description = "3-day reservoir",
            Category = TrackerCategory.Reservoir,
            Icon = "beaker",
            TriggerEventTypes = "[\"Insulin Change\",\"Reservoir Change\"]",
            LifespanHours = 72,
            InfoHours = 48,
            WarnHours = 66,
            HazardHours = 70,
            UrgentHours = 72,
            IsFavorite = false,
        },

        // Appointments
        new TrackerDefinitionEntity
        {
            UserId = userId,
            Name = "Endocrinologist Visit",
            Description = "Quarterly endo appointment",
            Category = TrackerCategory.Appointment,
            Icon = "calendar",
            TriggerEventTypes = "[]",
            LifespanHours = 2160, // 90 days
            InfoHours = 2088,    // 87 days (1 week notice)
            WarnHours = 2136,    // 89 days (1 day notice)
            IsFavorite = false,
        },

        // Reminders
        new TrackerDefinitionEntity
        {
            UserId = userId,
            Name = "A1C Lab Work",
            Description = "Quarterly A1C test",
            Category = TrackerCategory.Reminder,
            Icon = "clock",
            TriggerEventTypes = "[]",
            LifespanHours = 2160, // 90 days
            InfoHours = 2088,
            WarnHours = 2136,
            IsFavorite = false,
        },
    ];
}
