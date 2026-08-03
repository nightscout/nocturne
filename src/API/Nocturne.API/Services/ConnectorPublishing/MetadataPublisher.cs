using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Services.ConnectorPublishing;

/// <summary>
/// Publishes profile, food, activity, state-span, system event, and note data received from
/// connectors into the Nocturne domain via the appropriate service and repository interfaces.
/// </summary>
/// <seealso cref="IMetadataPublisher"/>
internal sealed class MetadataPublisher : IMetadataPublisher
{
    private readonly IProfileWriteService _profileWriteService;
    private readonly IFoodService _foodService;
    private readonly IConnectorFoodEntryService _connectorFoodEntryService;
    private readonly IActivityService _activityService;
    private readonly IStateSpanService _stateSpanService;
    private readonly ISystemEventRepository _systemEventRepository;
    private readonly INoteRepository _noteRepository;
    private readonly ITenantOwnerResolver _tenantOwnerResolver;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly NocturneDbContext _db;
    private readonly ILogger<MetadataPublisher> _logger;

    public MetadataPublisher(
        IProfileWriteService profileWriteService,
        IFoodService foodService,
        IConnectorFoodEntryService connectorFoodEntryService,
        IActivityService activityService,
        IStateSpanService stateSpanService,
        ISystemEventRepository systemEventRepository,
        INoteRepository noteRepository,
        ITenantOwnerResolver tenantOwnerResolver,
        ITenantAccessor tenantAccessor,
        NocturneDbContext db,
        ILogger<MetadataPublisher> logger)
    {
        _profileWriteService = profileWriteService ?? throw new ArgumentNullException(nameof(profileWriteService));
        _foodService = foodService ?? throw new ArgumentNullException(nameof(foodService));
        _connectorFoodEntryService = connectorFoodEntryService ?? throw new ArgumentNullException(nameof(connectorFoodEntryService));
        _activityService = activityService ?? throw new ArgumentNullException(nameof(activityService));
        _stateSpanService = stateSpanService ?? throw new ArgumentNullException(nameof(stateSpanService));
        _systemEventRepository = systemEventRepository ?? throw new ArgumentNullException(nameof(systemEventRepository));
        _noteRepository = noteRepository ?? throw new ArgumentNullException(nameof(noteRepository));
        _tenantOwnerResolver = tenantOwnerResolver ?? throw new ArgumentNullException(nameof(tenantOwnerResolver));
        _tenantAccessor = tenantAccessor ?? throw new ArgumentNullException(nameof(tenantAccessor));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The subject a connector's food entries, and the match suggestions they raise, are attributed
    /// to. A sync has no user of its own, and the UI lists notifications by subject id.
    /// </summary>
    private async Task<string?> ResolveNotificationSubjectAsync(
        string source,
        CancellationToken cancellationToken)
    {
        if (!_tenantAccessor.IsResolved)
        {
            _logger.LogWarning(
                "No tenant resolved while publishing for {Source}; cannot attribute its notifications",
                source);
            return null;
        }

        var subjectId = await _tenantOwnerResolver.GetOwnerSubjectIdAsync(
            _tenantAccessor.TenantId, cancellationToken);

        if (subjectId == null)
        {
            _logger.LogWarning(
                "Tenant {TenantId} has no owner; {Source} food entries will import without match suggestions",
                _tenantAccessor.TenantId,
                source);
        }

        return subjectId;
    }

    public async Task<bool> PublishProfilesAsync(
        IEnumerable<Profile> profiles,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            await _profileWriteService.CreateProfilesAsync(profiles, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish profiles for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishFoodAsync(
        IEnumerable<Food> foods,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            await _foodService.CreateFoodAsync(foods, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish food for {Source}", source);
            return false;
        }
    }

    public async Task<IReadOnlyList<ConnectorFoodEntry>?> PublishConnectorFoodEntriesAsync(
        IEnumerable<ConnectorFoodEntryImport> entries,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _connectorFoodEntryService.ImportAsync(
                await ResolveNotificationSubjectAsync(source, cancellationToken),
                entries,
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish connector food entries for {Source}", source);
            return null;
        }
    }

    public async Task<int?> ReconcileConnectorFoodEntriesAsync(
        IEnumerable<string> presentExternalEntryIds,
        DateTimeOffset from,
        DateTimeOffset to,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _connectorFoodEntryService.MarkMissingAsDeletedAsync(
                await ResolveNotificationSubjectAsync(source, cancellationToken),
                source,
                from,
                to,
                presentExternalEntryIds,
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile connector food entries for {Source}", source);
            return null;
        }
    }

    public async Task<bool> PublishActivityAsync(
        IEnumerable<Activity> activities,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            await _activityService.CreateActivitiesAsync(activities, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish activities for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishStateSpansAsync(
        IEnumerable<StateSpan> stateSpans,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var span in stateSpans)
            {
                await _stateSpanService.UpsertStateSpanAsync(span, cancellationToken);
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish state spans for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishSystemEventsAsync(
        IEnumerable<SystemEvent> systemEvents,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            await _systemEventRepository.BulkUpsertAsync(systemEvents, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish system events for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishNotesAsync(
        IEnumerable<Note> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            await _noteRepository.BulkCreateAsync(recordList, origin, cancellationToken);
            _logger.LogDebug("Published {Count} Note records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish Note records for {Source}", source);
            return false;
        }
    }

    /// <summary>
    /// Returns the timestamp of the most recent activity record for the current tenant,
    /// or <c>null</c> if none exist. Activities are stored across decomposed sources (StateSpans,
    /// HeartRate, StepCount); <see cref="IActivityService.GetActivitiesAsync"/> merges them and
    /// orders newest-first, so requesting a single record yields the global latest. Like
    /// <see cref="ITreatmentPublisher.GetLatestTreatmentTimestampAsync"/>, this is not source-filtered.
    /// </summary>
    public async Task<DateTime?> GetLatestActivityTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        // TODO: Filter by source to support multi-connector catch-up. Currently returns global latest.
        var latest = (await _activityService.GetActivitiesAsync(
                count: 1,
                skip: 0,
                cancellationToken: cancellationToken))
            .FirstOrDefault();

        if (latest == null)
            return null;

        if (!string.IsNullOrEmpty(latest.CreatedAt)
            && DateTime.TryParse(latest.CreatedAt, out var createdAt))
            return createdAt;

        if (latest.Mills > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(latest.Mills).UtcDateTime;

        return null;
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetBackfillLowWaterMarkAsync(
        string source,
        string collection,
        CancellationToken cancellationToken = default)
    {
        var config = await FindConnectorConfigurationAsync(source, cancellationToken);
        if (config?.BackfillLowWaterMarks is null)
            return null;

        var marks = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(config.BackfillLowWaterMarks);
        return marks is not null && marks.TryGetValue(collection, out var mark)
            ? DateTime.SpecifyKind(mark, DateTimeKind.Utc)
            : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The update is a single jsonb-path statement on PostgreSQL so concurrent writers to
    /// DIFFERENT collections on the same row (a manual sync or cursor-reset job racing the
    /// background sync) can't clobber each other's keys — a read-modify-write of the whole map
    /// could drop a mark, and a dropped mark is stranded history, the exact failure marks exist
    /// to prevent. Same-key races stay last-writer-wins: any surviving mark is a valid resume
    /// point because the resume crawl is unbounded below it.
    /// </remarks>
    public async Task SetBackfillLowWaterMarkAsync(
        string source,
        string collection,
        DateTime? lowWaterMark,
        CancellationToken cancellationToken = default)
    {
        var config = await FindConnectorConfigurationAsync(source, cancellationToken);
        if (config is null)
        {
            _logger.LogWarning(
                "No connector configuration found for {Source}; cannot persist backfill low-water mark",
                source);
            return;
        }

        if (_db.Database.IsNpgsql())
        {
            if (lowWaterMark is null)
            {
                await _db.Database.ExecuteSqlAsync(
                    $"""
                     UPDATE connector_configurations
                     SET backfill_low_water_marks =
                         NULLIF(coalesce(backfill_low_water_marks, jsonb_build_object()) - {collection}, jsonb_build_object())
                     WHERE id = {config.Id}
                     """,
                    cancellationToken);
            }
            else
            {
                // Serialized as an ISO-8601 UTC string ("...Z"), matching what
                // System.Text.Json writes so Get round-trips without a timezone shift.
                var value = lowWaterMark.Value.ToString("O");
                await _db.Database.ExecuteSqlAsync(
                    $"""
                     UPDATE connector_configurations
                     SET backfill_low_water_marks =
                         jsonb_set(coalesce(backfill_low_water_marks, jsonb_build_object()), ARRAY[{collection}], to_jsonb({value}::text))
                     WHERE id = {config.Id}
                     """,
                    cancellationToken);
            }
            return;
        }

        // Non-relational providers (tests): plain read-modify-write on the tracked entity.
        var marks = config.BackfillLowWaterMarks is null
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, DateTime>>(config.BackfillLowWaterMarks) ?? new Dictionary<string, DateTime>();

        if (lowWaterMark is null)
            marks.Remove(collection);
        else
            marks[collection] = lowWaterMark.Value;

        config.BackfillLowWaterMarks = marks.Count == 0 ? null : JsonSerializer.Serialize(marks);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the connector configuration row for a connector data source. Sources follow the
    /// <c>{connector-name}-connector</c> convention (<c>nightscout-connector</c>) while
    /// configuration rows carry the bare connector name, so the suffix is stripped for the
    /// lookup, with an exact match as fallback. Reads without tracking — marks are written via
    /// jsonb-path updates, and a stale tracked snapshot must never flow back into the row.
    /// </summary>
    private async Task<ConnectorConfigurationEntity?> FindConnectorConfigurationAsync(
        string source,
        CancellationToken cancellationToken)
    {
        const string suffix = "-connector";
        var name = source.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? source[..^suffix.Length]
            : source;

        var query = _db.Database.IsNpgsql()
            ? _db.ConnectorConfigurations.AsNoTracking()
            : _db.ConnectorConfigurations;

        return await query
            .FirstOrDefaultAsync(
                c => c.ConnectorName.ToLower() == name.ToLower()
                    || c.ConnectorName.ToLower() == source.ToLower(),
                cancellationToken);
    }
}
