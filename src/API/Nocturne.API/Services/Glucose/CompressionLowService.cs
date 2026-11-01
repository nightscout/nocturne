using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Entries;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Domain service for compression low suggestion management. Provides retrieval, review, and
/// dismissal of <see cref="CompressionLowSuggestion"/> records detected by
/// <see cref="CompressionLowDetectionService"/>, including enrichment of
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
    private readonly ICompressionLowNightWindowResolver _nightWindowResolver;
    private readonly IUISettingsService _uiSettingsService;
    private readonly ITenantOwnerResolver _tenantOwnerResolver;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly TimeProvider _time;
    private readonly ILogger<CompressionLowService> _logger;

    public CompressionLowService(
        ICompressionLowRepository repository,
        IStateSpanService stateSpanService,
        IEntryService entryService,
        ITreatmentService treatmentService,
        IInAppNotificationService notificationService,
        ICompressionLowNightWindowResolver nightWindowResolver,
        IUISettingsService uiSettingsService,
        ITenantOwnerResolver tenantOwnerResolver,
        ITenantAccessor tenantAccessor,
        TimeProvider timeProvider,
        ILogger<CompressionLowService> logger)
    {
        _repository = repository;
        _stateSpanService = stateSpanService;
        _entryService = entryService;
        _treatmentService = treatmentService;
        _notificationService = notificationService;
        _nightWindowResolver = nightWindowResolver;
        _uiSettingsService = uiSettingsService;
        _tenantOwnerResolver = tenantOwnerResolver;
        _tenantAccessor = tenantAccessor;
        _time = timeProvider;
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

        // The overnight window is cut from the tenant's own bedtime and wake time, and a null
        // return from this method already means "no such suggestion".
        // <see cref="SettingsUnavailableException"/>.
        var settings = await _uiSettingsService.GetSettingsAsync(cancellationToken)
            ?? throw new SettingsUnavailableException("UI settings");

        var window = await _nightWindowResolver.ResolveAsync(
            suggestion.NightOf, settings.DataQuality.SleepSchedule, cancellationToken);

        // Get entries for the window
        var entries = await _entryService.GetEntriesAsync(
            find: $"{{\"mills\":{{\"$gte\":{window.WindowStart},\"$lte\":{window.WindowEnd}}}}}",
            count: 1000,
            skip: 0,
            cancellationToken: cancellationToken);

        // Get treatments for the window (boluses, temp basals, etc.)
        var treatments = await _treatmentService.GetTreatmentsAsync(
            find: $"{{\"mills\":{{\"$gte\":{window.WindowStart},\"$lte\":{window.WindowEnd}}}}}",
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
        CancellationToken cancellationToken = default,
        bool demoSeed = false)
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
        if (demoSeed)
            stateSpan.Metadata["DemoSeed"] = true;

        var createdSpan = await _stateSpanService.UpsertStateSpanAsync(stateSpan, cancellationToken);

        // Update suggestion
        suggestion.Status = CompressionLowStatus.Accepted;
        suggestion.ReviewedAt = _time.GetUtcNow().ToUnixTimeMilliseconds();
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
        suggestion.ReviewedAt = _time.GetUtcNow().ToUnixTimeMilliseconds();

        await _repository.UpdateAsync(suggestion, cancellationToken);

        // Check if we should archive the notification
        await TryArchiveNotificationAsync(suggestion.NightOf, cancellationToken);

        _logger.LogInformation("Dismissed compression low suggestion {SuggestionId}", id);
    }

    private async Task DeleteSuggestionAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var suggestion = await _repository.GetByIdAsync(id, cancellationToken);
        if (suggestion == null)
            return;

        // If accepted, delete the associated state span
        if (suggestion.Status == CompressionLowStatus.Accepted && suggestion.StateSpanId.HasValue)
        {
            await _stateSpanService.DeleteStateSpanAsync(
                suggestion.StateSpanId.Value.ToString(),
                cancellationToken);
            _logger.LogInformation(
                "Deleted StateSpan {StateSpanId} for suggestion {SuggestionId}",
                suggestion.StateSpanId, id);
        }

        await _repository.DeleteAsync(id, cancellationToken);

        _logger.LogInformation("Deleted compression low suggestion {SuggestionId}", id);
    }

    public async Task ApplyBatchAsync(
        CompressionLowBatchAction action,
        IReadOnlyCollection<CompressionLowBatchItem> items,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(action))
            throw new InvalidOperationException($"Unknown batch action {(int)action}");

        var due = new List<(CompressionLowBatchItem Item, CompressionLowSuggestion Suggestion)>();
        foreach (var item in items.DistinctBy(i => i.Id))
        {
            var suggestion = await _repository.GetByIdAsync(item.Id, cancellationToken);
            if (suggestion == null)
            {
                if (action != CompressionLowBatchAction.Delete)
                    throw new InvalidOperationException($"Suggestion {item.Id} not found");
                continue;
            }

            if (NeedsApplying(action, suggestion))
                due.Add((item, suggestion));
        }

        foreach (var (item, suggestion) in due)
        {
            switch (action)
            {
                case CompressionLowBatchAction.Accept:
                    await AcceptSuggestionAsync(
                        item.Id,
                        item.StartMills ?? suggestion.StartMills,
                        item.EndMills ?? suggestion.EndMills,
                        cancellationToken);
                    break;
                case CompressionLowBatchAction.Dismiss:
                    await DismissSuggestionAsync(item.Id, cancellationToken);
                    break;
                case CompressionLowBatchAction.Delete:
                    await DeleteSuggestionAsync(item.Id, cancellationToken);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }
    }

    private static bool NeedsApplying(CompressionLowBatchAction action, CompressionLowSuggestion suggestion)
    {
        var applied = action switch
        {
            CompressionLowBatchAction.Accept => CompressionLowStatus.Accepted,
            CompressionLowBatchAction.Dismiss => CompressionLowStatus.Dismissed,
            CompressionLowBatchAction.Delete => (CompressionLowStatus?)null,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
        if (suggestion.Status == applied)
            return false;
        if (applied != null && suggestion.Status != CompressionLowStatus.Pending)
            throw new InvalidOperationException($"Suggestion {suggestion.Id} is not pending");
        return true;
    }

    private async Task TryArchiveNotificationAsync(
        DateOnly nightOf,
        CancellationToken cancellationToken)
    {
        var pendingCount = await _repository.CountPendingForNightAsync(nightOf, cancellationToken);
        if (pendingCount != 0)
            return;

        try
        {
            var ownerId = await _tenantOwnerResolver.GetOwnerSubjectIdAsync(
                _tenantAccessor.TenantId, cancellationToken);

            if (ownerId == null)
            {
                _logger.LogWarning(
                    "No owner found for tenant {TenantId}; leaving the compression low notification for {NightOf} active",
                    _tenantAccessor.TenantId, nightOf);
                return;
            }

            await _notificationService.ArchiveBySourceAsync(
                userId: ownerId,
                type: CompressionLowDetectionService.NotificationType,
                sourceId: CompressionLowDetectionService.NotificationSourceId(nightOf),
                reason: NotificationArchiveReason.Completed,
                cancellationToken: cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            LogArchiveFailure(ex, nightOf);
        }
        catch (DbException ex)
        {
            LogArchiveFailure(ex, nightOf);
        }
        catch (RetryLimitExceededException ex)
        {
            LogArchiveFailure(ex, nightOf);
        }
    }

    private void LogArchiveFailure(Exception ex, DateOnly nightOf) =>
        _logger.LogWarning(ex, "Failed to archive compression low notification for {NightOf}", nightOf);
}
