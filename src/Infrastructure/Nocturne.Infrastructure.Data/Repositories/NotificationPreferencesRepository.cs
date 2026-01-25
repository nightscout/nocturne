using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// PostgreSQL repository for NotificationPreferences operations
/// </summary>
public class NotificationPreferencesRepository
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the NotificationPreferencesRepository class
    /// </summary>
    /// <param name="context">The database context</param>
    public NotificationPreferencesRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get notification preferences for a specific user
    /// </summary>
    public virtual async Task<NotificationPreferencesEntity?> GetPreferencesForUserAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        return await _context.NotificationPreferences.FirstOrDefaultAsync(
            p => p.UserId == userId,
            cancellationToken
        );
    }

    /// <summary>
    /// Create or update notification preferences for a user
    /// </summary>
    public async Task<NotificationPreferencesEntity> UpsertPreferencesAsync(
        NotificationPreferencesEntity preferences,
        CancellationToken cancellationToken = default
    )
    {
        var existing = await _context.NotificationPreferences.FirstOrDefaultAsync(
            p => p.UserId == preferences.UserId,
            cancellationToken
        );

        if (existing != null)
        {
            // Update existing preferences
            existing.EmailEnabled = preferences.EmailEnabled;
            existing.EmailAddress = preferences.EmailAddress;
            existing.PushoverEnabled = preferences.PushoverEnabled;
            existing.PushoverUserKey = preferences.PushoverUserKey;
            existing.PushoverDevices = preferences.PushoverDevices;
            existing.SmsEnabled = preferences.SmsEnabled;
            existing.SmsPhoneNumber = preferences.SmsPhoneNumber;
            if (preferences.WebhookUrls != null)
            {
                existing.WebhookEnabled = preferences.WebhookEnabled;
                existing.WebhookUrls = preferences.WebhookUrls;
            }
            existing.QuietHoursStart = preferences.QuietHoursStart;
            existing.QuietHoursEnd = preferences.QuietHoursEnd;
            existing.QuietHoursEnabled = preferences.QuietHoursEnabled;
            existing.EmergencyOverrideQuietHours = preferences.EmergencyOverrideQuietHours;
            existing.PushEnabled = preferences.PushEnabled;
            existing.BatteryLowThreshold = preferences.BatteryLowThreshold;
            existing.SensorExpirationWarningHours = preferences.SensorExpirationWarningHours;
            existing.DataGapWarningMinutes = preferences.DataGapWarningMinutes;
            existing.CalibrationReminderHours = preferences.CalibrationReminderHours;
            existing.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
            return existing;
        }
        else
        {
            // Create new preferences
            preferences.Id = Guid.CreateVersion7();
            preferences.CreatedAt = DateTime.UtcNow;
            preferences.UpdatedAt = DateTime.UtcNow;

            _context.NotificationPreferences.Add(preferences);
            await _context.SaveChangesAsync(cancellationToken);
            return preferences;
        }
    }

    /// <summary>
    /// Delete notification preferences for a user
    /// </summary>
    public async Task<bool> DeletePreferencesAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var preferences = await _context.NotificationPreferences.FirstOrDefaultAsync(
            p => p.UserId == userId,
            cancellationToken
        );

        if (preferences == null)
            return false;

        _context.NotificationPreferences.Remove(preferences);
        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    /// <summary>
    /// Update quiet hours for a user
    /// </summary>
    public async Task<bool> UpdateQuietHoursAsync(
        string userId,
        bool enableQuietHours,
        TimeOnly? quietHoursStart = null,
        TimeOnly? quietHoursEnd = null,
        bool? emergencyOverride = null,
        CancellationToken cancellationToken = default
    )
    {
        var preferences = await _context.NotificationPreferences.FirstOrDefaultAsync(
            p => p.UserId == userId,
            cancellationToken
        );

        if (preferences == null)
            return false;

        preferences.QuietHoursEnabled = enableQuietHours;
        if (quietHoursStart.HasValue)
            preferences.QuietHoursStart = quietHoursStart.Value;
        if (quietHoursEnd.HasValue)
            preferences.QuietHoursEnd = quietHoursEnd.Value;
        if (emergencyOverride.HasValue)
            preferences.EmergencyOverrideQuietHours = emergencyOverride.Value;
        preferences.UpdatedAt = DateTime.UtcNow;

        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    /// <summary>
    /// Update Pushover settings for a user
    /// </summary>
    public async Task<bool> UpdatePushoverSettingsAsync(
        string userId,
        bool enablePushover,
        string? userKey = null,
        string? devices = null,
        CancellationToken cancellationToken = default
    )
    {
        var preferences = await _context.NotificationPreferences.FirstOrDefaultAsync(
            p => p.UserId == userId,
            cancellationToken
        );

        if (preferences == null)
            return false;

        preferences.PushoverEnabled = enablePushover;
        if (userKey != null)
            preferences.PushoverUserKey = userKey;
        if (devices != null)
            preferences.PushoverDevices = devices;
        preferences.UpdatedAt = DateTime.UtcNow;

        var result = await _context.SaveChangesAsync(cancellationToken);
        return result > 0;
    }

    /// <summary>
    /// Check if a user is in quiet hours
    /// </summary>
    public virtual async Task<bool> IsUserInQuietHoursAsync(
        string userId,
        DateTime? checkTime = null,
        CancellationToken cancellationToken = default
    )
    {
        var preferences = await _context.NotificationPreferences.FirstOrDefaultAsync(
            p => p.UserId == userId,
            cancellationToken
        );

        if (preferences == null || !preferences.QuietHoursEnabled)
            return false;

        if (!preferences.QuietHoursStart.HasValue || !preferences.QuietHoursEnd.HasValue)
            return false;

        var now = checkTime?.TimeOfDay ?? DateTime.UtcNow.TimeOfDay;
        var start = preferences.QuietHoursStart.Value.ToTimeSpan();
        var end = preferences.QuietHoursEnd.Value.ToTimeSpan();

        // Handle quiet hours that span midnight
        if (start <= end)
        {
            return now >= start && now <= end;
        }
        else
        {
            return now >= start || now <= end;
        }
    }
}
