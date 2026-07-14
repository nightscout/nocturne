using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;

namespace Nocturne.API.Services.Seeding;

/// <summary>Persisted record counts from one <see cref="SampleDataSeeder.SeedAsync"/> run.</summary>
public sealed record SampleDataSeedResult(
    int Entries,
    int Treatments,
    int SleepSessions,
    int HeartRates,
    int StepCounts,
    int DeviceChanges,
    int TrackerDefinitions,
    int TrackerInstances,
    int AlertRules,
    int AlertExcursions);

/// <summary>
/// Populates a tenant with realistic sample data using the demo service's oref
/// pharmacokinetic generator plus the scenario-correlated health generators:
/// CGM entries, treatments, device-change events, sleep sessions, heart rate,
/// step counts, consumable trackers, and alert rules with historical alarm
/// firings derived from the generated glucose itself. Everything is written
/// through the normal ingestion services and repositories so device
/// attribution, the v4 canonical glucose stream, and RLS tenant context are
/// handled exactly like production writes.
///
/// Two callers: the dev-only admin endpoints (Development, dataSource
/// "dev-sample") and the demo admin endpoint the demo container invokes after
/// each regenerate (dataSource "demo-service").
/// </summary>
public class SampleDataSeeder
{
    private readonly ITenantAccessor _tenantAccessor;
    private readonly NocturneDbContext _db;
    private readonly IEntryService _entryService;
    private readonly ITreatmentService _treatmentService;
    private readonly ISleepService _sleepService;
    private readonly IHeartRateService _heartRateService;
    private readonly IStepCountService _stepCountService;
    private readonly ITrackerRepository _trackerRepository;
    private readonly IRuleScopeClassifier _scopeClassifier;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SampleDataSeeder> _logger;

    private const int BatchSize = 500;
    private const int MaxDays = 90;

    /// <summary>Most recent alarm episodes to materialize as excursions/instances.</summary>
    private const int MaxAlarmEpisodes = 40;

    /// <summary>
    /// Default source for dev seeding. The generator stamps records with
    /// DataSources.DemoService, which DataSources.IsEphemeral hides from every
    /// non-demo tenant's reads — seeded data must carry a non-ephemeral source
    /// to be visible. The demo tenant passes DataSources.DemoService instead.
    /// </summary>
    public const string DevSampleDataSource = "dev-sample";

