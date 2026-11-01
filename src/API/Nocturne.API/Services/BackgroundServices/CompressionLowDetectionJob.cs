using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Scans a tenant's latest night that ended at least <see cref="DetectionDelay"/> ago with
/// <see cref="CompressionLowDetectionService.DetectForNightAsync"/>, once per night, and comes due again
/// when its next night has ended or after <see cref="MaxSleep"/>, whichever is sooner.
/// </summary>
/// <remarks>
/// The cursor's <see cref="JobCursor.At"/> is the last night scanned, as midnight UTC of its date: a date
/// rather than a <see cref="JobCursor.Page"/>, which is a page number. A night that already has a
/// suggestion, dismissed or not, is not detected again. A tenant whose settings cannot be read fails the
/// run, and the backoff, capped at <see cref="MaxSleep"/>, is its retry.
/// </remarks>
public sealed class CompressionLowDetectionJob(CompressionLowDetectionService detector) : IScheduledJob
{
    internal const string JobName = "compression-low-detection";

    /// <summary>How long after a night's window ends it is scanned, for its last readings to arrive.</summary>
    internal static readonly TimeSpan DetectionDelay = TimeSpan.FromMinutes(15);

    internal static readonly TimeSpan MinSleep = TimeSpan.FromMinutes(1);

    /// <summary>The longest any run waits to be tried again.</summary>
    internal static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public TimeSpan MaxBackoff => MaxSleep;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        if (!run.Tenant!.IsActive)
            return JobOutcome.Resume(run.Cursor, run.StartedAt + MaxSleep);

        var now = run.StartedAt.UtcDateTime;
        var settings = await run.Services.GetRequiredService<IUISettingsService>().GetSettingsAsync(ct)
            ?? throw new InvalidOperationException("Could not read the tenant's UI settings.");
        var sleepSchedule = settings.DataQuality.SleepSchedule;
        var zone = (await run.Services.GetRequiredService<ICompressionLowNightWindowResolver>()
            .ResolveAsync(DateOnly.FromDateTime(now.AddDays(-1)), sleepSchedule, ct))
            .TimeZone;

        var night = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        while (ReadyAtUtc(night, zone, sleepSchedule) > now)
            night = night.AddDays(-1);

        var cursor = run.Cursor;
        if (cursor?.At is not { } scanned || DateOnly.FromDateTime(scanned.UtcDateTime) < night)
        {
            if (!await run.Services.GetRequiredService<ICompressionLowRepository>().AnySuggestionExistsForNightAsync(night, ct))
                await detector.DetectForNightAsync(night, run.Services, ct);

            cursor = new JobCursor(At: new DateTimeOffset(night.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        }

        var nextReady = new DateTimeOffset(ReadyAtUtc(night.AddDays(1), zone, sleepSchedule), TimeSpan.Zero);
        // Capped at MaxSleep so a change to the tenant's sleep schedule is seen within it.
        var wait = Math.Clamp((nextReady - run.StartedAt).Ticks, MinSleep.Ticks, MaxSleep.Ticks);
        return JobOutcome.Resume(cursor, run.StartedAt + TimeSpan.FromTicks(wait));
    }

    private static DateTime ReadyAtUtc(DateOnly nightOf, TimeZoneInfo zone, SleepScheduleSettings sleepSchedule)
    {
        var (_, windowEnd) = TimeZoneHelper.GetOvernightWindow(
            nightOf, zone, sleepSchedule.BedtimeHour, sleepSchedule.WakeTimeHour);

        return DateTimeOffset.FromUnixTimeMilliseconds(windowEnd).UtcDateTime + DetectionDelay;
    }
}
