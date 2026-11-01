using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.Entries;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Detects compression-low artefacts in overnight CGM data.
/// A compression low is a transient glucose dip caused by sensor compression during sleep,
/// characterised by a rapid V-shaped drop and recovery that does not reflect true hypoglycaemia.
/// </summary>
/// <remarks>
/// Each tenant's sleep schedule and timezone are read from <see cref="IUISettingsService"/> to
/// determine the overnight evaluation window. <see cref="CompressionLowDetectionJob"/> runs it for each
/// tenant's nights as they end.
/// </remarks>
/// <seealso cref="CompressionLowDetection"/>
public class CompressionLowDetectionService
{
    internal const string AuditEndpoint = "service:compression-low-detection";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CompressionLowDetectionService> _logger;

    public const string NotificationType = "glucose.compression_low_review";

    public static string NotificationSourceId(DateOnly nightOf) => nightOf.ToString("yyyy-MM-dd");

    internal const double MinDropRateMgDlPerMin = 2.0;
    internal const int MinDropDurationMinutes = 10;
    internal const double NadirThresholdMgDl = 70.0;
    internal const double RecoveryPercentage = 0.20;
    internal const int MaxRecoveryMinutes = 60;
    internal const double SharpnessWeight = 0.35;
    internal const double TimeOfNightWeight = 0.35;
    internal const double RecoveryWeight = 0.30;
    internal const double SharpDropRateMgDlPerMin = 5.0;
    internal const double SleepWindowEdgeShare = 0.25;
    internal const double InsulinPenaltyWeight = 0.4;
    internal const double CarbPenaltyWeight = 0.2;
    // A snack's worth of carbs on board: from here the penalty stays at its weight.
    internal const double CarbPenaltyFullCobGrams = 30.0;
    private const int StartTrimMinutes = 5;

    public CompressionLowDetectionService(
        TimeProvider timeProvider,
        ILogger<CompressionLowDetectionService> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Detects and saves the compression lows of the night of <paramref name="nightOf"/> for the tenant
    /// <paramref name="scopedProvider"/> reads as, and returns how many it saved.
    /// </summary>
    /// <param name="nightOf">The local calendar date whose overnight window is analysed.</param>
    /// <param name="scopedProvider">A job scope from <see cref="TenantJobScope"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> DetectForNightAsync(
        DateOnly nightOf,
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken)
    {
        var repository = scopedProvider.GetRequiredService<ICompressionLowRepository>();
        var entryService = scopedProvider.GetRequiredService<IEntryService>();
        var iobCobReader = scopedProvider.GetRequiredService<IIobCobReader>();
        var sensitivityResolver = scopedProvider.GetRequiredService<ISensitivityResolver>();
        var notificationService = scopedProvider.GetRequiredService<IInAppNotificationService>();
        var uiSettingsService = scopedProvider.GetRequiredService<IUISettingsService>();
        var tenantAccessor = scopedProvider.GetRequiredService<ITenantAccessor>();

        // The enable flag and the sleep hours are the tenant's own, so a read that failed leaves
        // nothing to run against: the defaults would both override a tenant who turned detection
        // off and judge the night by hours they never chose.
        var settings = await uiSettingsService.GetSettingsAsync(cancellationToken);
        if (settings == null)
        {
            _logger.LogWarning(
                "Could not read UI settings; skipping compression low detection for night of {NightOf}",
                nightOf);
            return 0;
        }

        if (!settings.DataQuality.CompressionLowDetection.Enabled)
        {
            _logger.LogDebug("Compression low detection is disabled");
            return 0;
        }

        // Check if already processed (only active suggestions block re-detection)
        if (await repository.ActiveSuggestionsExistForNightAsync(nightOf, cancellationToken))
        {
            _logger.LogDebug("Already processed night of {NightOf}", nightOf);
            return 0;
        }

        var window = await scopedProvider
            .GetRequiredService<ICompressionLowNightWindowResolver>()
            .ResolveAsync(nightOf, settings.DataQuality.SleepSchedule, cancellationToken);

        // Get entries
        var entries = (await entryService.GetEntriesAsync(
            find: $"{{\"mills\":{{\"$gte\":{window.WindowStart},\"$lte\":{window.WindowEnd}}}}}",
            count: int.MaxValue,
            skip: 0,
            cancellationToken: cancellationToken))
            .Where(e => e.GlucoseMgdl() > 0)
            .OrderBy(e => e.Mills)
            .ToList();

        if (entries.Count < 10)
        {
            _logger.LogDebug("Insufficient entries for night of {NightOf}: {Count}", nightOf, entries.Count);
            return 0;
        }

        // Detect V-shape candidates
        var candidates = DetectVShapeCandidates(entries);

        // Score candidates
        var suggestions = new List<CompressionLowSuggestion>();
        foreach (var candidate in candidates)
        {
            var confidence = await ScoreCandidateAsync(
                candidate, window.TimeZone, window.BedtimeHour, window.WakeTimeHour, iobCobReader, sensitivityResolver, cancellationToken);
            if (confidence >= CompressionLowConfidence.MinThreshold)
            {
                var trimmedStartMills = candidate.StartMills + (StartTrimMinutes * 60 * 1000);
                suggestions.Add(new CompressionLowSuggestion
                {
                    Id = Guid.CreateVersion7(),
                    StartMills = trimmedStartMills,
                    EndMills = candidate.EndMills,
                    Confidence = confidence,
                    Status = CompressionLowStatus.Pending,
                    NightOf = nightOf,
                    CreatedAt = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    LowestGlucose = candidate.LowestGlucose,
                    DropRate = candidate.MaxDropRate,
                    RecoveryMinutes = candidate.RecoveryMinutes
                });
            }
        }

        await repository.CreateManyAsync(suggestions, cancellationToken);

        // Create notification if any suggestions found.
        // Notification uses i18n keys so the frontend can render localized text.
        // Notifications are keyed by subject ID (not tenant ID). Look up the tenant
        // owner so the notification is attributed to an actual user.
        if (suggestions.Count > 0)
        {
            var ownerId = await GetTenantOwnerSubjectIdAsync(
                tenantAccessor.TenantId, scopedProvider, cancellationToken);

            if (ownerId != null)
            {
                await CreateNotificationAsync(nightOf, suggestions.Count, ownerId, notificationService, cancellationToken);
            }
            else
            {
                _logger.LogWarning(
                    "No owner found for tenant {TenantId}; skipping compression low notification for night {NightOf}",
                    tenantAccessor.TenantId, nightOf);
            }
        }

        _logger.LogInformation(
            "Detected {Count} compression lows for night of {NightOf}",
            suggestions.Count, nightOf);

        return suggestions.Count;
    }

