using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Converts Unix milliseconds to local seconds-from-midnight using the patient's timezone.
/// Shared across all schedule resolvers; takes repositories directly (not
/// <see cref="Core.Contracts.Profiles.Resolvers.ITherapySettingsResolver"/>) to avoid a
/// circular dependency with that resolver. Timezone precedence matches
/// <c>TherapySettingsResolver.GetTimezoneAsync</c>: canonical
/// <see cref="Core.Models.V4.PatientRecord.Timezone"/> first, then the legacy per-profile
/// <c>TherapySettings.Timezone</c>.
/// </summary>
internal static class ScheduleTimeHelper
{
    /// <summary>
    /// Converts Unix milliseconds to seconds-from-midnight in the patient's local timezone.
    /// Prefers <see cref="IPatientRecordRepository"/>, falls back to the per-profile
    /// <see cref="ITherapySettingsRepository"/> timezone, then UTC when neither is configured.
    /// </summary>
    public static async Task<int> GetSecondsFromMidnightAsync(
        long timeMills,
        string profileName,
        DateTime timestamp,
        ITherapySettingsRepository therapyRepo,
        IPatientRecordRepository patientRecordRepo,
        CancellationToken ct)
    {
        var patient = await patientRecordRepo.GetAsync(ct);
        var timezone = patient?.Timezone;

        if (string.IsNullOrEmpty(timezone))
        {
            var therapy = await therapyRepo.GetActiveAtAsync(profileName, timestamp, ct);
            timezone = therapy?.Timezone;
        }

        var dto = DateTimeOffset.FromUnixTimeMilliseconds(timeMills);

        if (!string.IsNullOrEmpty(timezone))
            dto = TimeZoneInfo.ConvertTime(dto, TimeZoneHelper.GetTimeZoneInfoFromId(timezone));

        return (int)dto.TimeOfDay.TotalSeconds;
    }
}
