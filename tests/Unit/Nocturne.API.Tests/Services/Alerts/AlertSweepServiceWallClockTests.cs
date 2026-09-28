using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Multitenancy;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Audit;
using Nocturne.API.Tests.Services.Alerts.Engines;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// Rules driven only by <see cref="AlertSweepService"/> ticks, through the real orchestrator and
/// each evaluation engine over in-memory tracker state.
/// </summary>
[Trait("Category", "Unit")]
public class AlertSweepServiceWallClockTests
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0003-000000000001");
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0003-000000000002");
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private const string SignalLoss15 = """{"timeout_minutes": 15}""";

    [Fact]
    public Task Managed_engine_opens_dispatches_once_and_closes_on_resume() =>
        RunOutageAsync(useRustEngine: false);

    [NativeFact]
    public Task Rust_backed_engine_opens_dispatches_once_and_closes_on_resume() =>
        RunOutageAsync(useRustEngine: true);

    [Fact]
    public async Task Tenant_with_no_reading_history_does_not_fire()
    {
        var fixture = new Fixture(useRustEngine: false, AlertConditionType.SignalLoss, SignalLoss15)
        {
            LastReadingAt = null,
        };

        await fixture.SweepAt(T0.AddHours(6));

        fixture.Dispatches.Should().Be(0);
        fixture.InstancesCreated.Should().Be(0);
    }

    [Fact]
    public Task Managed_engine_fires_signal_loss_nested_in_a_night_window() =>
        RunNightOutageAsync(useRustEngine: false);

    [NativeFact]
    public Task Rust_backed_engine_fires_signal_loss_nested_in_a_night_window() =>
        RunNightOutageAsync(useRustEngine: true);

    [Fact]
    public async Task Reading_driven_leaves_keep_the_verdict_of_the_last_reading()
    {
        // Opened by a low reading. The sweep re-judges that same reading, so it must not feed
        // false and start hysteresis.
        const string lowOrLoss = """
            {"operator": "or", "conditions": [
              {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
              {"type": "signal_loss", "signal_loss": {"timeout_minutes": 15}}
            ]}
            """;
        var fixture = new Fixture(useRustEngine: false, AlertConditionType.Composite, lowOrLoss)
        {
            LastReadingAt = T0,
            LatestMgdl = 60,
        };

        await fixture.SweepAt(T0.AddMinutes(1));
        fixture.InstancesCreated.Should().Be(1);

        await fixture.SweepAt(T0.AddMinutes(2));
        await fixture.SweepAt(T0.AddMinutes(4));
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId))!.State.Should().Be("active");
        fixture.Closed.Should().BeEmpty();
    }

    [Fact]
    public async Task Rule_without_a_wall_clock_leaf_is_not_evaluated()
    {
        const string nightLow = """
            {"operator": "and", "conditions": [
              {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
              {"type": "time_of_day", "time_of_day": {"from": "22:00", "to": "06:00", "timezone": null}}
            ]}
            """;
        var fixture = new Fixture(useRustEngine: false, AlertConditionType.Composite, nightLow)
        {
            LastReadingAt = T0,
            LatestMgdl = 60,
        };

        await fixture.SweepAt(T0.AddHours(11));

        fixture.InstancesCreated.Should().Be(0);
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId)).Should().BeNull();
    }

    [Fact]
    public Task Managed_engine_escalates_a_child_of_a_reading_driven_parent() =>
        RunEscalationAsync(useRustEngine: false);

    [NativeFact]
    public Task Rust_backed_engine_escalates_a_child_of_a_reading_driven_parent() =>
        RunEscalationAsync(useRustEngine: true);

    [Fact]
    public Task Managed_engine_measures_signal_loss_from_the_last_usable_reading() =>
        RunErrorReadingOutageAsync(useRustEngine: false);

    [NativeFact]
    public Task Rust_backed_engine_measures_signal_loss_from_the_last_usable_reading() =>
        RunErrorReadingOutageAsync(useRustEngine: true);

    private static async Task RunErrorReadingOutageAsync(bool useRustEngine)
    {
        var fixture = new Fixture(useRustEngine, AlertConditionType.SignalLoss, SignalLoss15);
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 110 });
        for (var minutes = 5; minutes <= 20; minutes += 5)
            fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(minutes), Mgdl = 0 });
        fixture.LastReadingAt = T0.AddMinutes(20);

        await fixture.SweepAt(T0.AddMinutes(14));
        fixture.InstancesCreated.Should().Be(0);

        await fixture.SweepAt(T0.AddMinutes(20.5));
        fixture.InstancesCreated.Should().Be(1, "error readings are not signal: the last usable one is 20 minutes old");
    }

    [Fact]
    public async Task The_lookback_for_a_usable_reading_runs_once_per_newest_reading()
    {
        var fixture = new Fixture(useRustEngine: false, AlertConditionType.SignalLoss, SignalLoss15);
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0, Mgdl = 110 });
        fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(5), Mgdl = 0 });
        fixture.LastReadingAt = T0.AddMinutes(5);

        await fixture.SweepAt(T0.AddMinutes(6));
        await fixture.SweepAt(T0.AddMinutes(6.5));
        await fixture.SweepAt(T0.AddMinutes(7));
        fixture.Canonical.Verify(
            c => c.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);

        fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(10), Mgdl = 0 });
        await fixture.SweepAt(T0.AddMinutes(15.5));
        fixture.Canonical.Verify(
            c => c.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        fixture.InstancesCreated.Should().Be(1, "the last usable reading is still the one at T0");
    }

    [Fact]
    public async Task An_outage_with_no_usable_reading_in_the_lookback_still_fires()
    {
        var fixture = new Fixture(useRustEngine: false, AlertConditionType.SignalLoss, SignalLoss15);
        for (var minutes = 0; minutes <= 60; minutes += 5)
            fixture.Readings.Add(new SensorGlucose { Timestamp = T0.AddMinutes(minutes), Mgdl = 0 });
        fixture.LastReadingAt = T0.AddMinutes(60);

        await fixture.SweepAt(T0.AddMinutes(60.5));

        fixture.InstancesCreated.Should().Be(1);
    }

    [Fact]
    public Task Managed_engine_reports_an_unevaluable_tree_once() =>
        RunUnevaluableAsync(useRustEngine: false);

    [NativeFact]
    public Task Rust_backed_engine_reports_an_unevaluable_tree_once() =>
        RunUnevaluableAsync(useRustEngine: true);

    private static async Task RunUnevaluableAsync(bool useRustEngine)
    {
        const string lossAndBroken = """
            {"operator": "and", "conditions": [
              {"type": "signal_loss", "signal_loss": {"timeout_minutes": 15}},
              {"type": "composite"}
            ]}
            """;
        var fixture = new Fixture(useRustEngine, AlertConditionType.Composite, lossAndBroken)
        {
            LastReadingAt = T0,
        };

        await fixture.SweepAt(T0.AddMinutes(1));
        await fixture.SweepAt(T0.AddMinutes(2));
        await fixture.SweepAt(T0.AddMinutes(3));

        fixture.OrchestratorLogger.Entries.Should().ContainSingle(e => e.Level >= LogLevel.Warning)
            .Which.Message.Should().Contain("cannot be evaluated");
    }

    [Fact]
    public async Task An_unwalkable_tree_is_reported_once_per_version()
    {
        var unwalkable = new AlertRule
        {
            Id = Guid.Parse("00000000-0000-0000-0003-0000000000b1"),
            Name = "Unwalkable",
            ConditionType = AlertConditionType.Composite,
            ConditionParams = null!,
        };
        var fixture = new Fixture(useRustEngine: false, AlertConditionType.SignalLoss, SignalLoss15, unwalkable)
        {
            LastReadingAt = T0,
        };

        await fixture.SweepAt(T0.AddMinutes(1));
        await fixture.SweepAt(T0.AddMinutes(2));
        await fixture.SweepAt(T0.AddMinutes(3));

        fixture.Logger.Warnings.Should().ContainSingle(w => w.Contains(unwalkable.Id.ToString()));
    }

    private static async Task RunEscalationAsync(bool useRustEngine)
    {
        // The parent is not wall-clock, so the sweep evaluates only the child; the child's
        // reference must still resolve against every enabled rule of the tenant.
        var parentId = Guid.Parse("00000000-0000-0000-0003-0000000000a1");
        var parent = new AlertRule
        {
            Id = parentId,
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = """{"direction": "below", "value": 70}""",
            ConfirmationReadings = 1,
        };
        var fixture = new Fixture(
            useRustEngine, AlertConditionType.AlertState,
            $$"""{"alert_id": "{{parentId}}", "state": "unacknowledged", "for_minutes": 15}""",
            parent)
        {
            LastReadingAt = T0,
            LatestMgdl = 60,
        };
        fixture.ActiveAlerts[parentId] = new ActiveAlertSnapshot("firing", T0, null);

        await fixture.SweepAt(T0.AddMinutes(10));
        fixture.InstancesCreated.Should().Be(0, "the parent has been unacknowledged for 10 minutes");

        await fixture.SweepAt(T0.AddMinutes(15));
        fixture.InstancesCreated.Should().Be(1);
        fixture.LastPayload!.AlertType.Should().Be(AlertConditionType.AlertState);
        (await fixture.TrackerRepo.GetTrackerStateAsync(parentId)).Should().BeNull(
            "the reading-driven parent is not the sweep's to evaluate");
    }

    private static async Task RunOutageAsync(bool useRustEngine)
    {
        var fixture = new Fixture(useRustEngine, AlertConditionType.SignalLoss, SignalLoss15)
        {
            LastReadingAt = T0,
        };

        await fixture.SweepAt(T0.AddMinutes(14.5));
        fixture.InstancesCreated.Should().Be(0, "the 15-minute timeout has not elapsed");

        await fixture.SweepAt(T0.AddMinutes(15));
        fixture.InstancesCreated.Should().Be(1);
        fixture.Dispatches.Should().Be(1);
        fixture.LastPayload!.AlertType.Should().Be(AlertConditionType.SignalLoss);

        await fixture.SweepAt(T0.AddMinutes(15.5));
        await fixture.SweepAt(T0.AddMinutes(16));
        await fixture.SweepAt(T0.AddMinutes(45));
        fixture.InstancesCreated.Should().Be(1, "a continuing outage is one excursion");
        fixture.Dispatches.Should().Be(1, "a continuing outage must not re-dispatch every sweep");

        fixture.LastReadingAt = T0.AddMinutes(46);
        await fixture.SweepAt(T0.AddMinutes(46));
        fixture.Closed.Should().BeEmpty("with hysteresis the first false evaluation only starts the window");

        await fixture.SweepAt(T0.AddMinutes(46.5));
        fixture.Closed.Should().ContainSingle()
            .Which.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId))!.State.Should().Be("idle");
        fixture.Dispatches.Should().Be(1);
    }

    private static async Task RunNightOutageAsync(bool useRustEngine)
    {
        const string nightLoss = """
            {"operator": "and", "conditions": [
              {"type": "signal_loss", "signal_loss": {"timeout_minutes": 15}},
              {"type": "time_of_day", "time_of_day": {"from": "22:00", "to": "06:00", "timezone": null}}
            ]}
            """;
        var fixture = new Fixture(useRustEngine, AlertConditionType.Composite, nightLoss)
        {
            LastReadingAt = T0.AddHours(9),
            LatestMgdl = 110,
        };

        await fixture.SweepAt(T0.AddHours(9).AddMinutes(20));
        fixture.InstancesCreated.Should().Be(0, "the outage is 20 minutes old but it is 21:20");

        await fixture.SweepAt(T0.AddHours(10));
        fixture.InstancesCreated.Should().Be(1, "at 22:00 the window opens on an outage already past 15 minutes");
        fixture.Dispatches.Should().Be(1);
        fixture.LastPayload!.AlertType.Should().Be(AlertConditionType.Composite);

        await fixture.SweepAt(T0.AddHours(10).AddMinutes(30));
        fixture.Dispatches.Should().Be(1, "a continuing outage must not re-dispatch every sweep");
    }

    private sealed class Fixture
    {
        private readonly AlertSweepService _sweep;

        public ManualTimeProvider Time { get; } = new();
        public ListLogger<AlertSweepService> Logger { get; } = new();
        public ListLogger<AlertOrchestrator> OrchestratorLogger { get; } = new();
        public InMemoryTrackerRepository TrackerRepo { get; }
        public DateTime? LastReadingAt { get; set; }
        public double? LatestMgdl { get; set; }
        public int InstancesCreated { get; private set; }
        public int Dispatches { get; private set; }
        public AlertPayload? LastPayload { get; private set; }
        public List<ExcursionTransition> Closed { get; } = [];
        public Dictionary<Guid, ActiveAlertSnapshot> ActiveAlerts { get; } = [];

        /// <summary>The canonical stream; when empty, one reading at <see cref="LastReadingAt"/>.</summary>
        public List<SensorGlucose> Readings { get; } = [];

        public Mock<ICanonicalGlucoseService> Canonical { get; } = new();

        public Task SweepAt(DateTime at)
        {
            Time.SetUtcNow(at);
            return _sweep.EvaluateWallClockRulesAsync(CancellationToken.None);
        }

        public Fixture(
            bool useRustEngine, AlertConditionType conditionType, string conditionParams,
            params AlertRule[] otherRules)
        {
            var rule = new AlertRule
            {
                Id = RuleId,
                Name = "Wall clock",
                ConditionType = conditionType,
                ConditionParams = conditionParams,
                ConfirmationReadings = 1,
                HysteresisMinutes = 0,
            };
            var snapshot = new AlertRuleSnapshot(
                RuleId, Tenant, rule.Name, conditionType, rule.ConditionParams,
                AlertRuleSeverity.Warning, "{}", 0, false, null);

            var snapshots = otherRules
                .Select(r => new AlertRuleSnapshot(
                    r.Id, Tenant, r.Name, r.ConditionType, r.ConditionParams,
                    AlertRuleSeverity.Warning, "{}", 0, false, null))
                .Prepend(snapshot)
                .ToList();

            TrackerRepo = new InMemoryTrackerRepository([rule, .. otherRules]);
            var timerStore = new RecordingTimerStore();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(Time);

            IAlertEvaluationEngine engine;
            if (useRustEngine)
            {
                engine = EngineTestHarness.BuildRustEngine(Time, timerStore, TrackerRepo);
            }
            else
            {
                var (managed, provider) = EngineTestHarness.BuildManagedEngine(Time, timerStore, TrackerRepo);
                engine = managed;
                services.AddSingleton(provider);
            }
            services.AddSingleton(engine);

            var repository = new Mock<IAlertRepository>();
            repository
                .Setup(x => x.GetAllEnabledRulesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(snapshots);
            repository
                .Setup(x => x.GetTenantAlertContextAsync(Tenant, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new TenantAlertContext(Tenant, "subject", "slug", "Slug", true, LastReadingAt));
            repository
                .Setup(x => x.CreateInstanceAsync(It.IsAny<CreateAlertInstanceRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((CreateAlertInstanceRequest r, CancellationToken _) =>
                {
                    InstancesCreated++;
                    return new AlertInstanceSnapshot(Guid.NewGuid(), r.TenantId, r.ExcursionId, r.Status, r.TriggeredAt, null, 0);
                });
            repository
                .Setup(x => x.GetChannelsForRuleAsync(Tenant, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            services.AddSingleton(repository.Object);

            var canonical = Canonical;
            canonical
                .Setup(x => x.GetLatestAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Readings.Count > 0
                    ? Readings.MaxBy(r => r.Timestamp)
                    : LastReadingAt is { } at
                        ? new SensorGlucose { Timestamp = at, Mgdl = LatestMgdl ?? 120 }
                        : null);
            canonical
                .Setup(x => x.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DateTime since, CancellationToken _) =>
                    Readings.Where(r => r.Timestamp >= since).OrderByDescending(r => r.Timestamp).ToList());
            services.AddSingleton(canonical.Object);

            var delivery = new Mock<IAlertDeliveryService>();
            delivery
                .Setup(x => x.DispatchAsync(
                    It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
                    It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
                .Callback((Guid _, IReadOnlyList<AlertRuleChannelSnapshot> _, AlertPayload p, CancellationToken _) =>
                {
                    Dispatches++;
                    LastPayload = p;
                })
                .Returns(Task.CompletedTask);
            services.AddSingleton(delivery.Object);

            var enricher = new Mock<ISensorContextEnricher>();
            enricher
                .Setup(x => x.EnrichAsync(
                    It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(),
                    It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, Guid _, CancellationToken _) =>
                    c with { ActiveAlerts = ActiveAlerts });
            services.AddSingleton(enricher.Object);

            var resolution = new Mock<IExcursionResolutionHandler>();
            resolution
                .Setup(x => x.HandleClosedAsync(It.IsAny<ExcursionTransition>(), Tenant, It.IsAny<CancellationToken>()))
                .Callback((ExcursionTransition t, Guid _, CancellationToken _) => Closed.Add(t))
                .Returns(Task.CompletedTask);
            services.AddSingleton(resolution.Object);

            services.AddSingleton(Mock.Of<IAlertAcknowledgementService>());
            services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
            services.AddScoped<IAuditContext, AuditContext>();
            services.AddScoped(_ => new NocturneDbContext(
                new DbContextOptionsBuilder<NocturneDbContext>()
                    .UseSqlite("DataSource=:memory:")
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options));
            services.AddSingleton<ConditionVersionLog>();
            services.AddSingleton<ILogger<AlertOrchestrator>>(OrchestratorLogger);
            services.AddScoped<IAlertOrchestrator, AlertOrchestrator>();

            _sweep = new AlertSweepService(
                services.BuildServiceProvider(), Logger, Time);
        }
    }
}
