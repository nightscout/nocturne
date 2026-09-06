using Nocturne.Core.Models;
using Nocturne.Services.Demo.Configuration;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// The demo tenant's therapy profile: one place defining the basal schedule,
/// carb ratio, sensitivity, and targets that the simulator runs on, the
/// Nightscout profile document seeded through the profile decomposer, and the
/// scheduled rates reported in device status. Keeping these together means the
/// basal-analysis report compares delivery against the same schedule the
/// simulation actually used.
/// </summary>
public static class DemoTherapyProfile
{
    public const string ProfileName = "Demo Profile";

    /// <summary>
    /// Basal blocks as (start time, multiplier on the configured base rate).
    /// Mirrors the circadian shape the generator uses: reduced overnight, a
    /// dawn-phenomenon ramp in the early morning, a lunch bump.
    /// </summary>
    public static readonly IReadOnlyList<(TimeSpan Start, double Multiplier)> BasalBlocks =
    [
        (TimeSpan.Zero, 0.9),
        (TimeSpan.FromHours(3), 1.0),
        (TimeSpan.FromHours(4.5), 1.15),
        (TimeSpan.FromHours(6.5), 1.05),
        (TimeSpan.FromHours(8), 1.0),
        (TimeSpan.FromHours(12), 1.1),
        (TimeSpan.FromHours(14), 1.0),
        (TimeSpan.FromHours(22), 0.9),
    ];

    /// <summary>
    /// The server's zone as an IANA id — <see cref="TimeZoneInfo.Local"/>
    /// reports a Windows id on Windows dev machines, which timezone pickers
    /// and profile consumers don't recognize.
    /// </summary>
    public static string LocalIanaTimezone()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId)
            return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana)
            ? iana
            : DemoLifestyleSeeds.HomeTimezoneFallback;
    }

    /// <summary>Scheduled basal rate at a wall-clock time for the configured base rate.</summary>
    public static double ScheduledRateAt(DateTime localTime, double baseRate)
    {
        var timeOfDay = localTime.TimeOfDay;
        var multiplier = BasalBlocks[0].Multiplier;
        foreach (var (start, m) in BasalBlocks)
        {
            if (timeOfDay >= start)
                multiplier = m;
        }

        return Math.Round(baseRate * multiplier, 2);
    }

    /// <summary>
    /// The Nightscout profile document for the demo tenant. Seeding it through
    /// the normal profile ingestion produces TherapySettings plus the four
    /// schedule tables, exactly like a Trio profile upload.
    /// </summary>
    public static Profile BuildProfile(DemoModeConfiguration config, DateTime nowUtc)
    {
        var basal = BasalBlocks
            .Select(b => new TimeValue
            {
                Time = $"{b.Start.Hours:00}:{b.Start.Minutes:00}",
                Value = Math.Round(config.BasalRate * b.Multiplier, 2),
                TimeAsSeconds = (int)b.Start.TotalSeconds,
            })
            .ToList();

        var profileData = new ProfileData
        {
            Dia = config.InsulinDurationMinutes / 60.0,
            CarbsHr = 20,
            Timezone = LocalIanaTimezone(),
            Units = "mg/dL",
            Basal = basal,
            CarbRatio =
            [
                new TimeValue { Time = "00:00", Value = config.CarbRatio + 2, TimeAsSeconds = 0 },
                new TimeValue { Time = "06:00", Value = config.CarbRatio - 1, TimeAsSeconds = 21600 },
                new TimeValue { Time = "11:00", Value = config.CarbRatio, TimeAsSeconds = 39600 },
                new TimeValue { Time = "17:00", Value = config.CarbRatio - 0.5, TimeAsSeconds = 61200 },
                new TimeValue { Time = "21:00", Value = config.CarbRatio + 2, TimeAsSeconds = 75600 },
            ],
            Sens =
            [
                new TimeValue { Time = "00:00", Value = config.InsulinSensitivityFactor + 8, TimeAsSeconds = 0 },
                new TimeValue { Time = "06:00", Value = config.InsulinSensitivityFactor - 5, TimeAsSeconds = 21600 },
                new TimeValue { Time = "12:00", Value = config.InsulinSensitivityFactor, TimeAsSeconds = 43200 },
                new TimeValue { Time = "20:00", Value = config.InsulinSensitivityFactor + 5, TimeAsSeconds = 72000 },
            ],
            TargetLow =
            [
                new TimeValue { Time = "00:00", Value = 100, TimeAsSeconds = 0 },
                new TimeValue { Time = "07:00", Value = 95, TimeAsSeconds = 25200 },
                new TimeValue { Time = "21:00", Value = 100, TimeAsSeconds = 75600 },
            ],
            TargetHigh =
            [
                new TimeValue { Time = "00:00", Value = 120, TimeAsSeconds = 0 },
                new TimeValue { Time = "07:00", Value = 115, TimeAsSeconds = 25200 },
                new TimeValue { Time = "21:00", Value = 120, TimeAsSeconds = 75600 },
            ],
        };

        return new Profile
        {
            DefaultProfile = ProfileName,
            StartDate = nowUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Mills = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds(),
            CreatedAt = nowUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Units = "mg/dL",
            EnteredBy = DemoDeviceStatusGenerator.DeviceName,
            Store = new Dictionary<string, ProfileData> { [ProfileName] = profileData },
        };
    }
}
