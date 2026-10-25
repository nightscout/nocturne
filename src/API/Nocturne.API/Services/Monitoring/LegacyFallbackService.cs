using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// A consumable the tenant has no tracker for, aged from its latest device event.
/// </summary>
/// <param name="Projected">
/// An unsaved run of the tracker <see cref="ILegacyFallbackService.CreateTrackersAsync"/> would
/// create, started at <paramref name="LatestEvent"/>, so its schedule is the one that tracker shows.
/// </param>
public sealed record LegacyFallback(
    LegacyConsumableKind Kind,
    DeviceEvent LatestEvent,
    ConsumableTrackerSpec Spec,
    TrackerInstanceEntity Projected);

/// <summary>A consumable the tenant has a tracker for, and the definitions that cover it.</summary>
/// <param name="DefinitionIds">Every definition <see cref="LegacyFallbackRules.Covers"/> the kind.</param>
public sealed record LegacyCoverage(LegacyConsumableKind Kind, IReadOnlyList<Guid> DefinitionIds);

/// <summary>What <see cref="ILegacyFallbackService.GetAsync"/> found.</summary>
/// <param name="Fallbacks">The uncovered consumables with an age to show.</param>
/// <param name="Covered">The covered consumables, in <see cref="LegacyConsumableKind"/> order.</param>
public sealed record LegacyFallbackRead(
    IReadOnlyList<LegacyFallback> Fallbacks,
    IReadOnlyList<LegacyCoverage> Covered);

/// <summary>What <see cref="ILegacyFallbackService.CreateTrackersAsync"/> did.</summary>
/// <param name="Definitions">The trackers it created.</param>
/// <param name="Instances">Their running instances.</param>
/// <param name="FailedKinds">The kinds whose transaction failed, which committed nothing.</param>
public sealed record LegacyConversion(
    IReadOnlyList<TrackerDefinitionEntity> Definitions,
    IReadOnlyList<TrackerInstanceEntity> Instances,
    IReadOnlyList<LegacyConsumableKind> FailedKinds);

/// <summary>
/// The legacy age pills (SAGE, CAGE, IAGE, BAGE) for a tenant whose trackers do not cover them,
/// and their conversion into trackers.
/// </summary>
public interface ILegacyFallbackService
{
    /// <summary>
    /// The uncovered consumables <see cref="LegacyFallbackRules.IsConfident"/> accepts, in
    /// <see cref="LegacyConsumableKind"/> order, and the definitions covering the rest.
    /// </summary>
    /// <param name="readDeviceAges">
    /// False for a caller that may not read device events: coverage comes from the definitions
    /// alone, and no fallback is resolved.
    /// </param>
    /// <remarks>A history-clamped member reads only the last day of device events, so sees fewer.</remarks>
    Task<LegacyFallbackRead> GetAsync(bool readDeviceAges, CancellationToken ct = default);

    /// <summary>
    /// Creates a tracker for each of <paramref name="kinds"/> that has a fallback, recording
    /// <paramref name="userId"/> as its creator, with its first run started at the latest matching
    /// event. A kind already covered is skipped, so repeating the request creates nothing.
    /// </summary>
    /// <remarks>
    /// The run's thresholds that were already due when it is recorded do not alert for it
    /// (docs/alerts/engine-semantics.md §3), so converting an old consumable does not page at once.
    /// </remarks>
    Task<LegacyConversion> CreateTrackersAsync(
        IReadOnlyCollection<LegacyConsumableKind> kinds, string userId, CancellationToken ct = default);
}

