using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Domain service for compression low suggestion management. Provides retrieval, review, and
/// dismissal of <see cref="CompressionLowSuggestion"/> records detected by
/// <see cref="BackgroundServices.CompressionLowDetectionService"/>, including enrichment of
/// suggestions with surrounding CGM entries for user review.
/// </summary>
/// <seealso cref="ICompressionLowService"/>
public class CompressionLowService : ICompressionLowService
{
    private readonly ICompressionLowRepository _repository;
    private readonly IStateSpanService _stateSpanService;
    private readonly IEntryService _entryService;
    private readonly ITreatmentService _treatmentService;
    private readonly IInAppNotificationService _notificationService;
    private readonly ITherapySettingsResolver _therapySettingsResolver;
    private readonly IUISettingsService _uiSettingsService;
    private readonly ILogger<CompressionLowService> _logger;

    public CompressionLowService(
        ICompressionLowRepository repository,
        IStateSpanService stateSpanService,
        IEntryService entryService,
        ITreatmentService treatmentService,
        IInAppNotificationService notificationService,
        ITherapySettingsResolver therapySettingsResolver,
        IUISettingsService uiSettingsService,
        ILogger<CompressionLowService> logger)
    {
        _repository = repository;
        _stateSpanService = stateSpanService;
        _entryService = entryService;
        _treatmentService = treatmentService;
        _notificationService = notificationService;
        _therapySettingsResolver = therapySettingsResolver;
        _uiSettingsService = uiSettingsService;
        _logger = logger;
    }

    public async Task<IEnumerable<CompressionLowSuggestion>> GetSuggestionsAsync(
        CompressionLowStatus? status = null,
        DateOnly? nightOf = null,
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetSuggestionsAsync(status, nightOf, cancellationToken);
    }

    public async Task<CompressionLowSuggestionWithEntries?> GetSuggestionWithEntriesAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var suggestion = await _repository.GetByIdAsync(id, cancellationToken);
        if (suggestion == null)
            return null;

        // Get sleep schedule from settings
        var settings = await _uiSettingsService.GetSettingsAsync(cancellationToken);
        var sleepSchedule = settings.DataQuality.SleepSchedule;

        // Get user's timezone: prefer UI settings, fall back to profile, then UTC
        var userTimeZone = ResolveTimeZone(sleepSchedule.Timezone)
            ?? await GetUserTimeZoneFromProfileAsync(suggestion.NightOf, cancellationToken)
            ?? TimeZoneInfo.Utc;

        // Get overnight window for entries in user's local time
        var (windowStart, windowEnd) = TimeZoneHelper.GetOvernightWindow(
            suggestion.NightOf,
            userTimeZone,
            sleepSchedule.BedtimeHour,
            sleepSchedule.WakeTimeHour);

        // Get entries for the window
        var entries = await _entryService.GetEntriesAsync(
            find: $"{{\"mills\":{{\"$gte\":{windowStart},\"$lte\":{windowEnd}}}}}",
            count: 1000,
            skip: 0,
            cancellationToken: cancellationToken);

        // Get treatments for the window (boluses, temp basals, etc.)
        var treatments = await _treatmentService.GetTreatmentsAsync(
            find: $"{{\"mills\":{{\"$gte\":{windowStart},\"$lte\":{windowEnd}}}}}",
            cancellationToken: cancellationToken);

