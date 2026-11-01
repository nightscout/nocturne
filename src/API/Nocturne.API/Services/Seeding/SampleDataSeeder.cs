using Nocturne.API.Multitenancy;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Entries;

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
    private const string AuditEndpoint = "service:sample-data-seeder";

    private readonly ITenantAccessor _tenantAccessor;
    private readonly INocturneDbContextFactory _contexts;
    private readonly ISubjectRepository _subjects;
    private readonly IEntryService _entryService;
    private readonly ITreatmentService _treatmentService;
    private readonly ISleepService _sleepService;
    private readonly IHeartRateService _heartRateService;
    private readonly IStepCountService _stepCountService;
    private readonly ITrackerRepository _trackerRepository;
    private readonly IAlertRuleRepository _alertRules;
    private readonly IRuleScopeClassifier _scopeClassifier;
    private readonly IProfileWriteService _profileWriteService;
    private readonly IDeviceStatusService _deviceStatuses;
    private readonly ICompressionLowDetectionService _compressionLowDetectionService;
    private readonly ICompressionLowService _compressionLowService;
    private readonly ICompressionLowRepository _compressionLowRepository;
    private readonly ITreatmentFoodRepository _treatmentFoods;
    private readonly IStateSpanRepository _stateSpans;
    private readonly IProfileActivationRepository _profileActivations;
    private readonly ITempBasalRepository _tempBasals;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SampleDataSeeder> _logger;

    private const int BatchSize = 500;
    private const int MaxDays = 90;

    /// <summary>A fixed seed, so seeding the same range again writes the same history.</summary>
    private const int RandomSeed = 1;

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
        INocturneDbContextFactory contexts,
        ISubjectRepository subjects,
        IEntryService entryService,
        ITreatmentService treatmentService,
        ISleepService sleepService,
        IHeartRateService heartRateService,
        IStepCountService stepCountService,
        ITrackerRepository trackerRepository,
        IAlertRuleRepository alertRules,
        IRuleScopeClassifier scopeClassifier,
        IProfileWriteService profileWriteService,
        IDeviceStatusService deviceStatuses,
        ICompressionLowDetectionService compressionLowDetectionService,
        ICompressionLowService compressionLowService,
        ICompressionLowRepository compressionLowRepository,
        ITreatmentFoodRepository treatmentFoods,
        IStateSpanRepository stateSpans,
        IProfileActivationRepository profileActivations,
        ITempBasalRepository tempBasals,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        ILogger<SampleDataSeeder> logger)
    {
        _tenantAccessor = tenantAccessor;
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
        _entryService = entryService;
        _treatmentService = treatmentService;
        _sleepService = sleepService;
        _heartRateService = heartRateService;
        _stepCountService = stepCountService;
        _trackerRepository = trackerRepository;
        _alertRules = alertRules;
        _scopeClassifier = scopeClassifier;
        _profileWriteService = profileWriteService;
        _deviceStatuses = deviceStatuses;
        _compressionLowDetectionService = compressionLowDetectionService;
        _compressionLowService = compressionLowService;
        _compressionLowRepository = compressionLowRepository;
        _treatmentFoods = treatmentFoods;
        _stateSpans = stateSpans;
        _profileActivations = profileActivations;
        _tempBasals = tempBasals;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Runs <see cref="SeedAsync"/> in a DI scope of <paramref name="tenant"/>'s own, so the calling
    /// request's tenant is left as it was. Dev-only and demo-admin routes bypass tenant resolution,
    /// so the caller's scope has no tenant of its own to seed under.
    /// </summary>
    public static async Task<SampleDataSeedResult> SeedInTenantScopeAsync(
        IServiceProvider services,
        TenantContext tenant,
        int days,
        Guid? ownerSubjectId,
        string dataSource = DevSampleDataSource,
        bool includeGlucose = true,
        CancellationToken ct = default)
    {
        await using var scope = TenantJobScope.Open(services, tenant, AuditEndpoint);
        return await scope.ServiceProvider.GetRequiredService<SampleDataSeeder>()
            .SeedAsync(tenant, days, ownerSubjectId, dataSource, includeGlucose, ct);
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
        await using var db = await _contexts.CreateAsync(ct);
        days = Math.Clamp(days, 1, MaxDays);

        if (_tenantAccessor.TenantId != tenant.TenantId)
            throw new InvalidOperationException(
                $"Seed {tenant.Slug} from a scope of its own; see {nameof(SeedInTenantScopeAsync)}.");

        var config = ConfigurationFor(days);
        var clock = SeedingClock.Resolve(
            await db.PatientRecords.AsNoTracking().Select(r => r.Timezone).FirstOrDefaultAsync(ct), _time);
        // One end for every generated stream, so the loop and the pump mode spans agree on the quiet
        // end (DemoLoopInterruptions.QuietBeforeEnd).
        var seedEnd = clock.UtcNow;

        // The episode tracker rides along with the glucose stream so alarm
        // history matches the lows/highs actually visible on the chart.
        var episodeTracker = new DemoAlertSeeds.GlucoseEpisodeTracker(DemoAlertSeeds.Defaults);

        await SeedPatientProfileAsync(db, clock, ct);

        // Seed the therapy profile before glucose so scheduled-basal context
        // exists from the first record.
        var profilesCreated = await SeedProfileAsync(db, config, clock, ct);

        var entryCount = 0;
        var treatmentCount = 0;
        var deviceStatusCount = 0;
        var mealCarbLinks = new List<(DateTime Time, string MealName)>();

        if (includeGlucose)
        {
            var generator = new DemoDataGenerator(
                Options.Create(config),
                clock.Demo,
                _loggerFactory.CreateLogger<DemoDataGenerator>(),
                _loggerFactory);

            var entryBatch = new List<Entry>(BatchSize);
            var treatmentBatch = new List<Treatment>(BatchSize);
            var statusBatch = new List<DeviceStatus>(BatchSize);

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
                var decomposed = await _treatmentService.ImportTreatmentsAsync(treatmentBatch, ct);
                // Every shape the demo generator emits is one Nocturne stores, so a skip is a
                // generator/decomposer mismatch that would leave the tenant without that history.
                if (decomposed.SkippedUnsupported > 0)
                {
                    throw new InvalidOperationException(
                        $"Sample-data seeding skipped {decomposed.SkippedUnsupported} generated "
                        + "treatments whose event type the decomposer does not store.");
                }
                treatmentCount += treatmentBatch.Count;
                treatmentBatch.Clear();
            }

            // One batch per flush, as a migration backfills device statuses: decomposed one at a
            // time, each status is several round trips with a duplicate check per snapshot, and
            // a week of them outlasts a client's request timeout.
            async Task FlushStatusesAsync()
            {
                if (statusBatch.Count == 0) return;
                await _deviceStatuses.ImportDeviceStatusesAsync(
                    statusBatch, dataSource, ct);
                deviceStatusCount += statusBatch.Count;
                statusBatch.Clear();
            }

            foreach (var step in generator.GenerateHistoricalTimeline(seedEnd))
            {
                ct.ThrowIfCancellationRequested();

                var readings = DemoCoverageSeeds.CgmReadings(step);
                foreach (var reading in readings)
                {
                    reading.DataSource = dataSource;
                    entryBatch.Add(reading);
                }
                if (readings.Count > 0)
                    episodeTracker.Observe(
                        DateTimeOffset.FromUnixTimeMilliseconds(step.Entry.Mills).UtcDateTime, step.Entry.Sgv);
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

                if (DemoDeviceStatusGenerator.ForStep(step, clock.Demo, config.TargetGlucose, config.BasalRate) is { } status)
                {
                    // Deterministic legacy id, so a re-seed skips the statuses it already wrote.
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

            await StampTempBasalScheduledRatesAsync(clock, dataSource, config.BasalRate, ct);
        }
        else
        {
            // Replay the stored stream so seeded alarm history matches the
            // chart exactly.
            var since = clock.UtcNow.AddDays(-days);
            var stored = db.SensorGlucose
                .AsNoTracking()
                .Where(e => e.Timestamp >= since && e.DataSource == dataSource)
                .OrderBy(e => e.Timestamp)
                .Select(e => new { e.Timestamp, e.Mgdl })
                .AsAsyncEnumerable();
            await foreach (var reading in stored.WithCancellation(ct))
                episodeTracker.Observe(reading.Timestamp, reading.Mgdl);
        }
        episodeTracker.Flush();

        var localToday = clock.LocalToday;

        // Device changes as treatments: the decomposer turns them into
        // DeviceEvents, which drive the dashboard age pills (SAGE/CAGE/…).
        // The schedule is deterministic, so filter out changes whose
        // DeviceEvent already exists — a re-seed would otherwise insert exact
        // duplicates (treatments have no sync key).
        var deviceSchedule = DemoDeviceLifecycle.GenerateSchedule(clock.LocalNow, days, clock.Demo);
        var existingDeviceEventTimes = (await db.DeviceEvents
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
            await _treatmentService.CreateTreatmentsAsync(deviceTreatments, WriteOrigin.Backfill, ct);

        var sleepCount = await SeedSleepAsync(clock, days, dataSource, ct);
        var (heartRateCount, stepCount) = await SeedActivityAsync(clock, days, dataSource, ct);
        var (trackerDefinitions, trackerInstances) =
            await SeedTrackersAsync(db, deviceSchedule, ownerSubjectId, ct);
        var (alertRules, alertExcursions) =
            await SeedAlertsAsync(db, tenant.TenantId, ownerSubjectId, episodeTracker.Episodes, ct);
        if (includeGlucose && dataSource == Nocturne.Core.Constants.DataSources.DemoService)
            await SeedCompressionLowReviewsAsync(clock, days, ct);

        var foodCount = await SeedFoodsAsync(db, clock, mealCarbLinks, ownerSubjectId, ct);
        var stateSpanCount = await SeedStateSpansAsync(clock, days, dataSource, ct);
        await SeedBodyWeightAsync(db, clock, days, dataSource, ct);
        await SeedTimezoneTimelineAsync(db, clock, days, ct);
        await SeedDndWindowAsync(db, clock, ct);
        var clockFaces = await SeedClockFacesAsync(db, ownerSubjectId, ct);
        var notificationCount = await SeedNotificationsAsync(
            db,
            ownerSubjectId, tenant.TenantId, episodeTracker.Episodes, ct);
        await SeedLabHbA1cAsync(db, clock, dataSource, ct);

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

    /// <summary>The simulation every seed of <paramref name="days"/> days runs.</summary>
    internal static DemoModeConfiguration ConfigurationFor(int days) =>
        new() { BackfillDays = days, RandomSeed = RandomSeed };

    /// <summary>
    /// Seeds the Nightscout profile document through the profile write service
    /// (which decomposes it into TherapySettings plus the basal/carb-ratio/
    /// sensitivity/target schedules), unless the demo profile already exists.
    /// </summary>
    private async Task<int> SeedProfileAsync(
        NocturneDbContext db, DemoModeConfiguration config, SeedingClock clock, CancellationToken ct)
    {
        var exists = await db.TherapySettings
            .AnyAsync(t => t.ProfileName == DemoTherapyProfile.ProfileName, ct);
        if (exists)
            return 0;

        await _profileWriteService.CreateProfilesAsync(
            [DemoTherapyProfile.BuildProfile(config, clock.UtcNow, clock.ZoneId)], ct);
        return 1;
    }

    /// <summary>
    /// One overnight sleep session per night, generated by the shared
    /// scenario-aware generator and written through <see cref="ISleepService"/>
    /// so RLS, entity mapping, and (Source, OriginalId) dedup on re-seed are
    /// handled like a connector import. Anchored to the tenant's local midnight
    /// so bedtime lands at night in the tenant's zone; stored as UTC.
    /// </summary>
    private async Task<int> SeedSleepAsync(
        SeedingClock clock, int days, string sourceApp, CancellationToken ct)
    {
        var count = 0;
        for (var d = 1; d <= days; d++)
        {
            // Night ending on the morning of (today - d + 1).
            var localMorning = clock.LocalToday.AddDays(-(d - 1));
            var session = DemoHealthDataGenerator.GenerateSleepSession(localMorning, sourceApp, clock.Demo);

            // Seeding before ~08:00 local would otherwise write last night's
            // session with an EndTime still in the future.
            if (session.EndTime > clock.UtcNow)
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
        SeedingClock clock, int days, string dataSource, CancellationToken ct)
    {
        var heartRates = new List<HeartRate>();
        var stepCounts = new List<StepCount>();

        for (var d = 0; d < days; d++)
        {
            var (dayHeartRates, daySteps) = DemoHealthDataGenerator.GenerateDailyActivity(
                clock.LocalToday.AddDays(-d), dataSource, clock.Demo);
            heartRates.AddRange(dayHeartRates.Where(h => h.Timestamp <= clock.UtcNow));
            stepCounts.AddRange(daySteps.Where(s => s.Timestamp <= clock.UtcNow));
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
        NocturneDbContext db,
        List<DeviceChangeEvent> schedule, Guid? ownerSubjectId, CancellationToken ct)
    {
        if (ownerSubjectId is not { } owner)
            return (0, 0);

        var userId = owner.ToString();
        var existing = await _trackerRepository.GetAllDefinitionsAsync(ct);

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
                    TenantId = db.TenantId,
                    CreatedBy = userId,
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
            await db.TrackerInstances.PurgeAsync(i => i.DefinitionId == definition.Id, ct);

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
    /// with an in-app channel to <paramref name="ownerSubjectId"/>, and no channel
    /// without one) plus historical excursions and resolved alert
    /// instances taken from the threshold crossings of the generated glucose
    /// stream — so /alerts/history lines up with the chart. Rules are
    /// found-or-created by name; their alarm history is wiped and rebuilt.
    /// </summary>
    private async Task<(int Rules, int Excursions)> SeedAlertsAsync(
        NocturneDbContext db,
        Guid tenantId,
        Guid? ownerSubjectId,
        IReadOnlyList<DemoAlertSeeds.GlucoseEpisode> episodes,
        CancellationToken ct)
    {
        IReadOnlyList<AlertRuleChannelWrite> channels = ownerSubjectId is { } owner
            ? [new AlertRuleChannelWrite(ChannelType.InApp, owner.ToString())]
            : [];
        var seeded = await _alertRules.CreateMissingByNameAsync(
            DemoAlertSeeds.Defaults
                .Select(seed => new NewAlertRule(
                    new AlertRuleWrite(
                        seed.Name,
                        seed.Description,
                        seed.ConditionType,
                        seed.ConditionParamsJson,
                        _scopeClassifier.Classify(seed.ConditionType, seed.ConditionParamsJson),
                        seed.Severity,
                        IsEnabled: true,
                        SortOrder: 0,
                        AllowThroughDnd: false,
                        AutoResolveEnabled: false,
                        AutoResolveParams: null,
                        ClientConfiguration: "{}"),
                    channels))
                .ToList(),
            ct);
        var rulesByName = seeded.RuleIdsByName;
        var rulesCreated = seeded.Created;

        // Rebuild alarm history for the seeded rules (idempotent re-seed).
        var seededRuleIds = rulesByName.Values.ToList();
        await db.AlertInstances.PurgeAsync(
            i => i.AlertExcursion != null && seededRuleIds.Contains(i.AlertExcursion.AlertRuleId), ct);
        await db.AlertExcursions.PurgeAsync(e => seededRuleIds.Contains(e.AlertRuleId), ct);

        var excursionsCreated = 0;
        foreach (var episode in episodes.OrderByDescending(e => e.StartUtc).Take(MaxAlarmEpisodes))
        {
            if (!rulesByName.TryGetValue(episode.RuleName, out var ruleId))
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
                AlertRuleId = ruleId,
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
            db.AlertExcursions.Add(excursion);
            excursionsCreated++;
        }
        await db.SaveChangesAsync(ct);

        return (rulesCreated, excursionsCreated);
    }

    internal static FoodEntity BuildFood(DemoLifestyleSeeds.FoodSeed seed, Guid tenantId, int position) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Name = seed.Name,
        Category = seed.Category,
        Subcategory = seed.Subcategory,
        Portion = seed.Portion,
        Unit = seed.Unit,
        Carbs = seed.Carbs,
        Protein = seed.Protein,
        Fat = seed.Fat,
        Energy = seed.EnergyKilojoules,
        Position = position,
    };

    /// <summary>
    /// The food library (found-or-created by name), favorites for the owner,
    /// and per-meal food attribution lines linking library foods to the carb
    /// intakes the generated meals decomposed into. Attribution is rebuilt from
    /// the meal timestamps, so re-seeding stays consistent.
    /// </summary>
    private async Task<int> SeedFoodsAsync(
        NocturneDbContext db,
        SeedingClock clock,
        List<(DateTime Time, string MealName)> mealCarbLinks, Guid? ownerSubjectId, CancellationToken ct)
    {
        var foodsByName = new Dictionary<string, FoodEntity>(StringComparer.OrdinalIgnoreCase);
        var created = 0;

        var position = 0;
        foreach (var seed in DemoLifestyleSeeds.FoodLibrary)
        {
            position++;
            var food = await db.Foods.FirstOrDefaultAsync(f => f.Name == seed.Name, ct);
            if (food is null)
            {
                food = BuildFood(seed, db.TenantId, position);
                db.Foods.Add(food);
                created++;
            }

            foodsByName[seed.Name] = food;
        }
        await db.SaveChangesAsync(ct);

        // Favorites: the snacks and one dinner, owned by the demo member.
        if (ownerSubjectId is { } owner)
        {
            var userId = owner.ToString();
            var favoriteNames = new[] { "Jelly beans", "Muesli bar", "Homemade pizza slices" };
            foreach (var name in favoriteNames)
            {
                if (!foodsByName.TryGetValue(name, out var food))
                    continue;
                var exists = await db.UserFoodFavorites
                    .AnyAsync(f => f.UserId == userId && f.FoodId == food.Id, ct);
                if (!exists)
                {
                    db.UserFoodFavorites.Add(new UserFoodFavoriteEntity
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = db.TenantId,
                        UserId = userId,
                        FoodId = food.Id,
                    });
                }
            }
            await db.SaveChangesAsync(ct);
        }

        // Food attribution on the meals' carb intakes (~60% of meals, as a
        // realistic user would log). Meal timestamps identify the intakes.
        if (mealCarbLinks.Count > 0)
        {
            var mealTimes = mealCarbLinks.Select(m => m.Time).ToList();
            var intakes = await db.CarbIntakes
                .Where(c => mealTimes.Contains(c.Timestamp))
                .Select(c => new { c.Id, c.Timestamp, c.Carbs })
                .ToListAsync(ct);
            var linkedIntakeIds = intakes.Select(i => i.Id).ToList();
            var breakdowns = new List<TreatmentFood>();

            foreach (var (time, mealName) in mealCarbLinks)
            {
                // Deterministic rolls key on the meal's local calendar day,
                // like every other demo stream (the UTC day differs east of
                // Greenwich).
                var localMeal = clock.Demo.ToLocal(time);
                if (DayScenarios.Roll(localMeal.Date, $"food-link:{localMeal.Hour}", 100) >= 60)
                    continue;

                var intake = intakes.FirstOrDefault(i => i.Timestamp == time);
                if (intake is null)
                    continue;

                foreach (var seed in DemoLifestyleSeeds.MealFoodsFor(localMeal.Date, mealName))
                {
                    if (!foodsByName.TryGetValue(seed.Name, out var food))
                        continue;
                    breakdowns.Add(new TreatmentFood
                    {
                        Id = Guid.CreateVersion7(),
                        CarbIntakeId = intake.Id,
                        FoodId = food.Id,
                        Portions = seed.Carbs > 0 ? Math.Round((decimal)(intake.Carbs / seed.Carbs), 2) : 1,
                        Carbs = (decimal)intake.Carbs,
                    });
                }
            }
            await _treatmentFoods.ReplaceForCarbIntakesAsync(linkedIntakeIds, breakdowns, ct);
        }

        return created;
    }

    /// <summary>
    /// A legacy "Temp Basal" treatment carries no scheduled rate, so neither does
    /// its decomposed TempBasal. Basal analysis then cannot say whether a temp ran
    /// above or below schedule. The seeder knows the schedule it seeded, so it
    /// stamps it on its own temps after decomposition.
    /// </summary>
    private Task StampTempBasalScheduledRatesAsync(
        SeedingClock clock,
        string dataSource, double baseRate, CancellationToken ct) =>
        _tempBasals.StampScheduledRatesAsync(
            dataSource, start => DemoTherapyProfile.ScheduledRateAt(clock.Demo.ToLocal(start), baseRate), ct);

    /// <summary>
    /// State spans for the window: pump mode with manual/exercise windows, workout
    /// overrides and temporary targets, illness runs, and the travel span; and the
    /// seeded therapy profile's activation over the whole window. Rows carrying our
    /// data source are wiped and rebuilt (idempotent re-seed).
    /// </summary>
    private async Task<int> SeedStateSpansAsync(
        SeedingClock clock, int days, string dataSource, CancellationToken ct)
    {
        var localToday = clock.LocalToday;
        await _profileActivations.ReplaceByDataSourceAsync(
            dataSource,
            [
                new Nocturne.Core.Models.V4.ProfileActivation
                {
                    Timestamp = clock.ToUtc(localToday.AddDays(-days)),
                    Phase = Nocturne.Core.Models.V4.ProfileActivationPhase.Requested,
                    Origin = Nocturne.Core.Models.V4.ProfileActivationOrigin.User,
                    BaseProfileName = DemoTherapyProfile.ProfileName,
                    DataSource = dataSource,
                },
            ],
            ct);

        var spans = DemoLifestyleSeeds.BuildSpans(localToday, days, clock.LocalNow);
        await _stateSpans.ReplaceBySourceAsync(
            dataSource,
            spans.Select(seed => new StateSpan
            {
                Category = seed.Category,
                State = seed.State,
                StartTimestamp = clock.ToUtc(seed.StartLocal),
                EndTimestamp = seed.EndLocal is { } end ? clock.ToUtc(end) : null,
                Source = dataSource,
                Metadata = seed.Metadata?.ToDictionary(pair => pair.Key, pair => pair.Value),
            }).ToList(),
            ct);

        return spans.Count;
    }

    internal sealed record DeviceSeed(
        Nocturne.Core.Models.V4.DeviceCategory Category,
        string Manufacturer,
        string Model,
        Nocturne.Core.Models.V4.AidAlgorithm? Aid,
        string? Serial);

    internal static readonly DeviceSeed[] DeviceRoster =
    [
        new(Nocturne.Core.Models.V4.DeviceCategory.CGM, "Dexcom", "G7", null, DemoCoverageSeeds.PrimaryCgmSerial),
        new(Nocturne.Core.Models.V4.DeviceCategory.CGM, "Dexcom", "G6", null, DemoCoverageSeeds.ComparisonCgmSerial),
        new(Nocturne.Core.Models.V4.DeviceCategory.InsulinPump, "Insulet", "Omnipod DASH",
            Nocturne.Core.Models.V4.AidAlgorithm.Trio, null),
        new(Nocturne.Core.Models.V4.DeviceCategory.GlucoseMeter, "Ascensia", "Contour Next One", null, null),
    ];

    internal static PatientDeviceEntity BuildDevice(DeviceSeed seed, Guid tenantId, DateOnly today) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        DeviceCategory = seed.Category.ToString(),
        Manufacturer = seed.Manufacturer,
        Model = seed.Model,
        SerialNumber = seed.Serial,
        AidAlgorithm = seed.Aid?.ToString(),
        StartDate = today.AddMonths(-8),
        IsCurrent = true,
    };

    /// <summary>
    /// The patient record singleton, the device roster (CGM, pod, meter), and
    /// the current insulin — the /settings/patient page and device attribution
    /// context. Only unset fields are filled. A report read during seeding
    /// creates an empty record through the API's get-or-create, and a re-seed
    /// must not overwrite the user's own edits.
    /// </summary>
    private async Task SeedPatientProfileAsync(NocturneDbContext db, SeedingClock clock, CancellationToken ct)
    {
        var record = await db.PatientRecords.FirstOrDefaultAsync(ct);
        if (record is null)
        {
            record = new PatientRecordEntity { Id = Guid.CreateVersion7(), TenantId = db.TenantId };
            db.PatientRecords.Add(record);
        }
        record.PreferredName ??= "Demo";
        record.DiabetesType ??= "type1";
        record.DiagnosisDate ??= new DateOnly(2014, 3, 12);
        record.DateOfBirth ??= new DateOnly(1992, 4, 17);
        record.Timezone ??= clock.ZoneId;

        foreach (var seed in DeviceRoster)
        {
            var existing = await db.PatientDevices
                .FirstOrDefaultAsync(d => d.Manufacturer == seed.Manufacturer && d.Model == seed.Model, ct);
            if (existing is null)
                db.PatientDevices.Add(BuildDevice(seed, db.TenantId, DateOnly.FromDateTime(clock.LocalToday)));
            else if (existing.SerialNumber is null && seed.Serial is not null)
                existing.SerialNumber = seed.Serial;
        }

        if (!await db.PatientInsulins.AnyAsync(ct))
        {
            db.PatientInsulins.Add(new PatientInsulinEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = db.TenantId,
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

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Leaves the newest planted compression low pending, accepts the next and dismisses the one
    /// after, walking back past the nights the detector did not flag.
    /// </summary>
    /// <remarks>
    /// The detector rejects a drop the insulin and carbs on board explain, and the random overnight
    /// trace sometimes leaves enough on board, so a planted low is not found on every night. A night
    /// whose artifact is still ahead of the run, or that already holds an active suggestion from
    /// another source, yields none either.
    /// </remarks>
    private async Task SeedCompressionLowReviewsAsync(SeedingClock clock, int days, CancellationToken ct)
    {
        var nights = Enumerable.Range(0, days)
            .Select(offset => DateOnly.FromDateTime(clock.LocalToday.AddDays(-offset)))
            .Where(night => DemoCoverageSeeds.CompressionArtifactEndedBy(night, clock.Demo, clock.UtcNow));
        var planted = new List<CompressionLowSuggestion>();
        foreach (var night in nights)
        {
            var nadirMills = DemoCoverageSeeds.CompressionNadirMills(night, clock.Demo);
            var suggestion = (await DemoSuggestionsForNightAsync(night, ct)).FirstOrDefault(demo =>
                demo.StartMills <= nadirMills && demo.EndMills >= nadirMills);
            if (suggestion is null)
                continue;

            planted.Add(suggestion);
            if (planted.Count == 3)
                break;
        }

        if (planted.ElementAtOrDefault(1) is { Status: CompressionLowStatus.Pending } accepted)
            await _compressionLowService.AcceptSuggestionAsync(
                accepted.Id, accepted.StartMills, accepted.EndMills, ct, demoSeed: true);
        if (planted.ElementAtOrDefault(2) is { Status: CompressionLowStatus.Pending } dismissed)
            await _compressionLowService.DismissSuggestionAsync(dismissed.Id, ct);
    }

    /// <summary>
    /// The demo's suggestions for <paramref name="night"/>, detecting it first when a previous seed
    /// left none there.
    /// </summary>
    private async Task<List<CompressionLowSuggestion>> DemoSuggestionsForNightAsync(DateOnly night, CancellationToken ct)
    {
        var existing = (await _compressionLowService.GetSuggestionsAsync(null, night, ct))
            .ToList();
        var existingDemo = existing
            .Where(suggestion => suggestion.DataSource == Nocturne.Core.Constants.DataSources.DemoService)
            .OrderBy(suggestion => suggestion.StartMills)
            .ToList();
        if (existingDemo.Count > 0)
            return existingDemo;

        var existingIds = existing.Select(suggestion => suggestion.Id).ToHashSet();
        await _compressionLowDetectionService.DetectForNightAsync(night, ct);
        var detected = (await _compressionLowService.GetSuggestionsAsync(
                CompressionLowStatus.Pending, night, ct))
            .Where(suggestion => !existingIds.Contains(suggestion.Id))
            .OrderBy(suggestion => suggestion.StartMills)
            .ToList();
        foreach (var suggestion in detected)
        {
            var readings = await _entryService.GetEntriesAsync(
                find: $"{{\"mills\":{{\"$gte\":{suggestion.StartMills - 900_000},\"$lte\":{suggestion.EndMills + 900_000}}}}}",
                count: 1000,
                skip: 0,
                cancellationToken: ct);
            var candidateReadings = readings.Where(entry => entry.GlucoseMgdl() > 0).ToList();
            if (candidateReadings.Count == 0 || candidateReadings.Any(entry =>
                    entry.DataSource != Nocturne.Core.Constants.DataSources.DemoService))
                continue;

            suggestion.DataSource = Nocturne.Core.Constants.DataSources.DemoService;
            await _compressionLowRepository.UpdateAsync(suggestion, ct);
        }
        return detected
            .Where(suggestion => suggestion.DataSource == Nocturne.Core.Constants.DataSources.DemoService)
            .ToList();
    }

    private async Task SeedLabHbA1cAsync(NocturneDbContext db, SeedingClock clock, string dataSource, CancellationToken ct)
    {
        var localToday = clock.LocalToday;
        if (dataSource != Nocturne.Core.Constants.DataSources.DemoService)
            return;

        const string note = "Demo lab result";
        if (await db.LabHbA1cResults.AnyAsync(result => result.Note == note, ct))
            return;

        foreach (var (monthsAgo, value) in new[] { (12, 7.2), (6, 6.9), (0, 6.8) })
        {
            db.LabHbA1cResults.Add(new LabHbA1cResultEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = db.TenantId,
                MeasuredAt = DateTime.SpecifyKind(localToday.AddMonths(-monthsAgo), DateTimeKind.Utc),
                ValuePercent = value,
                Note = note,
                CreatedAt = clock.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Weekly Sunday-morning weigh-ins with a deterministic sync key.</summary>
    internal static async Task SeedBodyWeightAsync(
        NocturneDbContext db,
        SeedingClock clock, int days, string dataSource, CancellationToken ct)
    {
        var localToday = clock.LocalToday;
        var existing = (await db.BodyWeights
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
            if (weighIn > clock.LocalNow)
                continue;

            db.BodyWeights.Add(new BodyWeightEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = db.TenantId,
                Mills = new DateTimeOffset(clock.ToUtc(weighIn)).ToUnixTimeMilliseconds(),
                WeightKg = (decimal)DemoLifestyleSeeds.WeightKgOn(day),
                Device = "Demo Scale",
                DataSource = dataSource,
                SyncIdentifier = syncId,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The timezone timeline: home zone from the window start, with a
    /// five-day trip (matching the Travel state span) three weeks back.
    /// EffectiveFrom is a local wall-clock value (Kind=Unspecified), matching
    /// <c>TimezoneTimelineService</c>. Created only when the timeline is empty.
    /// </summary>
    internal static async Task SeedTimezoneTimelineAsync(NocturneDbContext db, SeedingClock clock, int days, CancellationToken ct)
    {
        var localToday = clock.LocalToday;
        if (await db.TimezoneTimeline.AnyAsync(ct))
            return;

        var home = clock.ZoneId;
        var windowStart = localToday.AddDays(-days);
        var tripStart = localToday.AddDays(-DemoLifestyleSeeds.TripStartDaysAgo).AddHours(14);
        var tripEnd = tripStart.AddDays(DemoLifestyleSeeds.TripLengthDays).AddHours(-4);

        db.TimezoneTimeline.Add(new TimezoneTimelineEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = db.TenantId,
            EffectiveFrom = DateTime.SpecifyKind(windowStart, DateTimeKind.Unspecified),
            Timezone = home,
        });

        if (tripStart > windowStart && tripEnd < clock.LocalNow)
        {
            db.TimezoneTimeline.Add(new TimezoneTimelineEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = db.TenantId,
                EffectiveFrom = DateTime.SpecifyKind(tripStart, DateTimeKind.Unspecified),
                Timezone = DemoLifestyleSeeds.TripTimezone,
            });
            db.TimezoneTimeline.Add(new TimezoneTimelineEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = db.TenantId,
                EffectiveFrom = DateTime.SpecifyKind(tripEnd, DateTimeKind.Unspecified),
                Timezone = home,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One historical, already-cleared DND window so the alerts pages show a
    /// worked example without suppressing the demo's live alerts.
    /// </summary>
    internal static async Task SeedDndWindowAsync(NocturneDbContext db, SeedingClock clock, CancellationToken ct)
    {
        if (await db.DndWindows.AnyAsync(ct))
            return;

        var start = clock.LocalToday.AddDays(-1).AddHours(22.5);
        db.DndWindows.Add(new DndWindowEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = db.TenantId,
            Scope = DndScope.All,
            StartedAt = clock.ToUtc(start),
            EndsAt = clock.ToUtc(start.AddHours(8.5)),
            ClearedAt = clock.ToUtc(start.AddHours(8.2)),
            ClearedBy = "demo",
            Source = "web",
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The default clock face for the owner, found-or-created by name.</summary>
    private async Task<int> SeedClockFacesAsync(NocturneDbContext db, Guid? ownerSubjectId, CancellationToken ct)
    {
        if (ownerSubjectId is not { } owner)
            return 0;

        var userId = owner.ToString();
        var exists = await db.ClockFaces.AnyAsync(c => c.UserId == userId, ct);
        if (exists)
            return 0;

        var ownerPreferences = (await _subjects.GetAsync(owner, ct))?.Preferences;

        db.ClockFaces.Add(new ClockFaceEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = db.TenantId,
            UserId = userId,
            Name = "Bedside Clock",
            ConfigJson = ClockFaceService.Serialize(
                ClockFaceConfig.Starter(UserDisplayPreferences.Deserialize(ownerPreferences))),
        });
        await db.SaveChangesAsync(ct);
        return 1;
    }

    /// <summary>
    /// In-app notifications mirroring the most recent seeded alarm episodes
    /// (read where the excursion was acknowledged), so /notifications shows the
    /// same story as /alerts/history. Rebuilt per seed via the data source tag.
    /// </summary>
    private async Task<int> SeedNotificationsAsync(
        NocturneDbContext db,
        Guid? ownerSubjectId,
        Guid tenantId,
        IReadOnlyList<DemoAlertSeeds.GlucoseEpisode> episodes,
        CancellationToken ct)
    {
        if (ownerSubjectId is not { } owner)
            return 0;

        var userId = owner.ToString();
        await db.InAppNotifications.PurgeAsync(n => n.UserId == userId && n.Source == "sample-seed", ct);

        var created = 0;
        foreach (var episode in episodes.OrderByDescending(e => e.StartUtc).Take(MaxAlarmNotifications))
        {
            var rng = DayScenarios.RngFor(episode.StartUtc.Date, $"ack:{episode.RuleName}");
            var acknowledged = rng.NextDouble() < 0.7;

            db.InAppNotifications.Add(new InAppNotificationEntity
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

        await db.SaveChangesAsync(ct);
        return created;
    }
}