    internal record VShapeCandidate(
        long StartMills,
        long EndMills,
        double LowestGlucose,
        double PreDropGlucose,
        double MaxDropRate,
        int RecoveryMinutes,
        DateTime NadirTime);

    internal List<VShapeCandidate> DetectVShapeCandidates(List<Entry> entries) =>
        entries
            .GroupBy(e => (e.DataSource, e.Device))
            .SelectMany(sensor => DetectVShapeCandidatesForSensor(sensor.ToList()))
            .OrderBy(c => c.StartMills)
            .ToList();

    private List<VShapeCandidate> DetectVShapeCandidatesForSensor(List<Entry> entries)
    {
        var candidates = new List<VShapeCandidate>();

        for (int i = 2; i < entries.Count - 2; i++)
        {
            var current = entries[i];

            if (current.GlucoseMgdl() is var nadirValue && (nadirValue <= 0 || nadirValue >= NadirThresholdMgDl))
                continue;

            var dropInfo = FindDrop(entries, i);
            if (dropInfo == null)
                continue;

            var recoveryInfo = FindRecovery(entries, i, dropInfo.Value.preDropValue);
            if (recoveryInfo == null)
                continue;

            candidates.Add(new VShapeCandidate(
                StartMills: entries[dropInfo.Value.startIndex].Mills,
                EndMills: entries[recoveryInfo.Value.endIndex].Mills,
                LowestGlucose: current.GlucoseMgdl(),
                PreDropGlucose: dropInfo.Value.preDropValue,
                MaxDropRate: dropInfo.Value.maxDropRate,
                RecoveryMinutes: recoveryInfo.Value.recoveryMinutes,
                NadirTime: current.Date ?? DateTime.UnixEpoch
            ));
        }

        return MergeOverlappingCandidates(candidates);
    }

    internal (int startIndex, double preDropValue, double maxDropRate)? FindDrop(
        List<Entry> entries, int nadirIndex)
    {
        var nadir = entries[nadirIndex];
        var maxDropRate = 0.0;
        var dropDurationMinutes = 0.0;
        int? earliestSteepIndex = null;

        for (int i = nadirIndex - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry.GlucoseMgdl() <= 0)
                continue;

            var timeDiffMinutes = (nadir.Mills - entry.Mills) / 60000.0;
            var glucoseDiff = entry.GlucoseMgdl() - nadir.GlucoseMgdl();
            var dropRate = glucoseDiff / timeDiffMinutes;

            if (dropRate > maxDropRate)
                maxDropRate = dropRate;

            if (dropRate >= MinDropRateMgDlPerMin)
            {
                dropDurationMinutes = timeDiffMinutes;
                earliestSteepIndex = i;
            }
            else if (dropDurationMinutes > 0)
            {
                if (dropDurationMinutes >= MinDropDurationMinutes)
                {
                    return (i + 1, entries[i + 1].GlucoseMgdl(), maxDropRate);
                }
                break;
            }
        }

        // The loop exhausted all entries while the drop rate stayed above the threshold.
        // This is the typical compression low pattern — steep drop all the way through.
        if (dropDurationMinutes >= MinDropDurationMinutes && earliestSteepIndex.HasValue)
        {
            return (earliestSteepIndex.Value, entries[earliestSteepIndex.Value].GlucoseMgdl(), maxDropRate);
        }

