using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Audit;
using Nocturne.API.Tests.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A signal-loss rule driven only by <see cref="AlertSweepService"/> ticks, through the real
/// orchestrator and each evaluation engine over in-memory tracker state.
/// </summary>
[Trait("Category", "Unit")]
public class AlertSweepServiceSignalLossTests
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0003-000000000001");
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0003-000000000002");
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public Task Managed_engine_opens_dispatches_once_and_closes_on_resume() =>
        RunOutageAsync(useRustEngine: false);

    [NativeFact]
    public Task Rust_backed_engine_opens_dispatches_once_and_closes_on_resume() =>
        RunOutageAsync(useRustEngine: true);

    [Fact]
    public async Task Tenant_with_no_reading_history_does_not_fire()
    {
        var fixture = new Fixture(useRustEngine: false) { LastReadingAt = null };

        fixture.Time.SetUtcNow(T0.AddHours(6));
        await fixture.Sweep.EvaluateSignalLossAsync(CancellationToken.None);

        fixture.Dispatches.Should().Be(0);
        fixture.InstancesCreated.Should().Be(0);
    }

    private static async Task RunOutageAsync(bool useRustEngine)
    {
        var fixture = new Fixture(useRustEngine) { LastReadingAt = T0 };

        async Task SweepAt(double minutes)
        {
            fixture.Time.SetUtcNow(T0.AddMinutes(minutes));
            await fixture.Sweep.EvaluateSignalLossAsync(CancellationToken.None);
        }

        await SweepAt(14.5);
        fixture.InstancesCreated.Should().Be(0, "the 15-minute timeout has not elapsed");

        await SweepAt(15);
        fixture.InstancesCreated.Should().Be(1);
        fixture.Dispatches.Should().Be(1);
        fixture.LastPayload!.AlertType.Should().Be(AlertConditionType.SignalLoss);

        await SweepAt(15.5);
        await SweepAt(16);
        await SweepAt(45);
        fixture.InstancesCreated.Should().Be(1, "a continuing outage is one excursion");
        fixture.Dispatches.Should().Be(1, "a continuing outage must not re-dispatch every sweep");

        fixture.LastReadingAt = T0.AddMinutes(46);
        await SweepAt(46);
        fixture.Closed.Should().BeEmpty("with hysteresis the first false evaluation only starts the window");

        await SweepAt(46.5);
        fixture.Closed.Should().ContainSingle()
            .Which.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        (await fixture.TrackerRepo.GetTrackerStateAsync(RuleId))!.State.Should().Be("idle");
        fixture.Dispatches.Should().Be(1);
    }

    private sealed class Fixture
    {
        public ManualTimeProvider Time { get; } = new();
        public InMemoryTrackerRepository TrackerRepo { get; }
        public AlertSweepService Sweep { get; }
        public DateTime? LastReadingAt { get; set; }
        public int InstancesCreated { get; private set; }
        public int Dispatches { get; private set; }
        public AlertPayload? LastPayload { get; private set; }
        public List<ExcursionTransition> Closed { get; } = [];

        public Fixture(bool useRustEngine)
        {
            var rule = new AlertRule
            {
                Id = RuleId,
                Name = "Signal loss",
                ConditionType = AlertConditionType.SignalLoss,
                ConditionParams = """{"timeout_minutes": 15}""",
                ConfirmationReadings = 1,
                HysteresisMinutes = 0,
            };
            var snapshot = new AlertRuleSnapshot(
                RuleId, Tenant, rule.Name, AlertConditionType.SignalLoss, rule.ConditionParams,
                AlertRuleSeverity.Warning, "{}", 0, false, null);

            TrackerRepo = new InMemoryTrackerRepository([rule]);
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
                .Setup(x => x.GetEnabledRulesByConditionTypeAsync(AlertConditionType.SignalLoss, It.IsAny<CancellationToken>()))
                .ReturnsAsync([snapshot]);
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
                .Setup(x => x.GetChannelsForRuleAsync(Tenant, RuleId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            services.AddSingleton(repository.Object);

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
                .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, Guid _, CancellationToken _) => c);
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
            services.AddScoped<IAlertOrchestrator, AlertOrchestrator>();

            Sweep = new AlertSweepService(
                services.BuildServiceProvider(), NullLogger<AlertSweepService>.Instance, Time);
        }
    }
}
