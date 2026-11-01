using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Audit;
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
internal sealed class MetadataPublisher : ConnectorPublisherBase, IMetadataPublisher
{
    private readonly IProfileWriteService _profileWriteService;
    private readonly IFoodService _foodService;
    private readonly IConnectorFoodEntryService _connectorFoodEntryService;
    private readonly IActivityService _activityService;
    private readonly IStateSpanService _stateSpanService;
    private readonly ISystemEventRepository _systemEventRepository;
    private readonly INoteRepository _noteRepository;
    private readonly IBodyWeightService _bodyWeightService;
    private readonly IStepCountService _stepCountService;
    private readonly IHeartRateService _heartRateService;
    private readonly ITenantOwnerResolver _tenantOwnerResolver;
    private readonly IActivityDedupRepository _activityDedup;
    private readonly IConnectorConfigurationRepository _configurations;
    private readonly IProfileActivationRepository _profileActivations;
    private readonly ProfileActivationWriter _profileActivationWriter;

    public MetadataPublisher(
        IProfileWriteService profileWriteService,
        IFoodService foodService,
        IConnectorFoodEntryService connectorFoodEntryService,
        IActivityService activityService,
        IStateSpanService stateSpanService,
        ISystemEventRepository systemEventRepository,
        INoteRepository noteRepository,
        IBodyWeightService bodyWeightService,
        IStepCountService stepCountService,
        IHeartRateService heartRateService,
        ITenantOwnerResolver tenantOwnerResolver,
        ITenantAccessor tenantAccessor,
        IActivityDedupRepository activityDedup,
        IConnectorConfigurationRepository configurations,
        IProfileActivationRepository profileActivations,
        ProfileActivationWriter profileActivationWriter,
        IAuditContext auditContext,
        PublishSkipTally skips,
        ILogger<MetadataPublisher> logger)
        : base(auditContext, skips, tenantAccessor, logger)
    {
        _profileActivations = profileActivations ?? throw new ArgumentNullException(nameof(profileActivations));
        _profileActivationWriter = profileActivationWriter ?? throw new ArgumentNullException(nameof(profileActivationWriter));
        _profileWriteService = profileWriteService ?? throw new ArgumentNullException(nameof(profileWriteService));
        _foodService = foodService ?? throw new ArgumentNullException(nameof(foodService));
        _connectorFoodEntryService = connectorFoodEntryService ?? throw new ArgumentNullException(nameof(connectorFoodEntryService));
        _activityService = activityService ?? throw new ArgumentNullException(nameof(activityService));
        _stateSpanService = stateSpanService ?? throw new ArgumentNullException(nameof(stateSpanService));
        _systemEventRepository = systemEventRepository ?? throw new ArgumentNullException(nameof(systemEventRepository));
        _noteRepository = noteRepository ?? throw new ArgumentNullException(nameof(noteRepository));
        _bodyWeightService = bodyWeightService ?? throw new ArgumentNullException(nameof(bodyWeightService));
        _stepCountService = stepCountService ?? throw new ArgumentNullException(nameof(stepCountService));
        _heartRateService = heartRateService ?? throw new ArgumentNullException(nameof(heartRateService));
        _tenantOwnerResolver = tenantOwnerResolver ?? throw new ArgumentNullException(nameof(tenantOwnerResolver));
        _activityDedup = activityDedup ?? throw new ArgumentNullException(nameof(activityDedup));
        _configurations = configurations ?? throw new ArgumentNullException(nameof(configurations));
    }

    /// <summary>
    /// The subject a connector's food entries, and the match suggestions they raise, are attributed
    /// to. A sync has no user of its own, and the UI lists notifications by subject id.
    /// </summary>
    private async Task<string?> ResolveNotificationSubjectAsync(
        string source,
        CancellationToken cancellationToken)
    {
        if (!TenantAccessor.IsResolved)
        {
            Logger.LogWarning(
                "No tenant resolved while publishing for {Source}; cannot attribute its notifications",
                source);
            return null;
        }

        var subjectId = await _tenantOwnerResolver.GetOwnerSubjectIdAsync(
            TenantAccessor.TenantId, cancellationToken);

        if (subjectId == null)
        {
            Logger.LogWarning(
                "Tenant {TenantId} has no owner; {Source} food entries will import without match suggestions",
                TenantAccessor.TenantId,
                source);
        }

        return subjectId;
    }