/// <inheritdoc />
internal sealed class LegacyFallbackService(
    ITrackerRepository trackers,
    IDeviceEventRepository deviceEvents,
    IPatientDeviceRepository patientDevices,
    ConsumableTrackerWriter writer,
    TimeProvider timeProvider,
    ILogger<LegacyFallbackService> logger) : ILegacyFallbackService
{
    /// <inheritdoc />
    public async Task<LegacyFallbackRead> GetAsync(bool readDeviceAges, CancellationToken ct = default)
    {
        var definitions = await trackers.GetAllDefinitionsAsync(ct);
        var covered = Enum.GetValues<LegacyConsumableKind>()
            .Select(kind => new LegacyCoverage(
                kind, [.. definitions.Where(d => LegacyFallbackRules.Covers(kind, d)).Select(d => d.Id)]))
            .Where(coverage => coverage.DefinitionIds.Count > 0)
            .ToList();
        var fallbacks = readDeviceAges
            ? await ResolveAsync(Enum.GetValues<LegacyConsumableKind>(), definitions, ct)
            : [];
        return new LegacyFallbackRead(fallbacks, covered);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Each kind is one transaction under the consumable lock: the coverage check, the definition,
    /// its rules and its first run commit together or not at all, and a second caller converting
    /// the same kind finds it covered.
    /// </remarks>
    public async Task<LegacyConversion> CreateTrackersAsync(
        IReadOnlyCollection<LegacyConsumableKind> kinds, string userId, CancellationToken ct = default)
    {
        var definitions = new List<TrackerDefinitionEntity>();
        var instances = new List<TrackerInstanceEntity>();
        var failed = new List<LegacyConsumableKind>();
        foreach (var kind in kinds.Distinct().Order())
        {
            CreatedConsumableTracker? created;
            try
            {
                created = await trackers.ExecuteUnderConsumableLockAsync(
                    async (tx, unit, token) =>
                        (await ResolveAsync([kind], await tx.GetAllDefinitionsAsync(token), token)).SingleOrDefault()
                            is { } fallback
                            ? await writer.CreateAsync(tx, unit, fallback.Spec, userId, withAlerts: true, token)
                            : null,
                    async (tx, attempt, token) =>
                        attempt is null || await ConsumableTrackerWriter.LandedAsync(tx, attempt, token),
                    ct);
            }
            catch (DbUpdateException ex)
            {
                logger.LogError(ex, "Converting the legacy {Kind} age into a tracker failed", kind);
                failed.Add(kind);
                continue;
            }
            catch (DbException ex)
            {
                logger.LogError(ex, "Converting the legacy {Kind} age into a tracker failed", kind);
                failed.Add(kind);
                continue;
            }
            catch (RetryLimitExceededException ex)
            {
                logger.LogError(ex, "Converting the legacy {Kind} age into a tracker failed", kind);
                failed.Add(kind);
                continue;
            }
            if (created is null)
                continue;

            var (definition, run) = (created.Definition, created.Run);
            try
            {
                if (await writer.PublishAsync(trackers, created, CancellationToken.None) is var (published, current))
                    (definition, run) = (published, current);
            }
            catch (DbUpdateException ex)
            {
                logger.LogError(ex, "Announcing tracker {DefinitionId} failed; it is converted", definition.Id);
            }
            catch (DbException ex)
            {
                logger.LogError(ex, "Announcing tracker {DefinitionId} failed; it is converted", definition.Id);
            }
            catch (RetryLimitExceededException ex)
            {
                logger.LogError(ex, "Announcing tracker {DefinitionId} failed; it is converted", definition.Id);
            }

            definitions.Add(definition);
            if (run is not null)
                instances.Add(run);
            logger.LogInformation("Created tracker {DefinitionId} for the legacy {Kind} age", definition.Id, kind);
        }

        return new LegacyConversion(definitions, instances, failed);
    }

    private async Task<IReadOnlyList<LegacyFallback>> ResolveAsync(
        IReadOnlyCollection<LegacyConsumableKind> kinds,
        IReadOnlyCollection<TrackerDefinitionEntity> definitions,
        CancellationToken ct)
    {
        var uncovered = kinds
            .Where(kind => !LegacyFallbackRules.IsCovered(kind, definitions))
            .Order()
            .ToList();
        if (uncovered.Count == 0)
            return [];

        var devices = (await patientDevices.GetCurrentAsync(ct)).ToList();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var fallbacks = new List<LegacyFallback>();
        foreach (var kind in uncovered)
        {
            if (ConsumableTrackerSpecs.For(kind, devices) is not { } spec)
                continue;
            var eventTypes = ConsumableTrackerSpecs.TriggerEventTypes(spec).ToArray();
            if (await deviceEvents.GetLatestByEventTypesAsync(eventTypes, patientDeviceId: null, ct) is not { } latest)
                continue;

            var recentCount = LegacyFallbackRules.RequiresRepeatedEvents(kind)
                ? await deviceEvents.CountByEventTypesSinceAsync(
                    eventTypes, now - LegacyFallbackRules.RepeatWindow, ct)
                : 0;
            if (!LegacyFallbackRules.IsConfident(kind, spec, latest.Timestamp, recentCount, now))
                continue;

            var projected = new TrackerInstanceEntity
            {
                StartedAt = latest.Timestamp,
                Definition = ConsumableTrackerSpecs.ToDefinition(spec, string.Empty),
            };
            fallbacks.Add(new LegacyFallback(kind, latest, spec, projected));
        }

        return fallbacks;
    }
}
