using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
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
    int AlertExcursions,
    int DeviceStatuses,
    int Profiles,
    int Foods,
    int StateSpans,
    int Notifications);

/// <summary>
/// Populates a tenant with realistic sample data covering every data type the
/// platform can hold, driven by the demo service's unified oref simulation
/// timeline: CGM entries, fingersticks and calibrations, treatments (boluses,
/// carbs, temp basals, BG checks, notes), Trio-style device status (APS
/// snapshots with predictions, pump reservoir/battery, phone battery), the
/// therapy profile, device-change events, sleep, heart rate, steps, consumable
/// trackers, alert rules with alarm history and their in-app notifications,
/// state spans (pump mode, overrides, exercise, illness, travel), the food
/// library with per-meal attribution, patient record and devices, body weight,
/// the timezone timeline, a clock face, and a DND-window example. Everything
/// writes through the normal ingestion services and decomposers so device
/// attribution, the v4 canonical streams, and RLS tenant context are handled
/// exactly like production writes.
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
    private readonly IProfileWriteService _profileWriteService;
    private readonly IDeviceStatusDecomposer _deviceStatusDecomposer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SampleDataSeeder> _logger;

    private const int BatchSize = 500;
    private const int MaxDays = 90;

    /// <summary>Device status cadence: one document per 15 minutes of history.</summary>
    private const int DeviceStatusMinutes = 15;

    /// <summary>Most recent alarm episodes to materialize as excursions/instances.</summary>
    private const int MaxAlarmEpisodes = 40;

    /// <summary>Most recent alarm episodes to mirror as in-app notifications.</summary>
    private const int MaxAlarmNotifications = 6;

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
        IProfileWriteService profileWriteService,
        IDeviceStatusDecomposer deviceStatusDecomposer,
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
        _profileWriteService = profileWriteService;
        _deviceStatusDecomposer = deviceStatusDecomposer;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Generates and persists <paramref name="days"/> days of sample data.
    /// <paramref name="ownerSubjectId"/> owns the seeded tracker definitions,
    /// clock faces, food favorites, and notifications; when null, those
    /// per-user types are skipped. With <paramref name="includeGlucose"/>
    /// false, entries/treatments/device status are assumed already present and
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

        var config = new DemoModeConfiguration { BackfillDays = days };

        // The episode tracker rides along with the glucose stream so alarm
        // history matches the lows/highs actually visible on the chart.
        var episodeTracker = new DemoAlertSeeds.GlucoseEpisodeTracker(DemoAlertSeeds.Defaults);

        // Seed the therapy profile before glucose so scheduled-basal context
        // exists from the first record.
        var profilesCreated = await SeedProfileAsync(config, ct);

        var entryCount = 0;
        var treatmentCount = 0;
        var deviceStatusCount = 0;
        var mealCarbLinks = new List<(DateTime Time, string MealName)>();

        if (includeGlucose)
        {
            var generator = new DemoDataGenerator(
                Options.Create(config),
                _loggerFactory.CreateLogger<DemoDataGenerator>(),
                _loggerFactory);

            var entryBatch = new List<Entry>(BatchSize);
            var treatmentBatch = new List<Treatment>(BatchSize);
            var statusBatch = new List<DeviceStatus>(BatchSize);
            var requestedTreatments = 0;

            async Task FlushEntriesAsync()
            {
                if (entryBatch.Count == 0) return;
                // Backfill origin: a 90-day seed must not broadcast tens of
                // thousands of records over SignalR from one request.
                var created = await _entryService.CreateEntriesAsync(
                    entryBatch, WriteOrigin.Backfill, ct);
                entryCount += created.Count();
                entryBatch.Clear();
            }

            async Task FlushTreatmentsAsync()
            {
                if (treatmentBatch.Count == 0) return;
                requestedTreatments += treatmentBatch.Count;
                var created = await _treatmentService.CreateTreatmentsAsync(treatmentBatch, ct);
                treatmentCount += created.Count();
                treatmentBatch.Clear();
            }

            async Task FlushStatusesAsync()
            {
                foreach (var status in statusBatch)
                {
                    await _deviceStatusDecomposer.DecomposeAsync(
                        status, dataSource, WriteOrigin.Backfill, ct);
                    deviceStatusCount++;
                }
                statusBatch.Clear();
            }

            foreach (var step in generator.GenerateHistoricalTimeline())
            {
                ct.ThrowIfCancellationRequested();

                step.Entry.DataSource = dataSource;
                episodeTracker.Observe(
                    DateTimeOffset.FromUnixTimeMilliseconds(step.Entry.Mills).UtcDateTime, step.Entry.Sgv);
                entryBatch.Add(step.Entry);
                foreach (var extra in step.ExtraEntries)
                {
                    extra.DataSource = dataSource;
                    entryBatch.Add(extra);
                }

                foreach (var treatment in step.Treatments)
                {
                    treatment.DataSource = dataSource;
                    treatmentBatch.Add(treatment);
                    if (treatment.EventType == "Carbs" && treatment.FoodType is { Length: > 0 })
                        mealCarbLinks.Add((DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime, treatment.FoodType));
                }

                if (step.Time.Minute % DeviceStatusMinutes == 0)
                {
                    var status = DemoDeviceStatusGenerator.Create(
                        step.Time,
                        step.Entry.Sgv ?? step.Entry.Mgdl,
                        step.Iob,
                        step.Cob,
                        step.TempBasalRate,
                        step.TempBasalDuration,
                        step.EffectiveIsf,
                        step.EffectiveCarbRatio,
                        config.TargetGlucose,
                        DemoTherapyProfile.ScheduledRateAt(step.Time, config.BasalRate),
                        step.Scenario);
                    // Deterministic legacy id so re-seeding updates in place.
                    status.Id = status.Mills.ToString("x24");
                    statusBatch.Add(status);
                }

                if (entryBatch.Count >= BatchSize) await FlushEntriesAsync();
                if (treatmentBatch.Count >= BatchSize) await FlushTreatmentsAsync();
                if (statusBatch.Count >= BatchSize) await FlushStatusesAsync();
            }

            await FlushEntriesAsync();
            await FlushTreatmentsAsync();
            await FlushStatusesAsync();

            // CreateTreatmentsAsync decomposes each treatment into its v4 canonical
            // records and swallows per-record decomposition failures, returning only
            // the ones that persisted. A shortfall means treatments threw and were
            // dropped, leaving a tenant that looks seeded but carries no bolus/carb/
            // basal history. Fail loudly rather than report a hollow success — the
            // demo generator's shapes all decompose cleanly, so any drop is a real
            // fault (e.g. a poisoned DB connection), not expected data.
            if (treatmentCount < requestedTreatments)
            {
                throw new InvalidOperationException(
                    $"Sample-data seeding persisted only {treatmentCount} of {requestedTreatments} "
                    + $"treatments; {requestedTreatments - treatmentCount} were dropped during "
                    + "decomposition (see preceding 'Failed to decompose treatment' errors).");
            }
        }
        else
        {
            // Replay the stored stream so seeded alarm history matches the
            // chart exactly.
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

        var foodCount = await SeedFoodsAsync(mealCarbLinks, ownerSubjectId, ct);
        var stateSpanCount = await SeedStateSpansAsync(localToday, days, dataSource, ct);
        await SeedPatientProfileAsync(ct);
        await SeedBodyWeightAsync(localToday, days, dataSource, ct);
        await SeedTimezoneTimelineAsync(localToday, days, ct);
        await SeedDndWindowAsync(localToday, ct);
        var clockFaces = await SeedClockFacesAsync(ownerSubjectId, ct);
        var notificationCount = await SeedNotificationsAsync(
            ownerSubjectId, tenant.TenantId, episodeTracker.Episodes, ct);

        _logger.LogInformation(
            "Seeded tenant {Slug} ({Days} days): {Entries} entries, {Treatments} treatments, "
            + "{DeviceStatuses} device statuses, {Profiles} profiles, {DeviceChanges} device changes, "
            + "{Sleep} sleep sessions, {HeartRates} heart rates, {Steps} step buckets, "
            + "{TrackerDefs} tracker definitions, {TrackerInstances} tracker instances, "
            + "{AlertRules} alert rules, {Excursions} alarm excursions, {Foods} foods, "
            + "{StateSpans} state spans, {ClockFaces} clock faces, {Notifications} notifications",
            tenant.Slug, days, entryCount, treatmentCount, deviceStatusCount, profilesCreated,
            deviceTreatments.Count, sleepCount, heartRateCount, stepCount, trackerDefinitions,
            trackerInstances, alertRules, alertExcursions, foodCount, stateSpanCount, clockFaces,
            notificationCount);

        return new SampleDataSeedResult(
            entryCount, treatmentCount, sleepCount, heartRateCount, stepCount,
            deviceTreatments.Count, trackerDefinitions, trackerInstances,
            alertRules, alertExcursions, deviceStatusCount, profilesCreated,
            foodCount, stateSpanCount, notificationCount);
    }

    /// <summary>
    /// Seeds the Nightscout profile document through the profile write service
    /// (which decomposes it into TherapySettings plus the basal/carb-ratio/
    /// sensitivity/target schedules), unless the demo profile already exists.
    /// </summary>
    private async Task<int> SeedProfileAsync(DemoModeConfiguration config, CancellationToken ct)
    {
        var exists = await _db.TherapySettings
            .AnyAsync(t => t.ProfileName == DemoTherapyProfile.ProfileName, ct);
        if (exists)
            return 0;

        await _profileWriteService.CreateProfilesAsync(
            [DemoTherapyProfile.BuildProfile(config, DateTime.UtcNow)], ct);
        return 1;
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
            await _db.TrackerInstances.PurgeAsync(i => i.DefinitionId == definition.Id, ct);

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
        await _db.AlertInstances.PurgeAsync(
            i => i.AlertExcursion != null && seededRuleIds.Contains(i.AlertExcursion.AlertRuleId), ct);
        await _db.AlertExcursions.PurgeAsync(e => seededRuleIds.Contains(e.AlertRuleId), ct);

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

    /// <summary>
    /// The food library (found-or-created by name), favorites for the owner,
    /// and per-meal food attribution lines linking library foods to the carb
    /// intakes the generated meals decomposed into. Attribution is rebuilt from
    /// the meal timestamps, so re-seeding stays consistent.
    /// </summary>
    private async Task<int> SeedFoodsAsync(
        List<(DateTime Time, string MealName)> mealCarbLinks, Guid? ownerSubjectId, CancellationToken ct)
    {
        var foodsByName = new Dictionary<string, FoodEntity>(StringComparer.OrdinalIgnoreCase);
        var created = 0;

        var position = 0;
        foreach (var seed in DemoLifestyleSeeds.FoodLibrary)
        {
            position++;
            var food = await _db.Foods.FirstOrDefaultAsync(f => f.Name == seed.Name, ct);
            if (food is null)
            {
                food = new FoodEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = _db.TenantId,
                    Name = seed.Name,
                    Category = seed.Category,
                    Subcategory = seed.Subcategory,
                    Portion = seed.Portion,
                    Unit = seed.Unit,
                    Carbs = seed.Carbs,
                    Protein = seed.Protein,
                    Fat = seed.Fat,
                    Energy = seed.Energy,
                    Position = position,
                };
                _db.Foods.Add(food);
                created++;
            }

            foodsByName[seed.Name] = food;
        }
        await _db.SaveChangesAsync(ct);

        // Favorites: the snacks and one dinner, owned by the demo member.
        if (ownerSubjectId is { } owner)
        {
            var userId = owner.ToString();
            var favoriteNames = new[] { "Jelly beans", "Muesli bar", "Homemade pizza slices" };
            foreach (var name in favoriteNames)
            {
                if (!foodsByName.TryGetValue(name, out var food))
                    continue;
                var exists = await _db.UserFoodFavorites
                    .AnyAsync(f => f.UserId == userId && f.FoodId == food.Id, ct);
                if (!exists)
                {
                    _db.UserFoodFavorites.Add(new UserFoodFavoriteEntity
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = _db.TenantId,
                        UserId = userId,
                        FoodId = food.Id,
                    });
                }
            }
            await _db.SaveChangesAsync(ct);
        }

        // Food attribution on the meals' carb intakes (~60% of meals, as a
        // realistic user would log). Meal timestamps identify the intakes.
        if (mealCarbLinks.Count > 0)
        {
            var mealTimes = mealCarbLinks.Select(m => m.Time).ToList();
            var intakes = await _db.CarbIntakes
                .Where(c => mealTimes.Contains(c.Timestamp))
                .Select(c => new { c.Id, c.Timestamp, c.Carbs })
                .ToListAsync(ct);
            var linkedIntakeIds = intakes.Select(i => i.Id).ToList();
            await _db.TreatmentFoods.PurgeAsync(tf => linkedIntakeIds.Contains(tf.CarbIntakeId), ct);

            foreach (var (time, mealName) in mealCarbLinks)
            {
                // Deterministic rolls key on the meal's local calendar day,
                // like every other demo stream (the UTC day differs east of
                // Greenwich).
                var localMeal = time.ToLocalTime();
                if (DayScenarios.Roll(localMeal.Date, $"food-link:{localMeal.Hour}", 100) >= 60)
                    continue;

                var intake = intakes.FirstOrDefault(i => i.Timestamp == time);
                if (intake is null)
                    continue;

                foreach (var seed in DemoLifestyleSeeds.MealFoodsFor(localMeal.Date, mealName))
                {
                    if (!foodsByName.TryGetValue(seed.Name, out var food))
                        continue;
                    _db.TreatmentFoods.Add(new TreatmentFoodEntity
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = _db.TenantId,
                        CarbIntakeId = intake.Id,
                        FoodId = food.Id,
                        Portions = seed.Carbs > 0 ? Math.Round((decimal)(intake.Carbs / seed.Carbs), 2) : 1,
                        Carbs = (decimal)intake.Carbs,
                    });
                }
            }
            await _db.SaveChangesAsync(ct);
        }

        return created;
    }

    /// <summary>
    /// State spans for the window: pump mode with manual/exercise windows, the
    /// active profile, workout overrides and temporary targets, illness runs,
    /// and the travel span. Spans carrying our data source are wiped and
    /// rebuilt (idempotent re-seed).
    /// </summary>
    private async Task<int> SeedStateSpansAsync(
        DateTime localToday, int days, string dataSource, CancellationToken ct)
    {
        await _db.StateSpans.PurgeAsync(s => s.Source == dataSource, ct);

        var spans = DemoLifestyleSeeds.BuildSpans(localToday, days);
        foreach (var seed in spans)
        {
            _db.StateSpans.Add(new StateSpanEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = _db.TenantId,
                Category = seed.Category.ToString(),
                State = seed.State,
                StartTimestamp = seed.StartLocal.ToUniversalTime(),
                EndTimestamp = seed.EndLocal?.ToUniversalTime(),
                Source = dataSource,
                MetadataJson = seed.Metadata is null ? null : JsonSerializer.Serialize(seed.Metadata),
            });
        }
        await _db.SaveChangesAsync(ct);

        return spans.Count;
    }

    /// <summary>
    /// The patient record singleton, the device roster (CGM, pod, meter), and
    /// the current insulin — the /settings/patient page and device attribution
    /// context. Created only when absent.
    /// </summary>
    private async Task SeedPatientProfileAsync(CancellationToken ct)
    {
        if (!await _db.PatientRecords.AnyAsync(ct))
        {
            _db.PatientRecords.Add(new PatientRecordEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = _db.TenantId,
                PreferredName = "Demo",
                DiabetesType = "type1",
                DiagnosisDate = new DateOnly(2014, 3, 12),
                DateOfBirth = new DateOnly(1992, 4, 17),
                Timezone = DemoTherapyProfile.LocalIanaTimezone(),
            });
        }

        var deviceSeeds = new (string Category, string Manufacturer, string Model, string? Aid)[]
        {
            ("cgm", "Dexcom", "G7", null),
            ("pump", "Insulet", "Omnipod DASH", DemoDeviceStatusGenerator.DeviceName),
            ("meter", "Ascensia", "Contour Next One", null),
        };
        foreach (var (category, manufacturer, model, aid) in deviceSeeds)
        {
            var exists = await _db.PatientDevices
                .AnyAsync(d => d.Manufacturer == manufacturer && d.Model == model, ct);
            if (!exists)
            {
                _db.PatientDevices.Add(new PatientDeviceEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = _db.TenantId,
                    DeviceCategory = category,
                    Manufacturer = manufacturer,
                    Model = model,
                    AidAlgorithm = aid,
                    StartDate = DateOnly.FromDateTime(DateTime.Today.AddMonths(-8)),
                    IsCurrent = true,
                });
            }
        }

        if (!await _db.PatientInsulins.AnyAsync(ct))
        {
            _db.PatientInsulins.Add(new PatientInsulinEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = _db.TenantId,
                InsulinCategory = "RapidActing",
                Name = "Humalog",
                IsCurrent = true,
                Dia = 4.0,
                Peak = 75,
                Curve = "rapid-acting",
                Concentration = 100,
                Role = "Bolus",
                IsPrimary = true,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Weekly Sunday-morning weigh-ins with a deterministic sync key.</summary>
    private async Task SeedBodyWeightAsync(
        DateTime localToday, int days, string dataSource, CancellationToken ct)
    {
        var existing = (await _db.BodyWeights
                .AsNoTracking()
                .Where(w => w.DataSource == dataSource)
                .Select(w => w.SyncIdentifier)
                .ToListAsync(ct))
            .ToHashSet();

        for (var d = 0; d <= days; d++)
        {
            var day = localToday.AddDays(-d);
            if (day.DayOfWeek != DayOfWeek.Sunday)
                continue;

            var syncId = $"demo-weight-{day:yyyy-MM-dd}";
            if (existing.Contains(syncId))
                continue;

            var weighIn = day.AddHours(7).AddMinutes(DayScenarios.Roll(day, "weigh-in", 40));
            if (weighIn > DateTime.Now)
                continue;

            _db.BodyWeights.Add(new BodyWeightEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = _db.TenantId,
                Mills = new DateTimeOffset(weighIn).ToUnixTimeMilliseconds(),
                WeightKg = (decimal)DemoLifestyleSeeds.WeightKgOn(day),
                Device = "Demo Scale",
                DataSource = dataSource,
                SyncIdentifier = syncId,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The timezone timeline: home zone from the window start, with a
    /// five-day trip (matching the Travel state span) three weeks back.
    /// EffectiveFrom is a local wall-clock value (Kind=Unspecified), matching
    /// <c>TimezoneTimelineService</c>. Created only when the timeline is empty.
    /// </summary>
    private async Task SeedTimezoneTimelineAsync(DateTime localToday, int days, CancellationToken ct)
    {
        if (await _db.TimezoneTimeline.AnyAsync(ct))
            return;

        var home = DemoTherapyProfile.LocalIanaTimezone();
        var windowStart = localToday.AddDays(-days);
        var tripStart = localToday.AddDays(-DemoLifestyleSeeds.TripStartDaysAgo).AddHours(14);
        var tripEnd = tripStart.AddDays(DemoLifestyleSeeds.TripLengthDays).AddHours(-4);

        _db.TimezoneTimeline.Add(new TimezoneTimelineEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _db.TenantId,
            EffectiveFrom = DateTime.SpecifyKind(windowStart, DateTimeKind.Unspecified),
            Timezone = home,
        });

        if (tripStart > windowStart && tripEnd < DateTime.Now)
        {
            _db.TimezoneTimeline.Add(new TimezoneTimelineEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = _db.TenantId,
                EffectiveFrom = DateTime.SpecifyKind(tripStart, DateTimeKind.Unspecified),
                Timezone = DemoLifestyleSeeds.TripTimezone,
            });
            _db.TimezoneTimeline.Add(new TimezoneTimelineEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = _db.TenantId,
                EffectiveFrom = DateTime.SpecifyKind(tripEnd, DateTimeKind.Unspecified),
                Timezone = home,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One historical, already-cleared DND window so the alerts pages show a
    /// worked example without suppressing the demo's live alerts.
    /// </summary>
    private async Task SeedDndWindowAsync(DateTime localToday, CancellationToken ct)
    {
        if (await _db.DndWindows.AnyAsync(ct))
            return;

        var start = localToday.AddDays(-1).AddHours(22.5);
        _db.DndWindows.Add(new DndWindowEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _db.TenantId,
            Scope = DndScope.All,
            StartedAt = start.ToUniversalTime(),
            EndsAt = start.AddHours(8.5).ToUniversalTime(),
            ClearedAt = start.AddHours(8.2).ToUniversalTime(),
            ClearedBy = "demo",
            Source = "web",
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The default clock face for the owner, found-or-created by name.</summary>
    private async Task<int> SeedClockFacesAsync(Guid? ownerSubjectId, CancellationToken ct)
    {
        if (ownerSubjectId is not { } owner)
            return 0;

        var userId = owner.ToString();
        var exists = await _db.ClockFaces.AnyAsync(c => c.UserId == userId, ct);
        if (exists)
            return 0;

        _db.ClockFaces.Add(new ClockFaceEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _db.TenantId,
            UserId = userId,
            Name = "Bedside Clock",
            ConfigJson = DemoLifestyleSeeds.DefaultClockFaceConfigJson,
        });
        await _db.SaveChangesAsync(ct);
        return 1;
    }

    /// <summary>
    /// In-app notifications mirroring the most recent seeded alarm episodes
    /// (read where the excursion was acknowledged), so /notifications shows the
    /// same story as /alerts/history. Rebuilt per seed via the data source tag.
    /// </summary>
    private async Task<int> SeedNotificationsAsync(
        Guid? ownerSubjectId,
        Guid tenantId,
        IReadOnlyList<DemoAlertSeeds.GlucoseEpisode> episodes,
        CancellationToken ct)
    {
        if (ownerSubjectId is not { } owner)
            return 0;

        var userId = owner.ToString();
        await _db.InAppNotifications.PurgeAsync(n => n.UserId == userId && n.Source == "sample-seed", ct);

        var created = 0;
        foreach (var episode in episodes.OrderByDescending(e => e.StartUtc).Take(MaxAlarmNotifications))
        {
            var rng = DayScenarios.RngFor(episode.StartUtc.Date, $"ack:{episode.RuleName}");
            var acknowledged = rng.NextDouble() < 0.7;

            _db.InAppNotifications.Add(new InAppNotificationEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                UserId = userId,
                Type = "alert.firing",
                Category = NotificationCategory.Alert,
                Urgency = episode.RuleName.Contains("Urgent", StringComparison.OrdinalIgnoreCase)
                    ? NotificationUrgency.Urgent
                    : NotificationUrgency.Warn,
                Icon = "bell",
                Source = "sample-seed",
                Title = episode.RuleName,
                Subtitle = $"Resolved after {(episode.EndUtc - episode.StartUtc).TotalMinutes:0} minutes",
                CreatedAt = episode.StartUtc,
                ReadAt = acknowledged ? episode.StartUtc.AddMinutes(rng.Next(2, 12)) : null,
            });
            created++;
        }

        await _db.SaveChangesAsync(ct);
        return created;
    }
}
