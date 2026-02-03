using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that detects compression lows in overnight glucose data
/// </summary>
public class CompressionLowDetectionService : BackgroundService, ICompressionLowDetectionService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CompressionLowDetectionService> _logger;

    // Detection configuration - sleep hours are now read from settings
    private const int DefaultBedtimeHour = 23;
    private const int DefaultWakeTimeHour = 7;
    private const int DetectionDelayMinutes = 15;
    private const double MinDropRateMgDlPerMin = 2.0;
    private const int MinDropDurationMinutes = 10;
    private const double NadirThresholdMgDl = 70.0;
    private const double RecoveryPercentage = 0.20;
    private const int MaxRecoveryMinutes = 60;
    private const double MinConfidenceThreshold = 0.5;
    private const int StartTrimMinutes = 5; // Trim leadup window to avoid capturing too many good readings

    public CompressionLowDetectionService(
        IServiceProvider serviceProvider,
        ILogger<CompressionLowDetectionService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Compression Low Detection Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Get wake time from settings to schedule detection
                int wakeTimeHour;
                using (var scope = _serviceProvider.CreateScope())
                {
                    var uiSettingsService = scope.ServiceProvider.GetRequiredService<IUISettingsService>();
                    var settings = await uiSettingsService.GetSettingsAsync(stoppingToken);
                    wakeTimeHour = settings.DataQuality.SleepSchedule.WakeTimeHour;
                }

                // Calculate next run time (wake time + delay)
                var now = DateTime.UtcNow;
                var nextRun = now.Date.AddHours(wakeTimeHour).AddMinutes(DetectionDelayMinutes);

                if (now >= nextRun)
                    nextRun = nextRun.AddDays(1);

                var delay = nextRun - now;
                _logger.LogDebug("Next compression low detection scheduled for {NextRun}", nextRun);

                await Task.Delay(delay, stoppingToken);

                // Run detection for last night
                var lastNight = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
                await DetectForNightAsync(lastNight, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in compression low detection cycle");
                // Wait before retrying
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        _logger.LogInformation("Compression Low Detection Service stopped");
    }

    public async Task<int> DetectForNightAsync(
        DateOnly nightOf,
        CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<CompressionLowRepository>();
        var entryService = scope.ServiceProvider.GetRequiredService<IEntryService>();
        var treatmentService = scope.ServiceProvider.GetRequiredService<ITreatmentService>();
        var notificationService = scope.ServiceProvider.GetRequiredService<IInAppNotificationService>();
        var profileDataService = scope.ServiceProvider.GetRequiredService<IProfileDataService>();
        var uiSettingsService = scope.ServiceProvider.GetRequiredService<IUISettingsService>();

        // Check if detection is enabled
        var settings = await uiSettingsService.GetSettingsAsync(cancellationToken);
        if (!settings.DataQuality.CompressionLowDetection.Enabled)
        {
            _logger.LogDebug("Compression low detection is disabled");
            return 0;
        }

        // Get sleep schedule from settings
        var sleepSchedule = settings.DataQuality.SleepSchedule;
        var bedtimeHour = sleepSchedule.BedtimeHour;
        var wakeTimeHour = sleepSchedule.WakeTimeHour;

        // Check if already processed
        if (await repository.SuggestionsExistForNightAsync(nightOf, cancellationToken))
        {
            _logger.LogDebug("Already processed night of {NightOf}", nightOf);
            return 0;
        }

        // Get user's timezone from profile that was active during the night in question
        var userTimeZone = await GetUserTimeZoneForNightAsync(profileDataService, nightOf, cancellationToken);

        // Get overnight window in user's local time
        var (windowStart, windowEnd) = GetOvernightWindow(nightOf, userTimeZone, bedtimeHour, wakeTimeHour);

        // Get entries
        var entries = (await entryService.GetEntriesAsync(
            find: $"{{\"mills\":{{\"$gte\":{windowStart},\"$lte\":{windowEnd}}}}}",
            count: 1000,
            skip: 0,
            cancellationToken: cancellationToken))
            .Where(e => e.Sgv.HasValue)
            .OrderBy(e => e.Mills)
            .ToList();

        if (entries.Count < 10)
        {
            _logger.LogDebug("Insufficient entries for night of {NightOf}: {Count}", nightOf, entries.Count);
            return 0;
        }

        // Get treatments for IOB context
        var treatments = (await treatmentService.GetTreatmentsAsync(
            find: $"{{\"mills\":{{\"$gte\":{windowStart},\"$lte\":{windowEnd}}}}}",
            cancellationToken: cancellationToken))
            .ToList();

        // Detect V-shape candidates
        var candidates = DetectVShapeCandidates(entries);

        // Score candidates
        var suggestions = new List<CompressionLowSuggestion>();
        foreach (var candidate in candidates)
        {
            var confidence = ScoreCandidate(candidate, treatments, userTimeZone);
            if (confidence >= MinConfidenceThreshold)
            {
                // Trim the start by 5 minutes to avoid capturing too many good readings before the drop
                var trimmedStartMills = candidate.StartMills + (StartTrimMinutes * 60 * 1000);
                suggestions.Add(new CompressionLowSuggestion
                {
                    Id = Guid.CreateVersion7(),
                    StartMills = trimmedStartMills,
                    EndMills = candidate.EndMills,
                    Confidence = confidence,
                    Status = CompressionLowStatus.Pending,
                    NightOf = nightOf,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LowestGlucose = candidate.LowestGlucose,
                    DropRate = candidate.MaxDropRate,
                    RecoveryMinutes = candidate.RecoveryMinutes
                });
            }
        }

        // Save suggestions
        foreach (var suggestion in suggestions)
        {
            await repository.CreateAsync(suggestion, cancellationToken);
        }

        // Create notification if any suggestions
        if (suggestions.Count > 0)
        {
            await CreateNotificationAsync(nightOf, suggestions.Count, notificationService, cancellationToken);
        }

        _logger.LogInformation(
            "Detected {Count} compression lows for night of {NightOf}",
            suggestions.Count, nightOf);

        return suggestions.Count;
    }

    private record VShapeCandidate(
        long StartMills,
        long EndMills,
        double LowestGlucose,
        double PreDropGlucose,
        double MaxDropRate,
        int RecoveryMinutes,
        DateTime NadirTime);

    private List<VShapeCandidate> DetectVShapeCandidates(List<Entry> entries)
    {
        var candidates = new List<VShapeCandidate>();

        for (int i = 2; i < entries.Count - 2; i++)
        {
            var current = entries[i];

            // Check if this could be a nadir
            if (!current.Sgv.HasValue || current.Sgv.Value >= NadirThresholdMgDl)
                continue;

            // Look for drop leading to this point
            var dropInfo = FindDrop(entries, i);
            if (dropInfo == null)
                continue;

            // Look for recovery after this point
            var recoveryInfo = FindRecovery(entries, i, dropInfo.Value.preDropValue);
            if (recoveryInfo == null)
                continue;

            candidates.Add(new VShapeCandidate(
                StartMills: entries[dropInfo.Value.startIndex].Mills,
                EndMills: entries[recoveryInfo.Value.endIndex].Mills,
                LowestGlucose: current.Sgv!.Value,
                PreDropGlucose: dropInfo.Value.preDropValue,
                MaxDropRate: dropInfo.Value.maxDropRate,
                RecoveryMinutes: recoveryInfo.Value.recoveryMinutes,
                NadirTime: current.Date ?? DateTime.UnixEpoch
            ));
        }

        // Merge overlapping candidates
        return MergeOverlappingCandidates(candidates);
    }

    private (int startIndex, double preDropValue, double maxDropRate)? FindDrop(
        List<Entry> entries, int nadirIndex)
    {
        var nadir = entries[nadirIndex];
        var maxDropRate = 0.0;
        var dropDurationMinutes = 0.0;

        for (int i = nadirIndex - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (!entry.Sgv.HasValue)
                continue;

            var timeDiffMinutes = (nadir.Mills - entry.Mills) / 60000.0;
            var glucoseDiff = entry.Sgv.Value - nadir.Sgv!.Value;
            var dropRate = glucoseDiff / timeDiffMinutes;

            if (dropRate > maxDropRate)
                maxDropRate = dropRate;

            if (dropRate >= MinDropRateMgDlPerMin)
            {
                dropDurationMinutes = timeDiffMinutes;
            }
            else if (dropDurationMinutes > 0)
            {
                // Drop rate dropped below threshold - this is where the drop started
                if (dropDurationMinutes >= MinDropDurationMinutes)
                {
                    return (i + 1, entries[i + 1].Sgv!.Value, maxDropRate);
                }
                break;
            }
        }

        return null;
    }

    private (int endIndex, int recoveryMinutes)? FindRecovery(
        List<Entry> entries, int nadirIndex, double preDropValue)
    {
        var nadir = entries[nadirIndex];
        var targetValue = nadir.Sgv!.Value + (preDropValue - nadir.Sgv.Value) * (1 - RecoveryPercentage);

        for (int i = nadirIndex + 1; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!entry.Sgv.HasValue)
                continue;

            var minutesSinceNadir = (entry.Mills - nadir.Mills) / 60000.0;

            if (minutesSinceNadir > MaxRecoveryMinutes)
                break;

            if (entry.Sgv.Value >= targetValue)
            {
                return (i, (int)minutesSinceNadir);
            }
        }

        return null;
    }

    private List<VShapeCandidate> MergeOverlappingCandidates(List<VShapeCandidate> candidates)
    {
        if (candidates.Count <= 1)
            return candidates;

        var sorted = candidates.OrderBy(c => c.StartMills).ToList();
        var merged = new List<VShapeCandidate> { sorted[0] };

        foreach (var candidate in sorted.Skip(1))
        {
            var last = merged[^1];
            if (candidate.StartMills <= last.EndMills)
            {
                // Merge - keep the one with lower nadir
                if (candidate.LowestGlucose < last.LowestGlucose)
                {
                    merged[^1] = candidate with
                    {
                        StartMills = last.StartMills,
                        EndMills = Math.Max(last.EndMills, candidate.EndMills)
                    };
                }
                else
                {
                    merged[^1] = last with
                    {
                        EndMills = Math.Max(last.EndMills, candidate.EndMills)
                    };
                }
            }
            else
            {
                merged.Add(candidate);
            }
        }

        return merged;
    }

    private double ScoreCandidate(VShapeCandidate candidate, List<Treatment> treatments, TimeZoneInfo userTimeZone)
    {
        var score = 0.4; // Base score

        // V-shape clarity (0-0.3)
        // Sharp drop and recovery = cleaner V
        var sharpness = candidate.MaxDropRate / 5.0; // Normalize to ~0-1 range
        score += Math.Min(0.3, sharpness * 0.3);

        // Time of night (0-0.25)
        // 2am-5am = full points, 11pm-1am or after 6am = reduced
        // IMPORTANT: Convert nadir time to user's local time for correct scoring
        var nadirTimeLocal = TimeZoneInfo.ConvertTimeFromUtc(candidate.NadirTime, userTimeZone);
        var nadirHour = nadirTimeLocal.Hour;
        if (nadirHour >= 2 && nadirHour <= 5)
            score += 0.25;
        else if (nadirHour >= 0 && nadirHour <= 1)
            score += 0.15;
        else if (nadirHour == 23 || nadirHour == 6)
            score += 0.10;

        // Recovery completeness (0-0.2)
        // Fast recovery = higher score
        var recoveryScore = 1.0 - (candidate.RecoveryMinutes / (double)MaxRecoveryMinutes);
        score += recoveryScore * 0.2;

        // IOB penalty (0-0.2 penalty)
        var iobPenalty = CalculateIobPenalty(candidate.StartMills, treatments);
        score -= iobPenalty;

        // Carb penalty (0-0.15 penalty)
        var carbPenalty = CalculateCarbPenalty(candidate.StartMills, treatments);
        score -= carbPenalty;

        return Math.Max(0, Math.Min(1, score));
    }

    private double CalculateIobPenalty(long dropStartMills, List<Treatment> treatments)
    {
        // Check for insulin in the 2 hours before drop
        var windowStart = dropStartMills - (2 * 60 * 60 * 1000);
        var recentInsulin = treatments
            .Where(t => t.Mills >= windowStart && t.Mills <= dropStartMills)
            .Sum(t => t.Insulin ?? 0);

        // More than 2u = max penalty
        return Math.Min(0.2, recentInsulin * 0.1);
    }

    private double CalculateCarbPenalty(long dropStartMills, List<Treatment> treatments)
    {
        // Check for carbs in the 2 hours before drop
        var windowStart = dropStartMills - (2 * 60 * 60 * 1000);
        var recentCarbs = treatments
            .Where(t => t.Mills >= windowStart && t.Mills <= dropStartMills)
            .Sum(t => t.Carbs ?? 0);

        // More than 30g = max penalty
        return Math.Min(0.15, recentCarbs / 200.0);
    }

    private async Task CreateNotificationAsync(
        DateOnly nightOf,
        int count,
        IInAppNotificationService notificationService,
        CancellationToken cancellationToken)
    {
        var subtitle = count == 1
            ? "1 potential compression low found last night"
            : $"{count} potential compression lows found last night";

        await notificationService.CreateNotificationAsync(
            userId: "default", // TODO: Multi-user support
            type: InAppNotificationType.CompressionLowReview,
            urgency: NotificationUrgency.Info,
            title: "Compression lows detected",
            subtitle: subtitle,
            sourceId: nightOf.ToString("yyyy-MM-dd"),
            actions: new List<NotificationActionDto>
            {
                new()
                {
                    ActionId = "review",
                    Label = "Review",
                    Icon = "eye",
                    Variant = "default"
                }
            },
            metadata: new Dictionary<string, object>
            {
                ["Count"] = count,
                ["NightOf"] = nightOf.ToString("yyyy-MM-dd")
            },
            cancellationToken: cancellationToken);
    }

    private static (long windowStart, long windowEnd) GetOvernightWindow(
        DateOnly nightOf,
        TimeZoneInfo userTimeZone,
        int bedtimeHour = DefaultBedtimeHour,
        int wakeTimeHour = DefaultWakeTimeHour)
    {
        // Create local times for the overnight window
        var startLocalDateTime = nightOf.ToDateTime(new TimeOnly(bedtimeHour, 0));
        var endLocalDateTime = nightOf.AddDays(1).ToDateTime(new TimeOnly(wakeTimeHour, 0));

        // Convert local times to UTC for querying
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocalDateTime, userTimeZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocalDateTime, userTimeZone);

        var windowStart = new DateTimeOffset(startUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var windowEnd = new DateTimeOffset(endUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();

        return (windowStart, windowEnd);
    }

    private async Task<TimeZoneInfo> GetUserTimeZoneForNightAsync(
        IProfileDataService profileDataService,
        DateOnly nightOf,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get the timestamp for 2am on the night in question (middle of the night)
            // First try with UTC, then we'll refine
            var approximateNightTime = nightOf.ToDateTime(new TimeOnly(2, 0));
            var approximateMills = new DateTimeOffset(approximateNightTime, TimeSpan.Zero).ToUnixTimeMilliseconds();

            var profile = await profileDataService.GetProfileAtTimestampAsync(approximateMills, cancellationToken);
            var timezoneId = profile?.Store?.Values.FirstOrDefault()?.Timezone;

            if (!string.IsNullOrEmpty(timezoneId))
            {
                return GetTimeZoneInfoFromId(timezoneId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get user timezone for night {NightOf}, using UTC", nightOf);
        }

        return TimeZoneInfo.Utc;
    }

    private TimeZoneInfo GetTimeZoneInfoFromId(string timezoneId)
    {
        try
        {
            // Try direct lookup first
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            // IANA to Windows timezone mapping for common timezones
            // This handles cases where the profile has IANA IDs but we're on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var windowsId = TryConvertIanaToWindows(timezoneId);
                if (windowsId != null)
                {
                    try
                    {
                        return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                    }
                    catch (TimeZoneNotFoundException)
                    {
                        _logger.LogWarning("Could not find Windows timezone {WindowsId} converted from {IanaId}", windowsId, timezoneId);
                    }
                }
            }

            _logger.LogWarning("Could not find timezone {TimezoneId}, using UTC", timezoneId);
            return TimeZoneInfo.Utc;
        }
    }

    private static string? TryConvertIanaToWindows(string ianaId)
    {
        // Common IANA to Windows timezone mappings
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["America/New_York"] = "Eastern Standard Time",
            ["America/Chicago"] = "Central Standard Time",
            ["America/Denver"] = "Mountain Standard Time",
            ["America/Los_Angeles"] = "Pacific Standard Time",
            ["America/Anchorage"] = "Alaskan Standard Time",
            ["Pacific/Honolulu"] = "Hawaiian Standard Time",
            ["America/Phoenix"] = "US Mountain Standard Time",
            ["Europe/London"] = "GMT Standard Time",
            ["Europe/Paris"] = "Romance Standard Time",
            ["Europe/Berlin"] = "W. Europe Standard Time",
            ["Europe/Moscow"] = "Russian Standard Time",
            ["Asia/Tokyo"] = "Tokyo Standard Time",
            ["Asia/Shanghai"] = "China Standard Time",
            ["Asia/Kolkata"] = "India Standard Time",
            ["Australia/Sydney"] = "AUS Eastern Standard Time",
            ["Australia/Perth"] = "W. Australia Standard Time",
            ["Etc/UTC"] = "UTC",
            ["UTC"] = "UTC"
        };

        return mapping.GetValueOrDefault(ianaId);
    }
}
