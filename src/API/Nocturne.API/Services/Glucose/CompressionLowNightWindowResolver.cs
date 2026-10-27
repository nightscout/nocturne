using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Resolves the zone and overnight window of a compression-low night, so detection and the
/// suggestion detail cut the same entries.
/// </summary>
public interface ICompressionLowNightWindowResolver
{
    /// <summary>
    /// Resolves the zone of the night of <paramref name="nightOf"/> and cuts its overnight window.
    /// </summary>
    /// <param name="nightOf">The local calendar date whose overnight window is cut.</param>
    /// <param name="sleepSchedule">The tenant's sleep schedule.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<CompressionLowNightWindow> ResolveAsync(
        DateOnly nightOf,
        SleepScheduleSettings sleepSchedule,
        CancellationToken cancellationToken);
}

/// <param name="TimeZone">The zone the night is local to.</param>
/// <param name="BedtimeHour">The hour the sleep schedule puts the night's start at.</param>
/// <param name="WakeTimeHour">The hour the sleep schedule puts the night's end at.</param>
/// <param name="WindowStart">Window start, Unix milliseconds.</param>
/// <param name="WindowEnd">Window end, Unix milliseconds.</param>
public sealed record CompressionLowNightWindow(
    TimeZoneInfo TimeZone,
    int BedtimeHour,
    int WakeTimeHour,
    long WindowStart,
    long WindowEnd);

/// <summary>
/// Prefers the zone in the sleep schedule, then the therapy profile's, then one inferred from the
/// UTC offsets of entries near the night, then UTC.
/// </summary>
/// <remarks>Scoped: it reads as the tenant of the scope it is resolved from.</remarks>
public sealed class CompressionLowNightWindowResolver(
    ITherapySettingsResolver therapySettingsResolver,
    IEntryService entryService,
    ILogger<CompressionLowNightWindowResolver> logger) : ICompressionLowNightWindowResolver
{
    public async Task<CompressionLowNightWindow> ResolveAsync(
        DateOnly nightOf,
        SleepScheduleSettings sleepSchedule,
        CancellationToken cancellationToken)
    {
        var timeZone = ParseTimeZone(sleepSchedule.Timezone)
            ?? await GetProfileTimeZoneAsync(cancellationToken)
            ?? await InferTimeZoneFromEntriesAsync(nightOf, cancellationToken)
            ?? TimeZoneInfo.Utc;

        var (windowStart, windowEnd) = TimeZoneHelper.GetOvernightWindow(
            nightOf, timeZone, sleepSchedule.BedtimeHour, sleepSchedule.WakeTimeHour);

        return new CompressionLowNightWindow(
            timeZone, sleepSchedule.BedtimeHour, sleepSchedule.WakeTimeHour, windowStart, windowEnd);
    }

    private static TimeZoneInfo? ParseTimeZone(string? timezoneId) =>
        TimeZoneHelper.TryGetTimeZoneInfoFromId(timezoneId, out var tz) ? tz : null;

    private async Task<TimeZoneInfo?> GetProfileTimeZoneAsync(CancellationToken cancellationToken)
    {
        try
        {
            return ParseTimeZone(await therapySettingsResolver.GetTimezoneAsync(ct: cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to get user timezone from profile");
            return null;
        }
    }

    private async Task<TimeZoneInfo?> InferTimeZoneFromEntriesAsync(
        DateOnly nightOf,
        CancellationToken cancellationToken)
    {
        try
        {
            var midnightUtcMills = nightOf.ToDateTime(TimeOnly.MinValue).ToMills();
            var entries = await entryService.GetEntriesAsync(
                find: $"{{\"mills\":{{\"$gte\":{midnightUtcMills - 12 * 3600000},\"$lte\":{midnightUtcMills + 12 * 3600000}}}}}",
                count: 10,
                skip: 0,
                cancellationToken: cancellationToken);

            var utcOffset = entries
                .Where(e => e.UtcOffset.HasValue && e.UtcOffset.Value != 0)
                .Select(e => e.UtcOffset!.Value)
                .FirstOrDefault();

            if (utcOffset == 0)
                return null;

            var offset = TimeSpan.FromMinutes(utcOffset);
            var sign = offset >= TimeSpan.Zero ? "+" : "";
            var tz = TimeZoneInfo.CreateCustomTimeZone(
                $"Entry-UTC{sign}{offset.Hours:D2}:{offset.Minutes:D2}",
                offset,
                $"UTC{sign}{offset.Hours}:{offset.Minutes:D2}",
                $"UTC{sign}{offset.Hours}:{offset.Minutes:D2}");

            logger.LogInformation(
                "Inferred timezone UTC{Offset} from entry data for night {NightOf}",
                $"{sign}{offset.Hours}:{offset.Minutes:D2}", nightOf);

            return tz;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to infer timezone from entries for night {NightOf}", nightOf);
            return null;
        }
    }
}