    public SampleDataSeeder(
        ITenantAccessor tenantAccessor,
        NocturneDbContext db,
        IEntryService entryService,
        ITreatmentService treatmentService,
        ISleepService sleepService,
        IHeartRateService heartRateService,
        IStepCountService stepCountService,
        ITrackerRepository trackerRepository,
        IRuleScopeClassifier scopeClassifier,
        ILoggerFactory loggerFactory,
        ILogger<SampleDataSeeder> logger)
    {
        _tenantAccessor = tenantAccessor;
        _db = db;
        _entryService = entryService;
        _treatmentService = treatmentService;
        _sleepService = sleepService;
        _heartRateService = heartRateService;
        _stepCountService = stepCountService;
        _trackerRepository = trackerRepository;
        _scopeClassifier = scopeClassifier;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Generates and persists <paramref name="days"/> days of sample data.
    /// <paramref name="ownerSubjectId"/> owns the seeded tracker definitions
    /// and instances; when null, trackers are skipped (they are per-user).
    /// With <paramref name="includeGlucose"/> false, entries/treatments are
    /// assumed already present (the demo container streams them itself) and
    /// alarm history derives from the stored glucose instead of the generated
    /// stream. Re-seeding is idempotent for every type except
    /// entries/treatments, which append.
    /// </summary>
    public async Task<SampleDataSeedResult> SeedAsync(
        TenantContext tenant,
        int days,
        Guid? ownerSubjectId,
        string dataSource = DevSampleDataSource,
        bool includeGlucose = true,
        CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, MaxDays);

        // Ingestion services resolve the tenant through ITenantAccessor for
        // factory-created contexts, and through the request-scoped context's
        // TenantId (normally pinned by tenant resolution middleware, which
        // dev-only and demo-admin routes bypass) for entity stamping and the
        // RLS GUC.
        _tenantAccessor.SetTenant(tenant);
        _db.TenantId = tenant.TenantId;

        // The episode tracker rides along with the glucose stream so alarm
        // history matches the lows/highs actually visible on the chart.
        var episodeTracker = new DemoAlertSeeds.GlucoseEpisodeTracker(DemoAlertSeeds.Defaults);

        var entryCount = 0;
        var treatmentCount = 0;
        if (includeGlucose)
        {
            var config = new DemoModeConfiguration { BackfillDays = days };
            var generator = new DemoDataGenerator(
                Options.Create(config),
                _loggerFactory.CreateLogger<DemoDataGenerator>(),
                _loggerFactory);

            var entries = generator.GenerateHistoricalEntries()
                .Select(e =>
                {
                    e.DataSource = dataSource;
                    episodeTracker.Observe(
                        DateTimeOffset.FromUnixTimeMilliseconds(e.Mills).UtcDateTime, e.Sgv);
                    return e;
                });

            foreach (var batch in entries.Chunk(BatchSize))
            {
                await _entryService.CreateEntriesAsync(batch, ct);
                entryCount += batch.Length;
            }

            // "Scheduled Basal" is a demo-service event type the treatment
            // decomposer doesn't recognize — it decomposes to nothing and logs
            // a warning per record.
            var treatments = generator.GenerateHistoricalTreatments()
                .Where(t => t.EventType != "Scheduled Basal")
                .Select(t =>
                {
                    t.DataSource = dataSource;
                    return t;
                });

            foreach (var batch in treatments.Chunk(BatchSize))
            {
                await _treatmentService.CreateTreatmentsAsync(batch, ct);
                treatmentCount += batch.Length;
            }
        }
        else
        {
            // Replay the stored stream (the demo container posted it over v1)
            // so seeded alarm history matches the chart exactly.
            var since = DateTime.UtcNow.AddDays(-days);
            var stored = _db.SensorGlucose
                .AsNoTracking()
                .Where(e => e.Timestamp >= since && e.DataSource == dataSource)
                .OrderBy(e => e.Timestamp)
                .Select(e => new { e.Timestamp, e.Mgdl })
                .AsAsyncEnumerable();
            await foreach (var reading in stored.WithCancellation(ct))
                episodeTracker.Observe(reading.Timestamp, reading.Mgdl);
        }
        episodeTracker.Flush();

        var localToday = DateTime.Now.Date;

        // Device changes as treatments: the decomposer turns them into
        // DeviceEvents, which drive the dashboard age pills (SAGE/CAGE/…).
        // The schedule is deterministic, so filter out changes whose
        // DeviceEvent already exists — a re-seed would otherwise insert exact
        // duplicates (treatments have no sync key).
        var deviceSchedule = DemoDeviceLifecycle.GenerateSchedule(localToday, days);
        var existingDeviceEventTimes = (await _db.DeviceEvents
                .AsNoTracking()
                .Where(d => d.DataSource == dataSource)
                .Select(d => d.Timestamp)
                .ToListAsync(ct))
            .ToHashSet();
        var deviceTreatments = deviceSchedule
            .Where(e => !existingDeviceEventTimes.Contains(e.TimestampUtc))
            .Select(e => DemoDeviceLifecycle.ToTreatment(e, dataSource))
            .ToList();
        if (deviceTreatments.Count > 0)
            await _treatmentService.CreateTreatmentsAsync(deviceTreatments, ct);

        var sleepCount = await SeedSleepAsync(localToday, days, dataSource, ct);
        var (heartRateCount, stepCount) = await SeedActivityAsync(localToday, days, dataSource, ct);
        var (trackerDefinitions, trackerInstances) =
            await SeedTrackersAsync(deviceSchedule, ownerSubjectId, ct);
        var (alertRules, alertExcursions) =
            await SeedAlertsAsync(tenant.TenantId, episodeTracker.Episodes, ct);

        _logger.LogInformation(
            "Seeded tenant {Slug} ({Days} days): {Entries} entries, {Treatments} treatments, "
            + "{DeviceChanges} device changes, {Sleep} sleep sessions, {HeartRates} heart rates, "
            + "{Steps} step buckets, {TrackerDefs} tracker definitions, {TrackerInstances} tracker instances, "
            + "{AlertRules} alert rules, {Excursions} alarm excursions",
            tenant.Slug, days, entryCount, treatmentCount, deviceTreatments.Count, sleepCount,
            heartRateCount, stepCount, trackerDefinitions, trackerInstances, alertRules, alertExcursions);

        return new SampleDataSeedResult(
            entryCount, treatmentCount, sleepCount, heartRateCount, stepCount,
            deviceTreatments.Count, trackerDefinitions, trackerInstances,
            alertRules, alertExcursions);
    }

