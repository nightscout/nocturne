using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Converts Unix milliseconds to local seconds-from-midnight using the profile's timezone
/// from <see cref="ITherapySettingsRepository"/>. Shared across all schedule resolvers
/// to avoid circular dependencies with <see cref="Core.Contracts.Profiles.Resolvers.ITherapySettingsResolver"/>.
/// </summary>
internal static class ScheduleTimeHelper
{
    /// <summary>
    /// Converts Unix milliseconds to seconds-from-midnight in the profile's local timezone.
    /// Falls back to UTC when no timezone is configured.
    /// </summary>
    public static async Task<int> GetSecondsFromMidnightAsync(
        long timeMills,
        string profileName,
        DateTime timestamp,
        ITherapySettingsRepository therapyRepo,
        CancellationToken ct)
    {
        var therapy = await therapyRepo.GetActiveAtAsync(profileName, timestamp, ct);
        var timezone = therapy?.Timezone;

        var dto = DateTimeOffset.FromUnixTimeMilliseconds(timeMills);

        if (!string.IsNullOrEmpty(timezone))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
                dto = TimeZoneInfo.ConvertTime(dto, tz);
            }
            catch (TimeZoneNotFoundException)
            {
                // Fall through to UTC
            }
            catch (InvalidTimeZoneException)
            {
                // Fall through to UTC
            }
        }

        return (int)dto.TimeOfDay.TotalSeconds;
    }
}
