using Nocturne.Core.Models;

namespace Nocturne.Core.Clinical.Schedules;

/// <summary>
/// The timezone a therapy schedule's times of day are read in, and the local time of day in it.
/// </summary>
public static class ScheduleTimeHelper
{
    /// <summary>
    /// The timezone a schedule is read in: the patient's (<see cref="Core.Models.V4.PatientRecord.Timezone"/>,
    /// the canonical one, since a patient lives in one timezone whatever profile is running), else the
    /// one a profile switch was uploaded with, else the stored profile's (connector imports wrote it
    /// there before the patient record held it), else null, which reads as UTC.
    /// </summary>
    /// <remarks>Every resolver reading a schedule at a time, point or batch, takes its timezone from here.</remarks>
    public static string? TimezoneOf(string? patientTimezone, string? switchTimezone, string? storeTimezone) =>
        NonEmpty(patientTimezone) ?? NonEmpty(switchTimezone) ?? NonEmpty(storeTimezone);

    private static string? NonEmpty(string? timezone) => string.IsNullOrEmpty(timezone) ? null : timezone;
}