    /// <summary>
    /// One overnight sleep session per night, generated by the shared
    /// scenario-aware generator and written through <see cref="ISleepService"/>
    /// so RLS, entity mapping, and (Source, OriginalId) dedup on re-seed are
    /// handled like a connector import. Anchored to local midnight so bedtime
    /// lands at night in the viewer's timezone (dev runs the API and browser on
    /// the same machine); stored as UTC.
    /// </summary>
    private async Task<int> SeedSleepAsync(
        DateTime localToday, int days, string sourceApp, CancellationToken ct)
    {
        var count = 0;
        for (var d = 1; d <= days; d++)
        {
            // Night ending on the morning of (today - d + 1).
            var localMorning = localToday.AddDays(-(d - 1));
            var session = DemoHealthDataGenerator.GenerateSleepSession(localMorning, sourceApp);

            // Seeding before ~08:00 local would otherwise write last night's
            // session with an EndTime still in the future.
            if (session.EndTime > DateTime.UtcNow)
                continue;

            await _sleepService.UpsertSessionAsync(session, ct);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Heart rate (5-minute cadence) and hourly step buckets per day, clamped
    /// to the present so today's partial day carries no future samples.
    /// Deterministic sync identifiers make re-seeding update in place.
    /// </summary>
    private async Task<(int HeartRates, int Steps)> SeedActivityAsync(
        DateTime localToday, int days, string dataSource, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var heartRates = new List<HeartRate>();
        var stepCounts = new List<StepCount>();

        for (var d = 0; d < days; d++)
        {
            var (dayHeartRates, daySteps) =
                DemoHealthDataGenerator.GenerateDailyActivity(localToday.AddDays(-d), dataSource);
            heartRates.AddRange(dayHeartRates.Where(h => h.Timestamp <= now));
            stepCounts.AddRange(daySteps.Where(s => s.Timestamp <= now));
        }

        foreach (var batch in heartRates.Chunk(BatchSize))
            await _heartRateService.CreateHeartRatesAsync(batch, ct);
        foreach (var batch in stepCounts.Chunk(BatchSize))
            await _stepCountService.CreateStepCountsAsync(batch, ct);

        return (heartRates.Count, stepCounts.Count);
    }

    /// <summary>
    /// Consumable tracker definitions (sensor, site, reservoir, battery) with
    /// instances aligned to the device-change schedule: each change starts an
    /// instance and completes the previous one, so the newest instance is
    /// running with a realistic age. Definitions are found-or-created by name;
    /// instances are wiped and rebuilt so re-seeding stays consistent with the
    /// regenerated schedule.
    /// </summary>
    private async Task<(int Definitions, int Instances)> SeedTrackersAsync(
        List<DeviceChangeEvent> schedule, Guid? ownerSubjectId, CancellationToken ct)
    {
        if (ownerSubjectId is not { } owner)
            return (0, 0);

        var userId = owner.ToString();
        var existing = await _trackerRepository.GetDefinitionsForUserAsync(userId, ct);

        var definitionsCreated = 0;
        var instancesCreated = 0;

        foreach (var spec in DemoDeviceLifecycle.TrackerSpecs)
        {
            var definition = existing.FirstOrDefault(d =>
                string.Equals(d.Name, spec.Name, StringComparison.OrdinalIgnoreCase));

            if (definition is null)
            {
                definition = await _trackerRepository.CreateDefinitionAsync(new TrackerDefinitionEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = _db.TenantId,
                    UserId = userId,
                    Name = spec.Name,
                    Category = spec.Category,
                    Icon = spec.Icon,
                    LifespanHours = spec.LifespanHours,
                    TriggerEventTypes = JsonSerializer.Serialize(new[] { spec.TriggerEventType }),
                    StartEventType = spec.TriggerEventType,
                    Mode = TrackerMode.Duration,
                }, ct);
                definitionsCreated++;
            }

            // Rebuild instances from the schedule (idempotent re-seed).
            await _db.TrackerInstances
                .Where(i => i.DefinitionId == definition.Id)
                .ExecuteDeleteAsync(ct);

            var changes = schedule
                .Where(e => e.EventType == spec.TriggerEventType)
                .OrderBy(e => e.TimestampUtc)
                .ToList();

            Guid? previousInstanceId = null;
            foreach (var change in changes)
            {
                if (previousInstanceId is { } previous)
                {
                    await _trackerRepository.CompleteInstanceAsync(
                        previous, spec.CompletionReason,
                        completedAt: change.TimestampUtc, cancellationToken: ct);
                }

                var instance = await _trackerRepository.StartInstanceAsync(
                    definition.Id, userId,
                    startedAt: change.TimestampUtc, cancellationToken: ct);
                previousInstanceId = instance.Id;
                instancesCreated++;
            }
        }

        return (definitionsCreated, instancesCreated);
    }

    /// <summary>
    /// A standard alert rule set (urgent low / low / high / signal loss, each
    /// with an in-app channel) plus historical excursions and resolved alert
    /// instances taken from the threshold crossings of the generated glucose
    /// stream — so /alerts/history lines up with the chart. Rules are
    /// found-or-created by name; their alarm history is wiped and rebuilt.
    /// </summary>
    private async Task<(int Rules, int Excursions)> SeedAlertsAsync(
        Guid tenantId,
        IReadOnlyList<DemoAlertSeeds.GlucoseEpisode> episodes,
        CancellationToken ct)
    {
        var rulesByName = new Dictionary<string, AlertRuleEntity>(StringComparer.OrdinalIgnoreCase);
        var rulesCreated = 0;

        foreach (var seed in DemoAlertSeeds.Defaults)
        {
            var rule = await _db.AlertRules
                .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == seed.Name, ct);

            if (rule is null)
            {
                rule = new AlertRuleEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    Name = seed.Name,
                    Description = seed.Description,
                    ConditionType = seed.ConditionType,
                    ConditionParams = seed.ConditionParamsJson,
                    ScopeClass = _scopeClassifier.Classify(seed.ConditionType, seed.ConditionParamsJson),
                    Severity = seed.Severity,
                    IsEnabled = true,
                };
                rule.Channels.Add(new AlertRuleChannelEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    AlertRuleId = rule.Id,
                    ChannelType = ChannelType.InApp,
                    SortOrder = 0,
                });
                _db.AlertRules.Add(rule);
                rulesCreated++;
            }