        return null;
    }

    internal (int endIndex, int recoveryMinutes)? FindRecovery(
        List<Entry> entries, int nadirIndex, double preDropValue)
    {
        var nadir = entries[nadirIndex];
        var nadirGlucose = nadir.GlucoseMgdl();
        var targetValue = nadirGlucose + (preDropValue - nadirGlucose) * (1 - RecoveryPercentage);

        for (int i = nadirIndex + 1; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.GlucoseMgdl() <= 0)
                continue;

            var minutesSinceNadir = (entry.Mills - nadir.Mills) / 60000.0;

            if (minutesSinceNadir > MaxRecoveryMinutes)
                break;

            if (entry.GlucoseMgdl() >= targetValue)
            {
                return (i, (int)minutesSinceNadir);
            }
        }

        return null;
    }

    internal List<VShapeCandidate> MergeOverlappingCandidates(List<VShapeCandidate> candidates)
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

    internal async Task<double> ScoreCandidateAsync(
        VShapeCandidate candidate,
        TimeZoneInfo userTimeZone,
        int bedtimeHour,
        int wakeTimeHour,
        IIobCobReader iobCobReader,
        ISensitivityResolver sensitivityResolver,
        CancellationToken cancellationToken)
    {
        var sharpness = Math.Clamp(
            (candidate.MaxDropRate - MinDropRateMgDlPerMin) / (SharpDropRateMgDlPerMin - MinDropRateMgDlPerMin),
            0,
            1);
        var recovery = Math.Clamp(1.0 - candidate.RecoveryMinutes / (double)MaxRecoveryMinutes, 0, 1);
        var nadirLocalTime = TimeZoneInfo.ConvertTimeFromUtc(candidate.NadirTime, userTimeZone).TimeOfDay;

        var score = sharpness * SharpnessWeight
            + TimeOfNightFraction(nadirLocalTime, bedtimeHour, wakeTimeHour) * TimeOfNightWeight
            + recovery * RecoveryWeight;

        var onBoard = await iobCobReader.ReadTotalAsync(candidate.StartMills, live: false, cancellationToken);
        score -= await InsulinPenaltyAsync(candidate, onBoard, sensitivityResolver, cancellationToken);
        score -= CarbPenalty(onBoard);

        return Math.Clamp(score, 0, 1);
    }

    internal static double TimeOfNightFraction(TimeSpan nadirLocalTime, int bedtimeHour, int wakeTimeHour)
    {
        var windowHours = TimeZoneHelper.SleepSpanHours(bedtimeHour, wakeTimeHour);

        var hoursIntoWindow = ((nadirLocalTime.TotalHours - bedtimeHour) % 24 + 24) % 24;
        if (hoursIntoWindow >= windowHours)
            return 0;

        var share = hoursIntoWindow / windowHours;
        return Math.Min(1, Math.Min(share, 1 - share) / SleepWindowEdgeShare);
    }

    private static async Task<double> InsulinPenaltyAsync(
        VShapeCandidate candidate,
        IobCobReading onBoard,
        ISensitivityResolver sensitivityResolver,
        CancellationToken cancellationToken)
    {
        var observedDrop = candidate.PreDropGlucose - candidate.LowestGlucose;
        if (!onBoard.HasIob || onBoard.Iob.Iob <= 0 || observedDrop <= 0)
            return 0;

        var isf = await sensitivityResolver.GetSensitivityAsync(candidate.StartMills, ct: cancellationToken);
        if (isf <= 0)
            return 0;

        var explainedDrop = onBoard.Iob.Iob * isf;
        return Math.Min(1, explainedDrop / observedDrop) * InsulinPenaltyWeight;
    }

    private static double CarbPenalty(IobCobReading onBoard)
    {
        if (!onBoard.HasCob || onBoard.Cob!.Cob <= 0)
            return 0;

        return Math.Min(1, onBoard.Cob.Cob / CarbPenaltyFullCobGrams) * CarbPenaltyWeight;
    }

    private async Task CreateNotificationAsync(
        DateOnly nightOf,
        int count,
        string userId,
        IInAppNotificationService notificationService,
        CancellationToken cancellationToken)
    {
        // Use i18n keys for title/subtitle so the frontend renders localized text.
        // The metadata contains the count and nightOf for interpolation.
        await notificationService.CreateNotificationAsync(
            userId: userId,
            type: NotificationType,
            title: "compression_low_detected",
            subtitle: "compression_low_detected_subtitle",
            sourceId: NotificationSourceId(nightOf),
            actions: new List<NotificationActionDto>
            {
                new()
                {
                    ActionId = "review",
                    Label = "review",
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

    /// <summary>
    /// Looks up the subject ID of the tenant owner so that in-app notifications are attributed to
    /// a real user (not a tenant ID). Returns <see langword="null"/> if no owner subject is found.
    /// </summary>
    /// <param name="tenantId">The tenant to look up the owner for.</param>
    /// <param name="scopedProvider">Scoped service provider for database access.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The owner's subject ID as a string, or <see langword="null"/>.</returns>
    private static Task<string?> GetTenantOwnerSubjectIdAsync(
        Guid tenantId,
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken) =>
        scopedProvider.GetRequiredService<ITenantOwnerResolver>()
            .GetOwnerSubjectIdAsync(tenantId, cancellationToken);
}
