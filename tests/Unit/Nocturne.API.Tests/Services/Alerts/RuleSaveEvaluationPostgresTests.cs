using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Services.SetupHub;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A save that creates a rule, enables one or changes its conditions has the rule judged against
/// the current context once it commits, with no new reading (docs/alerts/engine-semantics.md §5.2).
/// Every writer saves through <see cref="AlertRuleRearm"/>, which announces to the real
/// <see cref="AlertFactEvaluationScheduler"/>; the evaluation runs through the write path's own
/// evaluator, orchestrator and engine, on PostgreSQL under the runtime role and Row Level Security.
/// </summary>
[Trait("Category", "Integration")]
public class RuleSaveEvaluationPostgresTests(RuleSaveEvaluationPostgresTests.Database database)
    : IClassFixture<RuleSaveEvaluationPostgresTests.Database>
{
    private static readonly DateTime Stored = new(2026, 11, 24, 3, 0, 0, DateTimeKind.Utc);
    private const string Below70 = """{"direction":"below","value":70}""";
    private const string Below50 = """{"direction":"below","value":50}""";
    private const string TelegramUserId = "864203571";

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("rule_save_evaluation");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>Records the rules each pass evaluated, then decides them.</summary>
    private sealed class RecordingOrchestrator(AlertOrchestrator inner, List<IReadOnlyList<Guid>> passes) : IAlertOrchestrator
    {
        public Task<AlertPassOutcome> EvaluateRulesAsync(
            IReadOnlyList<AlertRuleSnapshot> rules, IReadOnlySet<Guid> enabledRuleIds, SensorContext context,
            long? readAt, CancellationToken ct)
        {
            lock (passes)
                passes.Add(rules.Select(r => r.Id).ToList());
            return inner.EvaluateRulesAsync(rules, enabledRuleIds, context, readAt, ct);
        }
    }

    /// <summary>Counts the evaluations the scheduler starts, whatever they go on to select.</summary>
    private sealed class CountingEvaluator(CanonicalAlertEvaluator inner, Tenant tenant) : ICanonicalAlertEvaluator
    {
        public Task<bool> EvaluateAsync(
            MovedFacts moved, ReadingReport report = ReadingReport.Here, CancellationToken ct = default)
        {
            Interlocked.Increment(ref tenant.Evaluations);
            return inner.EvaluateAsync(moved, report, ct);
        }
    }

    /// <summary>
    /// One tenant whose newest reading is 55 mg/dL, and one API process over the shared database:
    /// its scheduler, the evaluation it runs, and the rule writers, each saving through one
    /// <see cref="AlertRuleRearm"/>.
    /// </summary>
    private sealed class Tenant : IDisposable
    {
        private readonly ServiceProvider _services;

        public Tenant(Guid id, Func<NocturneDbContext> context)
        {
            Id = id;
            Contexts = new SuppliedContextFactory(context);
            var canonical = new Mock<ICanonicalGlucoseService>();
            var low = new SensorGlucose { Id = Guid.CreateVersion7(), Timestamp = Stored, CreatedAt = Stored, Mgdl = 55 };
            canonical.Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(low);
            canonical.Setup(c => c.GetLatestAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(low);
            canonical.Setup(c => c.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([low]);
            var enricher = new Mock<ISensorContextEnricher>();
            enricher
                .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) => c);

            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddMetrics();
            services.AddSingleton<TimeProvider>(Time);
            services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
            services.AddScoped<IAuditContext, AuditContext>();
            services.AddSingleton<INocturneDbContextFactory>(Contexts);
            services.AddSingleton<IAlertRepository>(new AlertRepository(Contexts));
            services.AddSingleton<IAlertTrackerRepository>(new AlertTrackerRepository(Contexts));
            services.AddSingleton<IEvaluatedReadingStore>(new AlertEvaluatedReadingRepository(Contexts));
            services.AddSingleton(canonical.Object);
            services.AddSingleton(enricher.Object);
            services.AddSingleton(Mock.Of<IExcursionOpenedHandler>());
            services.AddSingleton(Mock.Of<IExcursionResolutionHandler>());
            services.AddSingleton(Mock.Of<IUnevaluableRuleNotice>());
            services.AddSingleton(Mock.Of<IClockAnomalyNotice>());
            services.AddSingleton(Mock.Of<IAlertChannelNotice>());
            services.AddSingleton(NoHeldRules.Notice());
            services.AddSingleton<AlertEngineErrors>();
            services.AddSingleton<ConditionVersionLog>();
            services.AddSingleton<NotTrueRecordingFloor>();
            services.AddSingleton<AlertRuleEvaluationGate>();
            services.AddSingleton<AlertRuleDecisions>();
            services.AddSingleton<AlertEvaluationWatermark>();
            services.AddScoped<IAlertEvaluationEngine, RustBackedAlertEngine>();
            services.AddScoped<AlertOrchestrator>();
            services.AddScoped<IAlertOrchestrator>(sp => new RecordingOrchestrator(sp.GetRequiredService<AlertOrchestrator>(), _passes));
            services.AddScoped<CanonicalAlertEvaluator>();
            services.AddScoped<ICanonicalAlertEvaluator>(sp => new CountingEvaluator(sp.GetRequiredService<CanonicalAlertEvaluator>(), this));
            services.AddSingleton<AlertFactEvaluationScheduler>();
            _services = services.BuildServiceProvider();

            var accessor = new HttpContextTenantAccessor();
            accessor.SetTenant(new TenantContext(id, $"save-{id:N}", "Save", true, IsDemo: false));
            Rearm = new AlertRuleRearm(
                _services.GetRequiredService<AlertRuleEvaluationGate>(), Scheduler, accessor);
        }

        private readonly List<IReadOnlyList<Guid>> _passes = [];

        public int Evaluations;

        public Guid Id { get; }

        public SuppliedContextFactory Contexts { get; }

        public FakeTimeProvider Time { get; } = new(Stored.AddMinutes(1));

        public AlertRuleRearm Rearm { get; }

        public AlertFactEvaluationScheduler Scheduler => _services.GetRequiredService<AlertFactEvaluationScheduler>();

        public IReadOnlyList<IReadOnlyList<Guid>> Passes
        {
            get { lock (_passes) return [.. _passes]; }
        }

        public static AlertRuleRetirement Retirement() =>
            new(Mock.Of<IExcursionTracker>(), Mock.Of<IExcursionResolutionHandler>(), Mock.Of<IUnevaluableRuleNotice>());

        public static IRuleScopeClassifier Classifier()
        {
            var classifier = new Mock<IRuleScopeClassifier>();
            classifier
                .Setup(c => c.Classify(It.IsAny<AlertConditionType>(), It.IsAny<string>()))
                .Returns(RuleScopeClass.Undirected);
            return classifier.Object;
        }

        /// <summary>Lets the debounce pass, then waits for every evaluation the saves started.</summary>
        public async Task SettledAsync()
        {
            Time.Advance(AlertFactEvaluationScheduler.Debounce);
            await Scheduler.StoppedAsync(CancellationToken.None).WaitAsync(Eventually.DefaultPatience);
        }

        public async Task<int> ExcursionsAsync(Guid rule)
        {
            await using var db = await Contexts.CreateAsync();
            return await db.AlertExcursions.CountAsync(e => e.AlertRuleId == rule);
        }

        public AlertRulesController Controller()
        {
            var validator = new Mock<IAlertRuleConditionValidator>();
            validator
                .Setup(v => v.Validate(
                    It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns([]);
            return new AlertRulesController(
                Contexts,
                new TenantMembershipRepository(Contexts, AccountLinkRoutes.Off),
                new SubjectRepository(Contexts, AccountLinkRoutes.Off),
                new AlertRepository(Contexts),
                new AlertRuleRepository(Contexts, TimeProvider.System),
                MockTenantAccessor.Create(Id).Object,
                Mock.Of<IAlertReferenceService>(),
                Mock.Of<IAlertDeliveryService>(),
                Classifier(),
                validator.Object,
                Mock.Of<ISecretEncryptionService>(),
                Rearm,
                Retirement(),
                Mock.Of<ILogger<AlertRulesController>>(), Time);
        }

        public TrackerAlertRuleSyncService TrackerSync()
        {
            var references = new Mock<IAlertReferenceService>();
            references
                .Setup(r => r.FindReferencingRulesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            return new TrackerAlertRuleSyncService(
                Contexts, Classifier(), references.Object, Rearm, Retirement(), Time,
                NullLogger<TrackerAlertRuleSyncService>.Instance, AccountLinkRoutes.Off);
        }

        public AlertSetupService Setup() => new(
            Contexts,
            new TenantRepository(Contexts),
            Mock.Of<ITenantGlucoseUnits>(u => u.ResolveAsync(It.IsAny<CancellationToken>()) == Task.FromResult("mg/dl")),
            new TenantMembershipRepository(Contexts, AccountLinkRoutes.Off),
            new SubjectRepository(Contexts, AccountLinkRoutes.Off),
            new AlertRuleRepository(Contexts, TimeProvider.System),
            Classifier(),
            Mock.Of<ISecretEncryptionService>(),
            Mock.Of<IAlertDeliveryService>(),
            Rearm,
            Retirement(),
            Time);

        public void Dispose() => _services.Dispose();
    }

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    private async Task<Tenant> SeedAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'rule-save', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"save-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        return new Tenant(tenant, () => Context(tenant));
    }

    private async Task<Guid> SeedRuleAsync(Tenant tenant, string condition, bool enabled)
    {
        var rule = Guid.CreateVersion7();
        await using var db = Context(tenant.Id);
        db.AlertRules.Add(new AlertRuleEntity
        {
            Id = rule,
            TenantId = tenant.Id,
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = condition,
            Severity = AlertRuleSeverity.Critical,
            IsEnabled = enabled,
            EnabledAt = enabled ? Stored.AddDays(-1) : null,
            CreatedAt = Stored.AddDays(-1),
            UpdatedAt = Stored.AddDays(-1),
        });
        await db.SaveChangesAsync();
        return rule;
    }

    private async Task<Guid> SeedTrackerDefinitionAsync(Tenant tenant)
    {
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CreatedBy = "user-1",
            Name = "Sensor",
            Mode = TrackerMode.Duration,
            LifespanHours = 240,
            Category = TrackerCategory.Consumable,
        };
        definition.NotificationThresholds.Add(new TrackerNotificationThresholdEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            TrackerDefinitionId = definition.Id,
            Hours = 24,
            Urgency = NotificationUrgency.Warn,
        });
        await using var db = Context(tenant.Id);
        db.TrackerDefinitions.Add(definition);
        await db.SaveChangesAsync();
        return definition.Id;
    }

    private async Task<IReadOnlyList<Guid>> ManagedRulesAsync(Tenant tenant, Guid definition)
    {
        await using var db = Context(tenant.Id);
        var tag = TrackerAlertRuleSyncService.ManagedByTag(definition);
        return await db.AlertRules.Where(r => r.ManagedBy == tag).Select(r => r.Id).ToListAsync();
    }

    private static CreateAlertRuleRequest Create(string condition, bool enabled) => new()
    {
        Name = "Low",
        ConditionType = AlertConditionType.Threshold,
        ConditionParams = JsonSerializer.Deserialize<JsonElement>(condition),
        IsEnabled = enabled,
    };

    private static UpdateAlertRuleRequest Update(string condition, string name = "Low", List<CreateAlertRuleChannelRequest>? channels = null) => new()
    {
        Name = name,
        ConditionType = AlertConditionType.Threshold,
        ConditionParams = JsonSerializer.Deserialize<JsonElement>(condition),
        IsEnabled = true,
        Channels = channels,
    };

    private static Guid CreatedId(ActionResult<AlertRuleResponse> result) =>
        ((AlertRuleResponse)((CreatedAtActionResult)result.Result!).Value!).Id;

    [NativeFact]
    public async Task Creating_an_enabled_rule_whose_condition_holds_opens_an_excursion_with_no_new_reading()
    {
        using var tenant = await SeedAsync();

        var rule = CreatedId(await tenant.Controller().CreateRule(Create(Below70, enabled: true), CancellationToken.None));
        await tenant.SettledAsync();

        (await tenant.ExcursionsAsync(rule)).Should().Be(1, "the reading of 55 is below the new rule's 70");
        tenant.Passes.Should().ContainSingle().Which.Should().Equal(rule);
    }

    [NativeFact]
    public async Task A_save_evaluates_only_the_rules_it_saved()
    {
        using var tenant = await SeedAsync();
        var other = await SeedRuleAsync(tenant, Below70, enabled: true);

        var rule = CreatedId(await tenant.Controller().CreateRule(Create(Below70, enabled: true), CancellationToken.None));
        await tenant.SettledAsync();

        tenant.Passes.Should().ContainSingle().Which.Should().Equal(rule);
        (await tenant.ExcursionsAsync(other)).Should().Be(0, "a rule the save did not touch waits for its next reading");
    }

    [NativeFact]
    public async Task Creating_a_disabled_rule_evaluates_nothing()
    {
        using var tenant = await SeedAsync();

        var rule = CreatedId(await tenant.Controller().CreateRule(Create(Below70, enabled: false), CancellationToken.None));
        await tenant.SettledAsync();

        tenant.Evaluations.Should().Be(0);
        (await tenant.ExcursionsAsync(rule)).Should().Be(0);
    }

    [NativeFact]
    public async Task Enabling_a_disabled_rule_opens_its_excursion()
    {
        using var tenant = await SeedAsync();
        var rule = await SeedRuleAsync(tenant, Below70, enabled: false);

        await tenant.Controller().ToggleRule(rule, CancellationToken.None);
        await tenant.SettledAsync();

        tenant.Passes.Should().ContainSingle().Which.Should().Equal(rule);
        (await tenant.ExcursionsAsync(rule)).Should().Be(1);
    }

    [NativeFact]
    public async Task Changing_an_enabled_rules_condition_opens_its_excursion()
    {
        using var tenant = await SeedAsync();
        var rule = await SeedRuleAsync(tenant, Below50, enabled: true);

        await tenant.Controller().UpdateRule(rule, Update(Below70), CancellationToken.None);
        await tenant.SettledAsync();

        tenant.Passes.Should().ContainSingle().Which.Should().Equal(rule);
        (await tenant.ExcursionsAsync(rule)).Should().Be(1);
    }

    [NativeFact]
    public async Task An_edit_of_only_the_name_and_channels_evaluates_nothing()
    {
        using var tenant = await SeedAsync();
        var rule = await SeedRuleAsync(tenant, Below70, enabled: true);
        List<CreateAlertRuleChannelRequest> telegram =
            [new() { ChannelType = ChannelType.TelegramDm, Destination = TelegramUserId }];

        var saved = await tenant.Controller().UpdateRule(rule, Update(Below70, name: "Renamed", channels: telegram), CancellationToken.None);
        await tenant.SettledAsync();

        var response = (AlertRuleResponse)((OkObjectResult)saved.Result!).Value!;
        response.Name.Should().Be("Renamed");
        response.Channels.Should().ContainSingle(c => c.ChannelType == ChannelType.TelegramDm);

        tenant.Evaluations.Should().Be(0, "what the evaluation reads did not change");
        (await tenant.ExcursionsAsync(rule)).Should().Be(0);
    }

    [NativeFact]
    public async Task A_tracker_sync_evaluates_the_managed_rules_it_creates()
    {
        using var tenant = await SeedAsync();
        var definition = await SeedTrackerDefinitionAsync(tenant);

        await tenant.TrackerSync().SyncDefinitionAsync(definition);
        await tenant.SettledAsync();

        var created = await ManagedRulesAsync(tenant, definition);
        created.Should().ContainSingle();
        tenant.Passes.Should().ContainSingle().Which.Should().Equal(created);
    }

    [NativeFact]
    public async Task Managed_rules_created_inside_the_trackers_transaction_are_evaluated_once_it_is_published()
    {
        using var tenant = await SeedAsync();
        var definition = await SeedTrackerDefinitionAsync(tenant);
        var sync = tenant.TrackerSync();
        var writer = new ConsumableTrackerWriter(
            Mock.Of<IDeviceEventRepository>(), sync, tenant.Rearm, Mock.Of<IDeviceEventReactor>(),
            Mock.Of<ISignalRBroadcastService>(), tenant.Time, NullLogger<ConsumableTrackerWriter>.Instance);

        await using (var unit = await DbUnitOfWork.OpenAsync(tenant.Contexts))
            await unit.CommitAsync(token => sync.CreateRulesAsync(unit, definition, token), CancellationToken.None);
        await tenant.SettledAsync();
        tenant.Evaluations.Should().Be(0, "a save inside a caller's transaction cannot see its commit");

        await writer.PublishAsync(
            Mock.Of<ITrackerRepository>(),
            new CreatedConsumableTracker(
                (await new TrackerRepository(tenant.Contexts).GetDefinitionByIdAsync(definition))!,
                new ConsumableTrackerSpec("Sensor", TrackerCategory.Consumable, "sensor", 240, []),
                Run: null,
                StartEvent: null),
            CancellationToken.None);
        await tenant.SettledAsync();

        tenant.Passes.Should().ContainSingle().Which.Should().Equal(await ManagedRulesAsync(tenant, definition));
    }

    [NativeFact]
    public async Task The_setup_hub_evaluates_the_enabled_starter_rules_it_creates()
    {
        using var tenant = await SeedAsync();
        var request = new SaveAlertSetupRequest
        {
            Rules = [new StarterAlertRuleRequest { Kind = StarterAlertKind.High, IsEnabled = false }],
            Channels = [new CreateAlertRuleChannelRequest { ChannelType = ChannelType.TelegramDm, Destination = TelegramUserId }],
        };

        await tenant.Setup().SaveAsync(Guid.NewGuid(), null, request, CancellationToken.None);
        await tenant.SettledAsync();

        var starters = await new AlertRuleRepository(tenant.Contexts, TimeProvider.System).GetStarterRulesAsync(CancellationToken.None);
        tenant.Passes.Should().ContainSingle().Which.Should().BeEquivalentTo(
            [starters[StarterAlertKind.UrgentLow].Rule.Id, starters[StarterAlertKind.Low].Rule.Id, starters[StarterAlertKind.NoReadings].Rule.Id],
            "the disabled high is not evaluated");
        (await tenant.ExcursionsAsync(starters[StarterAlertKind.Low].Rule.Id)).Should().Be(1);
    }
}