            rulesByName[seed.Name] = rule;
        }
        await _db.SaveChangesAsync(ct);

        // Rebuild alarm history for the seeded rules (idempotent re-seed).
        var seededRuleIds = rulesByName.Values.Select(r => r.Id).ToList();
        await _db.AlertInstances
            .Where(i => i.AlertExcursion != null && seededRuleIds.Contains(i.AlertExcursion.AlertRuleId))
            .ExecuteDeleteAsync(ct);
        await _db.AlertExcursions
            .Where(e => seededRuleIds.Contains(e.AlertRuleId))
            .ExecuteDeleteAsync(ct);

        var excursionsCreated = 0;
        foreach (var episode in episodes.OrderByDescending(e => e.StartUtc).Take(MaxAlarmEpisodes))
        {
            if (!rulesByName.TryGetValue(episode.RuleName, out var rule))
                continue;

            var rng = DayScenarios.RngFor(episode.StartUtc.Date, $"ack:{episode.RuleName}");
            var acknowledged = rng.NextDouble() < 0.7;
            var acknowledgedAt = acknowledged
                ? episode.StartUtc.AddMinutes(rng.Next(2, 12))
                : (DateTime?)null;

            var excursion = new AlertExcursionEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                AlertRuleId = rule.Id,
                StartedAt = episode.StartUtc,
                EndedAt = episode.EndUtc,
                AcknowledgedAt = acknowledgedAt,
                AcknowledgedBy = acknowledged ? "demo" : null,
            };
            excursion.Instances.Add(new AlertInstanceEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                AlertExcursionId = excursion.Id,
                Status = "resolved",
                TriggeredAt = episode.StartUtc,
                ResolvedAt = episode.EndUtc,
                // The engine's wire reason for a threshold clearing naturally.
                ResolutionReason = "hysteresis",
            });
            _db.AlertExcursions.Add(excursion);
            excursionsCreated++;
        }
        await _db.SaveChangesAsync(ct);

        return (rulesCreated, excursionsCreated);
    }
}