    public async Task<bool> PublishProfilesAsync(
        IEnumerable<Profile> profiles,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        var profileList = profiles.ToList();
        try
        {
            await _profileWriteService.CreateProfilesAsync(profileList, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogPublishFailure(ex, profileList, source);
            return false;
        }
    }

    public async Task<bool> PublishFoodAsync(
        IEnumerable<Food> foods,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        var foodList = foods.ToList();
        try
        {
            await _foodService.CreateFoodAsync(foodList, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogPublishFailure(ex, foodList, source);
            return false;
        }
    }

    public async Task<IReadOnlyList<ConnectorFoodEntry>?> PublishConnectorFoodEntriesAsync(
        IEnumerable<ConnectorFoodEntryImport> entries,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        var entryList = entries.ToList();
        try
        {
            var subject = await ResolveNotificationSubjectAsync(source, cancellationToken);
            using (PushSystemAudit())
            {
                return await _connectorFoodEntryService.ImportAsync(subject, entryList, cancellationToken);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogPublishFailure(ex, entryList, source);
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
            var subject = await ResolveNotificationSubjectAsync(source, cancellationToken);
            using (PushSystemAudit())
            {
                return await _connectorFoodEntryService.MarkMissingAsDeletedAsync(
                    subject, source, from, to, presentExternalEntryIds, cancellationToken);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to reconcile connector food entries for {Source}", source);
            return null;
        }
    }

    public async Task<bool> PublishActivityAsync(
        IEnumerable<Activity> activities,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
        => await WriteActivityAsync(activities, source, cancellationToken) is not null;

    /// <returns>How many activities were written, or null when the write failed.</returns>
    private async Task<int?> WriteActivityAsync(
        IEnumerable<Activity> activities,
        string source,
        CancellationToken cancellationToken)
    {
        var activityList = activities.ToList();
        try
        {
            // Stamp the publishing connector so every decomposed destination (state spans, heart
            // rates, step counts) resolves to this connector's watermark.
            foreach (var activity in activityList)
                activity.DataSource = source;

            var written = await _activityService.CreateActivitiesAsync(activityList, cancellationToken);
            return written.Count();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogPublishFailure(ex, activityList, source);
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>Held ids are read as <see cref="IActivityDedupRepository"/> describes.</remarks>
    public Task<int?> PublishRecentActivityAsync(
        IEnumerable<Activity> activities,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
        => PublishUnheldAsync(
            activities, a => a.Id,
            unheld => WriteActivityAsync(unheld, source, cancellationToken),
            source,
            ids => _activityDedup.GetHeldIdsAsync(ids, cancellationToken));

    /// <remarks>
    /// A Profile span, as an instance before profile activations replays it, is published as the
    /// effective activation it records (<see cref="PublishProfileActivationsAsync"/>).
    /// </remarks>
    public async Task<bool> PublishStateSpansAsync(
        IEnumerable<StateSpan> stateSpans,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        var spans = stateSpans.ToList();
        var profileSpans = spans.Where(s => s.Category == StateSpanCategory.Profile).ToList();
        if (profileSpans.Count > 0
            && !await PublishProfileActivationsAsync(
                profileSpans.Select(ConnectorProfileActivations.FromSpan), source, origin, cancellationToken))
            return false;

        try
        {
            foreach (var span in spans.Where(s => s.Category != StateSpanCategory.Profile))
            {
                // Source is the connector writing the row here, not one carried in the payload:
                // NocturneRemote replays another instance's spans verbatim, and honouring their
                // Source would advance a watermark the named connector never earned. A displaced
                // value is stashed so the remote origin stays recoverable.
                if (!string.IsNullOrEmpty(span.Source) && span.Source != source)
                {
                    span.Metadata ??= [];
                    span.Metadata["originSource"] = span.Source;
                }

                span.Source = source;
                await _stateSpanService.UpsertStateSpanAsync(span, cancellationToken);
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogPublishFailure(ex, spans, source);
            return false;
        }
    }

    /// <remarks>
    /// Each activation is written oldest first and settled against its neighbours, so one published
    /// after an earlier sync's still-open activation ends it. One the user deleted stays deleted.
    /// </remarks>
    public async Task<bool> PublishProfileActivationsAsync(
        IEnumerable<ProfileActivation> activations,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        var activationList = activations.OrderBy(a => a.StartTimestamp).ToList();
        try
        {
            foreach (var activation in activationList)
            {
                activation.DataSource = source;
                activation.Phase = ProfileActivationPhase.Effective;
                activation.Origin = ProfileActivationOrigin.Connector;
                var stored = string.IsNullOrEmpty(activation.SyncIdentifier)
                    ? null
                    : await _profileActivations.FindBySyncIdentifierAsync(source, activation.SyncIdentifier, cancellationToken);

                await _profileActivationWriter.WriteAsync(activation, stored, async settled =>
                {
                    try
                    {
                        return (await _profileActivations.UpsertAsync(settled, origin, cancellationToken)).Record;
                    }
                    catch (RecreationBlockedException)
                    {
                        return null;
                    }
                }, cancellationToken);
            }
            return true;
        }
        catch (DbUpdateException ex)
        {
            LogPublishFailure(ex, activationList, source);
            return false;
        }
    }

    public async Task<bool> PublishSystemEventsAsync(
        IEnumerable<SystemEvent> systemEvents,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        var systemEventList = systemEvents.ToList();
        try
        {
            await _systemEventRepository.BulkUpsertAsync(systemEventList, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogPublishFailure(ex, systemEventList, source);
            return false;
        }
    }

    public Task<bool> PublishNotesAsync(
        IEnumerable<Note> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
        => PublishAsync(records, _noteRepository, source, origin, cancellationToken);

    public async Task<bool> PublishBodyWeightsAsync(
        IEnumerable<BodyWeight> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        var list = records.ToList();
        try
        {
            if (list.Count == 0) return true;

            await _bodyWeightService.CreateBodyWeightsAsync(list, cancellationToken);

            Logger.LogDebug("Published {Count} BodyWeight records for {Source}", list.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogPublishFailure(ex, list, source);
            return false;
        }
    }

    public async Task<bool> PublishStepCountsAsync(
        IEnumerable<StepCount> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        var list = records.ToList();
        try
        {
            if (list.Count == 0) return true;

            await _stepCountService.CreateStepCountsAsync(list, cancellationToken);

            Logger.LogDebug("Published {Count} StepCount records for {Source}", list.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogPublishFailure(ex, list, source);
            return false;
        }
    }

    public async Task<bool> PublishHeartRatesAsync(
        IEnumerable<HeartRate> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        var list = records.ToList();
        try
        {
            if (list.Count == 0) return true;

            await _heartRateService.CreateHeartRatesAsync(list, cancellationToken);

            Logger.LogDebug("Published {Count} HeartRate records for {Source}", list.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogPublishFailure(ex, list, source);
            return false;
        }
    }

    /// <inheritdoc />
    public Task<DateTime?> GetLatestActivityTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
        => _activityService.GetLatestTimestampAsync(source, cancellationToken);

    /// <inheritdoc />
    public Task<DateTime?> GetLatestStateSpanTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
        => _stateSpanService.GetLatestNonActivityTimestampAsync(source, cancellationToken);

    /// <inheritdoc />
    public Task<DateTime?> GetBackfillLowWaterMarkAsync(
        string source,
        string collection,
        CancellationToken cancellationToken = default)
        => _configurations.GetBackfillLowWaterMarkAsync(source, collection, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Same-key races (a manual sync or cursor-reset job racing the background sync) stay
    /// last-writer-wins: any surviving mark is a valid resume point because the resume crawl is
    /// unbounded below it. Different keys on the same row keep each other; see
    /// <see cref="Infrastructure.Data.Repositories.ConnectorConfigurationRepository"/>.
    /// </remarks>
    public async Task SetBackfillLowWaterMarkAsync(
        string source,
        string collection,
        DateTime? lowWaterMark,
        CancellationToken cancellationToken = default)
    {
        if (!await _configurations.SetBackfillLowWaterMarkAsync(source, collection, lowWaterMark, cancellationToken))
        {
            Logger.LogWarning(
                "No connector configuration found for {Source}; cannot persist backfill low-water mark",
                source);
        }
    }
}