        return new CompressionLowSuggestionWithEntries
        {
            Suggestion = suggestion,
            Entries = entries.OrderBy(e => e.Mills),
            Treatments = treatments.OrderBy(t => t.Mills)
        };
    }

    public async Task<StateSpan> AcceptSuggestionAsync(
        Guid id,
        long startMills,
        long endMills,
        CancellationToken cancellationToken = default)
    {
        var suggestion = await _repository.GetByIdAsync(id, cancellationToken);
        if (suggestion == null)
            throw new InvalidOperationException($"Suggestion {id} not found");

        if (suggestion.Status != CompressionLowStatus.Pending)
            throw new InvalidOperationException($"Suggestion {id} is not pending");

        // Create DataExclusion StateSpan
        var stateSpan = new StateSpan
        {
            Category = StateSpanCategory.DataExclusion,
            State = "CompressionLow",
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startMills).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(endMills).UtcDateTime,
            Source = "compression-low-detection",
            Metadata = new Dictionary<string, object>
            {
                ["Confidence"] = suggestion.Confidence,
                ["DetectedAt"] = suggestion.CreatedAt,
                ["SuggestionId"] = suggestion.Id.ToString()
            }
        };

        var createdSpan = await _stateSpanService.UpsertStateSpanAsync(stateSpan, cancellationToken);

        // Update suggestion
        suggestion.Status = CompressionLowStatus.Accepted;
        suggestion.ReviewedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        suggestion.StateSpanId = Guid.TryParse(createdSpan.Id, out var spanId) ? spanId : null;

        await _repository.UpdateAsync(suggestion, cancellationToken);

        // Check if we should archive the notification
        await TryArchiveNotificationAsync(suggestion.NightOf, cancellationToken);

        _logger.LogInformation(
            "Accepted compression low suggestion {SuggestionId}, created StateSpan {StateSpanId}",
            id, createdSpan.Id);

        return createdSpan;
    }

    public async Task DismissSuggestionAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var suggestion = await _repository.GetByIdAsync(id, cancellationToken);
        if (suggestion == null)
            throw new InvalidOperationException($"Suggestion {id} not found");

        if (suggestion.Status != CompressionLowStatus.Pending)
            throw new InvalidOperationException($"Suggestion {id} is not pending");

        suggestion.Status = CompressionLowStatus.Dismissed;
        suggestion.ReviewedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _repository.UpdateAsync(suggestion, cancellationToken);

        // Check if we should archive the notification
        await TryArchiveNotificationAsync(suggestion.NightOf, cancellationToken);

        _logger.LogInformation("Dismissed compression low suggestion {SuggestionId}", id);
    }

    public async Task DeleteSuggestionAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var suggestion = await _repository.GetByIdAsync(id, cancellationToken);
        if (suggestion == null)
            throw new InvalidOperationException($"Suggestion {id} not found");

        // If accepted, delete the associated state span
        if (suggestion.Status == CompressionLowStatus.Accepted && suggestion.StateSpanId.HasValue)
        {
            try
            {
                await _stateSpanService.DeleteStateSpanAsync(
                    suggestion.StateSpanId.Value.ToString(),
                    cancellationToken);
                _logger.LogInformation(
                    "Deleted StateSpan {StateSpanId} for suggestion {SuggestionId}",
                    suggestion.StateSpanId, id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to delete StateSpan {StateSpanId} for suggestion {SuggestionId}",
                    suggestion.StateSpanId, id);
            }
        }

        await _repository.DeleteAsync(id, cancellationToken);

        _logger.LogInformation("Deleted compression low suggestion {SuggestionId}", id);
    }

    private async Task TryArchiveNotificationAsync(
        DateOnly nightOf,
        CancellationToken cancellationToken)
    {
        var pendingCount = await _repository.CountPendingForNightAsync(nightOf, cancellationToken);
        if (pendingCount == 0)
        {
            try
            {
                await _notificationService.ArchiveBySourceAsync(
                    userId: "default",
                    type: "glucose.compression_low_review",
                    sourceId: nightOf.ToString("yyyy-MM-dd"),
                    reason: NotificationArchiveReason.Completed,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to archive compression low notification for {NightOf}", nightOf);
            }
        }
    }

    private static TimeZoneInfo? ResolveTimeZone(string? timezoneId)
    {
        if (string.IsNullOrEmpty(timezoneId))
            return null;

        var tz = TimeZoneHelper.GetTimeZoneInfoFromId(timezoneId);
        if (tz == TimeZoneInfo.Utc && !timezoneId.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return null;

        return tz;
    }

    private async Task<TimeZoneInfo?> GetUserTimeZoneFromProfileAsync(
        DateOnly nightOf,
        CancellationToken cancellationToken)
    {
        try
        {
            var timezoneId = await _therapySettingsResolver.GetTimezoneAsync(ct: cancellationToken);
            return ResolveTimeZone(timezoneId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get user timezone from profile for night {NightOf}", nightOf);
            return null;
        }
    }
}
