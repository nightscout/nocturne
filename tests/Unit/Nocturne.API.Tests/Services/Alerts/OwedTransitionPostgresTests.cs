using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Tests.Services.BackgroundServices;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A transition commits with the record of the work it owes, and a process that dies anywhere
/// between that commit and the last of the work loses no alert: <see cref="AlertSweepJob"/> finishes
/// it once its lease runs out, sending each channel once (docs/alerts/engine-semantics.md §7). On
/// PostgreSQL under the runtime role and Row Level Security, since it rests on transactions, an
/// advisory lock and a compare-and-set.
/// </summary>
[Trait("Category", "Integration")]
public class OwedTransitionPostgresTests(OwedTransitionPostgresTests.Database database)
    : IClassFixture<OwedTransitionPostgresTests.Database>
{
    private static readonly DateTime Opened = new(2026, 11, 22, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset AfterLease = Opened + AlertSweepJob.OwedTransitionLease + TimeSpan.FromSeconds(1);
    private const string HomeAssistant = "ha-instance";

    public sealed class Database() : MigratedDatabase("owed_transition");

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    /// <summary>
    /// One tenant with one enabled rule sending to one Home Assistant instance, and the pieces of
    /// one API process over the shared database: the transition writer, the opened and close
    /// handlers, and the sweep. Each send the instance receives is counted.
    /// </summary>
    private sealed class Tenant
    {
        public required Guid Id { get; init; }
        public required AlertRuleSnapshot Rule { get; init; }
        public required IReadOnlyList<AlertRuleChannelSnapshot> Channels { get; init; }
        public AlertRuleChannelSnapshot Channel => Channels[0];
        public required Func<NocturneDbContext> Context { get; init; }
        public int Sends;
        public Func<Task>? OnSend;
        public readonly Mock<IAlertAcknowledgementService> Acknowledgements = new();
        public readonly Mock<IAlertRuleRepository> Rules = new();
        public readonly Mock<ITenantMembershipRepository> Members = new();
        public readonly Mock<ISignalRBroadcastService> Broadcasts = new();
        public readonly Mock<IEngineAlarmRelay> Alarms = new();
        public readonly Mock<IInAppNotificationService> Notifications = new();
        public readonly FakeTimeProvider Time = new(Opened);
        public IReadOnlySet<DndScope> Dnd = new HashSet<DndScope>();
        public IReadOnlyList<SensorGlucose> Recent = [];

        public AlertRepository Repository() => new(new SuppliedContextFactory(Context));

        /// <summary>A repository that runs a hook at a chosen step, to interleave two processes.</summary>
        public HookedRepository Hooked() => new(new SuppliedContextFactory(Context));

        public IAlertAcknowledgementService RealAcknowledgements() => new AlertAcknowledgementService(
            new SuppliedContextFactory(Context), Mock.Of<ISignalRBroadcastService>(), Mock.Of<ITenantMemberService>(),
            Mock.Of<IEngineAlarmRelay>(), NullLogger<AlertAcknowledgementService>.Instance, Time);

        /// <summary>Gives <paramref name="instance"/> a snooze that lapsed at <paramref name="until"/>, or now.</summary>
        public async Task LapseSnoozeAsync(Guid instance, DateTime? until = null)
        {
            await using var db = Context();
            var row = await db.AlertInstances.SingleAsync(i => i.Id == instance);
            row.SnoozedUntil = until ?? Time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
        }

        public async Task SmartSnoozeAsync()
        {
            await using var db = Context();
            var row = await db.AlertRules.SingleAsync(r => r.Id == Rule.Id);
            row.ClientConfiguration = """{"snooze":{"smartSnooze":true}}""";
            await db.SaveChangesAsync();
        }

        public async Task UnsettleOpeningAsync(Guid excursion)
        {
            await using var db = Context();
            var row = await db.AlertExcursions.SingleAsync(e => e.Id == excursion);
            row.OpenedOwedAt = Opened;
            await db.SaveChangesAsync();
        }

        public AlertTrackerRepository Tracker() => new(new SuppliedContextFactory(Context));

        public IServiceProvider Process(AlertRepository? repository = null)
        {
            var hub = new Mock<IHubContext<HomeAssistantHub>>();
            var proxy = new Mock<IClientProxy>();
            proxy
                .Setup(p => p.SendCoreAsync("alert_dispatch", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    Interlocked.Increment(ref Sends);
                    if (OnSend is { } onSend)
                        await onSend();
                });
            hub.Setup(h => h.Clients.Group(It.IsAny<string>())).Returns(proxy.Object);
            var connections = new HomeAssistantConnections();
            foreach (var channel in Channels)
                connections.Add(Id, channel.Destination, $"connection-{channel.Destination}");

            var canonical = new Mock<ICanonicalGlucoseService>();
            canonical
                .Setup(c => c.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Recent);

            var root = new ServiceCollection()
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .AddSingleton<TimeProvider>(Time)
                .AddSingleton<INocturneDbContextFactory>(new SuppliedContextFactory(Context))
                .AddSingleton<IAlertRepository>(repository ?? Repository())
                .AddSingleton(Rules.Object)
                .AddSingleton(Acknowledgements.Object)
                .AddSingleton(Members.Object)
                .AddSingleton(Broadcasts.Object)
                .AddSingleton(Alarms.Object)
                .AddSingleton(Notifications.Object)
                .AddSingleton(hub.Object)
                .AddSingleton(connections)
                .AddSingleton<HomeAssistantProvider>()
                .AddSingleton<InAppProvider>()
                .AddScoped<IAlertDeliveryService, AlertDeliveryService>()
                .AddScoped<IExcursionOpenedHandler, ExcursionOpenedHandler>()
                .AddScoped<IExcursionResolutionHandler, ExcursionResolutionHandler>()
                .AddSingleton<IExcursionTracker>(new ExcursionTracker(
                    Tracker(), new AlertRuleEvaluationGate(), Time, NullLogger<ExcursionTracker>.Instance,
                    new RustExcursionDecider(new AlertEngineErrors(new TestMeterFactory(), Time))))
                .AddScoped<IAlertSnoozeService, AlertSnoozeService>()
                .AddSingleton(canonical.Object)
                .AddSingleton(EnricherOf())
                .AddSweepServices()
                .BuildServiceProvider();
            return TenantJobScope.Open(root, new TenantContext(Id, "slug", "Slug", true, IsDemo: false), "test").ServiceProvider;
        }

        /// <summary>The enricher, which gives the Do Not Disturb scopes in <see cref="Dnd"/> as they are at the call.</summary>
        private ISensorContextEnricher EnricherOf()
        {
            var enricher = new Mock<ISensorContextEnricher>();
            enricher
                .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) => c with { ActiveDndScopes = Dnd });
            return enricher.Object;
        }

        /// <summary>The alarm the tenant's alerting instances keep sounding, or null.</summary>
        public Task<EngineAlarm?> AlarmingAsync() =>
            new EngineAlarmRelay(
                    new SuppliedContextFactory(Context), SettableTenantAccessor.Resolved(Id),
                    Mock.Of<ITenantGlucoseUnits>(), Mock.Of<ICanonicalGlucoseService>(), Mock.Of<ISignalRBroadcastService>(),
                    Time, NullLogger<EngineAlarmRelay>.Instance)
                .HighestAlarmingAsync(CancellationToken.None);

        /// <summary>The evaluation's commit of an opening: excursion, tracker state and owed work.</summary>
        public async Task<Guid> OpenAsync()
        {
            var (transition, _) = await ExcursionTransitionWriter.ApplyAsync(
                Tracker(), NullLogger.Instance, Rule.Id, null,
                new TrackerDecision(ExcursionTransitionType.ExcursionOpened, null,
                    new TrackerPostState(TrackerPostState.Active, true, Opened, null)),
                null, Opened, CancellationToken.None);
            return transition.ExcursionId!.Value;
        }

        /// <summary>The evaluation's commit of a close: excursion end, tracker state and owed work.</summary>
        public async Task<ExcursionTransition> CloseAsync(DateTime at)
        {
            var tracker = Tracker();
            var prior = await tracker.GetTrackerStateAsync(Rule.Id);
            var (transition, _) = await ExcursionTransitionWriter.ApplyAsync(
                tracker, NullLogger.Instance, Rule.Id, prior,
                new TrackerDecision(ExcursionTransitionType.ExcursionClosed, ExcursionCloseReason.Hysteresis,
                    new TrackerPostState(TrackerPostState.Idle, false, at, null)),
                null, at, CancellationToken.None);
            return transition;
        }

        public Task HandleOpenedAsync(IServiceProvider process, Guid excursion) =>
            process.GetRequiredService<IExcursionOpenedHandler>().HandleOpenedAsync(
                Rule, excursion, Opened, new SensorContext { LatestValue = 52, LatestTimestamp = Opened, TrendRate = null, LastReadingAt = Opened },
                Id, late: false, metBy: [], callerCloses: false, CancellationToken.None);

        public Task SweepAsync(IServiceProvider process, DateTimeOffset at) =>
            new AlertSweepJob(NullLogger<AlertSweepJob>.Instance).RunAsync(process, Id, at);

        public async Task<(DateTime? Opened, DateTime? Closed)> OwedAsync(Guid excursion)
        {
            await using var db = Context();
            var row = await db.AlertExcursions.AsNoTracking().SingleAsync(e => e.Id == excursion);
            return (row.OpenedOwedAt, row.ClosedOwedAt);
        }

        public async Task<List<AlertInstanceEntity>> InstancesAsync(Guid excursion)
        {
            await using var db = Context();
            return await db.AlertInstances.AsNoTracking().Where(i => i.AlertExcursionId == excursion).ToListAsync();
        }

        public async Task<List<AlertDeliveryEntity>> DeliveriesAsync(Guid excursion)
        {
            await using var db = Context();
            return await db.AlertDeliveries.AsNoTracking()
                .Where(d => db.AlertInstances.Any(i => i.Id == d.AlertInstanceId && i.AlertExcursionId == excursion))
                .ToListAsync();
        }
    }

    private sealed class HookedRepository(INocturneDbContextFactory contexts) : AlertRepository(contexts)
    {
        public Func<Task>? AfterRead;
        public Func<Task>? BeforeEnsure;
        public Func<Task>? AfterEnsure;
        public Func<Task>? BeforeOweClose;
        public Func<Task>? AfterOweClose;
        public Func<Task>? AfterEndNotice;
        public Func<Task>? AfterResolveInstances;
        public Func<Task>? BeforeEndLapsedSnooze;
        public Func<Task>? AfterEndLapsedSnooze;
        public Func<Task>? BeforeSettle;
        public Func<Task>? AfterSnoozedRead;

        public override async Task SettleTransitionAsync(
            Guid excursionId, ExcursionTransitionType kind, DateTime owedAt, CancellationToken ct)
        {
            if (BeforeSettle is { } before)
                await before();
            await base.SettleTransitionAsync(excursionId, kind, owedAt, ct);
        }

        public override async Task<IReadOnlyList<SnoozedInstanceSnapshot>> GetExpiredSnoozedInstancesAsync(
            DateTime asOf, CancellationToken ct)
        {
            var snoozed = await base.GetExpiredSnoozedInstancesAsync(asOf, ct);
            if (snoozed.Count > 0 && AfterSnoozedRead is { } after)
                await after();
            return snoozed;
        }

        public override async Task<IReadOnlyList<OwedTransition>> GetOwedTransitionsAsync(DateTime takenUpBefore, CancellationToken ct)
        {
            var owed = await base.GetOwedTransitionsAsync(takenUpBefore, ct);
            if (owed.Count > 0 && AfterRead is { } hook)
                await hook();
            return owed;
        }

        public override async Task<OpenedInstance?> EnsureInstanceAsync(
            Guid excursionId, DateTime triggeredAt, bool whileOpen, CancellationToken ct)
        {
            if (BeforeEnsure is { } before)
                await before();
            var instance = await base.EnsureInstanceAsync(excursionId, triggeredAt, whileOpen, ct);
            if (AfterEnsure is { } after)
                await after();
            return instance;
        }

        public override async Task<bool> OweCloseIfEndedAsync(Guid excursionId, DateTime now, CancellationToken ct)
        {
            if (BeforeOweClose is { } before)
                await before();
            var owed = await base.OweCloseIfEndedAsync(excursionId, now, ct);
            if (owed && AfterOweClose is { } after)
                await after();
            return owed;
        }

        public override async Task EndNoticeAsync(Guid excursionId, DateTime at, CancellationToken ct)
        {
            await base.EndNoticeAsync(excursionId, at, ct);
            if (AfterEndNotice is { } after)
                await after();
        }

        public override async Task<LapsedSnoozeEnd> EndLapsedSnoozeAsync(
            Guid instanceId, DateTime lapsedUntil, DateTime? renotifyAt, CancellationToken ct)
        {
            if (BeforeEndLapsedSnooze is { } before)
                await before();
            var end = await base.EndLapsedSnoozeAsync(instanceId, lapsedUntil, renotifyAt, ct);
            if (AfterEndLapsedSnooze is { } after)
                await after();
            return end;
        }

        public override async Task ResolveInstancesForExcursionAsync(
            Guid excursionId, DateTime resolvedAt, string? resolutionReason, CancellationToken ct)
        {
            await base.ResolveInstancesForExcursionAsync(excursionId, resolvedAt, resolutionReason, ct);
            if (AfterResolveInstances is { } after)
                await after();
        }
    }

    private async Task<Tenant> SeedAsync(AlertRuleSeverity severity = AlertRuleSeverity.Critical, int channels = 1)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await conn.SeedTenantAsync(tenant, $"owed-{tenant:N}", "owed-test");
        }

        var rule = Guid.CreateVersion7();
        var channelSnapshots = Enumerable.Range(0, channels)
            .Select(n => new AlertRuleChannelSnapshot(
                Guid.CreateVersion7(), rule, ChannelType.HomeAssistant, n == 0 ? HomeAssistant : $"{HomeAssistant}-{n}", null, n))
            .ToList();
        await using (var seed = Context(tenant))
        {
            seed.AlertRules.Add(new AlertRuleEntity
            {
                Id = rule,
                TenantId = tenant,
                Name = "Urgent Low",
                ConditionType = AlertConditionType.Threshold,
                ConditionParams = """{"direction":"below","value":55}""",
                Severity = severity,
                ScopeClass = RuleScopeClass.Low,
                IsEnabled = true,
                CreatedAt = Opened,
                UpdatedAt = Opened,
            });
            seed.AlertRuleChannels.AddRange(channelSnapshots.Select(c => new AlertRuleChannelEntity
            {
                Id = c.Id,
                TenantId = tenant,
                AlertRuleId = rule,
                ChannelType = ChannelType.HomeAssistant,
                Destination = c.Destination,
                SortOrder = c.SortOrder,
            }));
            await seed.SaveChangesAsync();
        }

        var snapshot = new AlertRuleSnapshot(
            rule, tenant, "Urgent Low", AlertConditionType.Threshold, """{"direction":"below","value":55}""",
            severity, "{}", 0, false, null, ScopeClass: RuleScopeClass.Low);
        var seeded = new Tenant
        {
            Id = tenant,
            Rule = snapshot,
            Channels = channelSnapshots,
            Context = () => Context(tenant),
        };
        seeded.Rules
            .Setup(r => r.GetChannelsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<AlertRuleChannelSnapshot>> { [rule] = channelSnapshots });
        return seeded;
    }

    [Fact]
    public async Task An_opening_commits_with_the_work_it_owes_and_a_finished_one_owes_nothing()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();

        (await tenant.OwedAsync(excursion)).Opened.Should().Be(Opened, "the work is owed from the commit that opens it");

        var process = tenant.Process();
        await tenant.HandleOpenedAsync(process, excursion);
        await tenant.SweepAsync(process, AfterLease);

        tenant.Sends.Should().Be(1);
        (await tenant.OwedAsync(excursion)).Opened.Should().BeNull();
        (await tenant.DeliveriesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("delivered");
    }

    /// <summary>Dies inside the opening's transaction, after the excursion row and before the state.</summary>
    private sealed class DiesBeforeTheStateRepository(NocturneDbContext context)
        : AlertTrackerRepository(DbUnitOfWork.Own(context))
    {
        public override Task UpsertTrackerStateAsync(AlertTrackerState state, CancellationToken ct = default) =>
            throw new InvalidOperationException("the process dies before the tracker state is written");
    }

    [Fact]
    public async Task An_opening_that_cannot_commit_its_state_leaves_no_excursion_owing_work()
    {
        var tenant = await SeedAsync();
        await using var context = tenant.Context();

        var open = () => ExcursionTransitionWriter.ApplyAsync(
            new DiesBeforeTheStateRepository(context), NullLogger.Instance, tenant.Rule.Id, null,
            new TrackerDecision(ExcursionTransitionType.ExcursionOpened, null,
                new TrackerPostState(TrackerPostState.Active, true, Opened, null)),
            null, Opened, CancellationToken.None);

        await open.Should().ThrowAsync<InvalidOperationException>();
        await using var db = tenant.Context();
        (await db.AlertExcursions.CountAsync(e => e.AlertRuleId == tenant.Rule.Id))
            .Should().Be(0, "the excursion and the work it owes roll back with the state");
    }

    [Fact]
    public async Task A_death_after_the_commit_and_before_the_instance_is_dispatched_by_the_sweep()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        var process = tenant.Process();

        await tenant.SweepAsync(process, Opened.AddSeconds(30));
        tenant.Sends.Should().Be(0, "the sweep leaves an opening alone while its taker may still be on it");

        await tenant.SweepAsync(process, AfterLease);

        tenant.Sends.Should().Be(1);
        (await tenant.InstancesAsync(excursion)).Should().ContainSingle();
        (await tenant.DeliveriesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("delivered");
        (await tenant.OwedAsync(excursion)).Opened.Should().BeNull();
    }

    [Fact]
    public async Task A_death_after_the_instance_and_before_the_dispatch_reuses_the_instance()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        var process = tenant.Process();
        tenant.Rules
            .SetupSequence(r => r.GetChannelsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NpgsqlException("the process dies after the instance"))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<AlertRuleChannelSnapshot>> { [tenant.Rule.Id] = [tenant.Channel] });

        await tenant.Invoking(t => t.HandleOpenedAsync(process, excursion)).Should().ThrowAsync<NpgsqlException>();
        (await tenant.InstancesAsync(excursion)).Should().ContainSingle();

        await tenant.SweepAsync(process, AfterLease);

        tenant.Sends.Should().Be(1);
        (await tenant.InstancesAsync(excursion)).Should().ContainSingle("the run that finishes the opening raises no second instance");
        (await tenant.OwedAsync(excursion)).Opened.Should().BeNull();
    }

    [Fact]
    public async Task A_death_after_the_delivery_row_and_before_the_send_sends_the_pending_row()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        var process = tenant.Process();
        using var dies = new CancellationTokenSource();
        tenant.OnSend = () =>
        {
            tenant.OnSend = null;
            dies.Cancel();
            throw new OperationCanceledException(dies.Token);
        };

        var dying = process.GetRequiredService<IExcursionOpenedHandler>().HandleOpenedAsync(
            tenant.Rule, excursion, Opened, new SensorContext { LatestValue = 52, LatestTimestamp = Opened, TrendRate = null, LastReadingAt = Opened },
            tenant.Id, late: false, metBy: [], callerCloses: false, dies.Token);
        await dying.Invoking(async t => await t).Should().ThrowAsync<OperationCanceledException>();
        (await tenant.DeliveriesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("pending");

        await tenant.SweepAsync(process, AfterLease);

        tenant.Sends.Should().Be(2, "the send the dying process made never reached the instance, and the sweep's does");
        (await tenant.DeliveriesAsync(excursion)).Should().ContainSingle("the pending row is sent, not a second one written")
            .Which.Status.Should().Be("delivered");
        (await tenant.OwedAsync(excursion)).Opened.Should().BeNull();
    }

    [Fact]
    public async Task A_channel_whose_provider_refused_is_not_sent_again()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        var instance = (await tenant.Repository().EnsureInstanceAsync(excursion, Opened, whileOpen: true, CancellationToken.None))!;
        await using (var db = tenant.Context())
        {
            db.AlertDeliveries.Add(new AlertDeliveryEntity
            {
                Id = Guid.CreateVersion7(), TenantId = tenant.Id, AlertInstanceId = instance.Id,
                AlertRuleChannelId = tenant.Channel.Id, ChannelType = ChannelType.HomeAssistant,
                Destination = HomeAssistant, Status = "failed", LastError = "No Home Assistant instance connected",
                CreatedAt = Opened,
            });
            await db.SaveChangesAsync();
        }

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        tenant.Sends.Should().Be(0, "a refusal is an answer, and this is not a retry policy");
        (await tenant.DeliveriesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("failed");
        (await tenant.OwedAsync(excursion)).Opened.Should().BeNull();
    }

    [Fact]
    public async Task A_death_after_the_send_and_before_the_auto_acknowledge_acknowledges_without_sending_again()
    {
        var tenant = await SeedAsync(AlertRuleSeverity.Info);
        var excursion = await tenant.OpenAsync();
        var process = tenant.Process();
        tenant.Acknowledgements
            .SetupSequence(a => a.AcknowledgeExcursionAsync(
                tenant.Id, excursion, It.IsAny<string>(), AlertAcknowledgementAuthority.System, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NpgsqlException("the process dies before the acknowledgement"))
            .ReturnsAsync(AlertAcknowledgementOutcome.Acknowledged);

        await tenant.Invoking(t => t.HandleOpenedAsync(process, excursion)).Should().ThrowAsync<NpgsqlException>();
        await tenant.SweepAsync(process, AfterLease);

        tenant.Sends.Should().Be(1, "the channel was delivered before the death");
        tenant.Acknowledgements.Verify(a => a.AcknowledgeExcursionAsync(
            tenant.Id, excursion, It.IsAny<string>(), AlertAcknowledgementAuthority.System, false, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        (await tenant.OwedAsync(excursion)).Opened.Should().BeNull();
    }

    [Fact]
    public async Task A_death_after_a_close_commits_is_resolved_by_the_sweep()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        var process = tenant.Process();
        await tenant.HandleOpenedAsync(process, excursion);

        var close = await tenant.CloseAsync(Opened.AddMinutes(1));
        close.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        (await tenant.OwedAsync(excursion)).Closed.Should().NotBeNull("the close commits with the work it owes");

        await tenant.SweepAsync(process, AfterLease.AddMinutes(1));

        (await tenant.InstancesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("resolved");
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
        tenant.Sends.Should().Be(1);
    }

    [Fact]
    public async Task An_opening_whose_excursion_has_since_closed_is_not_sent()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        await tenant.CloseAsync(Opened.AddMinutes(1));

        await tenant.SweepAsync(tenant.Process(), AfterLease.AddHours(3));

        tenant.Sends.Should().Be(0, "an alert for a condition that ended misleads");
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task An_excursion_written_before_the_owed_columns_owes_nothing()
    {
        var tenant = await SeedAsync();
        var excursion = Guid.CreateVersion7();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await conn.PinTenantAsync(tenant.Id);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO alert_excursions (id, tenant_id, alert_rule_id, started_at)
                VALUES (@id, @tenant, @rule, @at);
                INSERT INTO alert_excursions (id, tenant_id, alert_rule_id, started_at, ended_at)
                VALUES (@ended, @tenant, @rule, @at, @at);
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", excursion));
            cmd.Parameters.Add(new NpgsqlParameter("@ended", Guid.CreateVersion7()));
            cmd.Parameters.Add(new NpgsqlParameter("@tenant", tenant.Id));
            cmd.Parameters.Add(new NpgsqlParameter("@rule", tenant.Rule.Id));
            cmd.Parameters.Add(new NpgsqlParameter("@at", Opened.AddDays(-3)));
            await cmd.ExecuteNonQueryAsync();
        }

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        tenant.Sends.Should().Be(0, "an upgrade must not send every alert opened before it");
        (await tenant.InstancesAsync(excursion)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_owed_pass_leaves_a_standing_notice_to_its_own_check_and_closes_a_resolved_one_without_a_send()
    {
        var tenant = await SeedAsync();
        var repository = tenant.Repository();
        const string version = "0000000000000000000000000000000000000000000000000000000000000001";
        var notice = (await repository.RecordUnevaluableNoticeAsync(tenant.Rule, version, Opened, CancellationToken.None))!;

        (await repository.GetOwedTransitionsAsync(AfterLease.UtcDateTime, CancellationToken.None))
            .Should().BeEmpty("a standing notice owes its dispatch to IUnevaluableRuleNotice, which builds its own payload");

        await repository.EndNoticeAsync(notice.ExcursionId, Opened.AddMinutes(1), CancellationToken.None);
        (await repository.GetOwedTransitionsAsync(AfterLease.UtcDateTime, CancellationToken.None))
            .Should().BeEmpty("an ended notice stands until its resolve resolves its instance, with the reason only the resolve has");

        await repository.ResolveInstancesForExcursionAsync(
            notice.ExcursionId, Opened.AddMinutes(1), ExcursionCloseReason.Manual.ToWireString(), CancellationToken.None);
        (await tenant.OwedAsync(notice.ExcursionId)).Opened.Should().NotBeNull(
            "a dispatch the resolve may have run through owes the resolve again once it returns");

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        tenant.Sends.Should().Be(0, "a resolved notice is never sent");
        (await tenant.InstancesAsync(notice.ExcursionId)).Should().ContainSingle()
            .Which.ResolutionReason.Should().Be(ExcursionCloseReason.Manual.ToWireString());
        (await tenant.OwedAsync(notice.ExcursionId)).Should().Be((null, null));
    }

    [Fact]
    public async Task An_owed_urgent_low_is_sent_although_an_acknowledge_all_for_another_alarm_reached_it()
    {
        var tenant = await SeedAsync();
        var low = await tenant.OpenAsync();
        await using (var db = tenant.Context())
        {
            var high = Guid.CreateVersion7();
            db.AlertRules.Add(new AlertRuleEntity
            {
                Id = high, TenantId = tenant.Id, Name = "High", ConditionType = AlertConditionType.Threshold,
                ConditionParams = """{"direction":"above","value":250}""", Severity = AlertRuleSeverity.Warning,
                IsEnabled = true, CreatedAt = Opened, UpdatedAt = Opened,
            });
            db.AlertExcursions.Add(new AlertExcursionEntity
            {
                Id = Guid.CreateVersion7(), TenantId = tenant.Id, AlertRuleId = high, StartedAt = Opened.AddMinutes(-30),
            });
            await db.SaveChangesAsync();
        }

        await tenant.RealAcknowledgements().AcknowledgeAllAsync(tenant.Id, "caregiver silencing the high", CancellationToken.None);
        await using (var db = tenant.Context())
            (await db.AlertExcursions.SingleAsync(e => e.Id == low)).AcknowledgedAt.Should().NotBeNull();

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        tenant.Sends.Should().Be(1, "an acknowledgement made for another alarm must not consume an alert nobody received");
        (await tenant.OwedAsync(low)).Opened.Should().BeNull();
    }

    [Fact]
    public async Task A_rerun_of_a_fully_dispatched_auto_acknowledged_info_opening_sends_nothing_and_settles()
    {
        var tenant = await SeedAsync(AlertRuleSeverity.Info);
        var excursion = await tenant.OpenAsync();
        var process = tenant.Process();
        await tenant.HandleOpenedAsync(process, excursion);
        await tenant.UnsettleOpeningAsync(excursion);

        await tenant.SweepAsync(process, AfterLease);

        tenant.Sends.Should().Be(1, "the channel was delivered; a re-run sends only what is still owed");
        (await tenant.DeliveriesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("delivered");
        tenant.Acknowledgements.Verify(a => a.AcknowledgeExcursionAsync(
            tenant.Id, excursion, It.IsAny<string>(), AlertAcknowledgementAuthority.System, false, It.IsAny<CancellationToken>()),
            Times.Exactly(2), "acknowledging an acknowledged excursion is a no-op, so the re-run may ask again");
        (await tenant.OwedAsync(excursion)).Opened.Should().BeNull();
    }

    [Fact]
    public async Task Two_take_ups_with_the_same_seen_value_give_exactly_one_true_for_either_kind()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        var closedAt = Opened.AddMinutes(1);
        await tenant.CloseAsync(closedAt);
        var (opened, closed) = await tenant.OwedAsync(excursion);

        foreach (var (kind, seen) in new[]
                 {
                     (ExcursionTransitionType.ExcursionOpened, opened!.Value),
                     (ExcursionTransitionType.ExcursionClosed, closed!.Value),
                 })
        {
            var taken = await Task.WhenAll(
                tenant.Repository().TakeUpOwedTransitionAsync(excursion, kind, seen, AfterLease.UtcDateTime, CancellationToken.None),
                tenant.Repository().TakeUpOwedTransitionAsync(excursion, kind, seen, AfterLease.UtcDateTime.AddSeconds(1), CancellationToken.None));

            taken.Count(t => t).Should().Be(1, $"one taker wins the {kind}");
        }
    }

    [Fact]
    public async Task An_opening_a_sweep_took_up_while_its_evaluation_settled_stays_owed_to_the_sweep()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        var dispatching = tenant.Hooked();
        dispatching.BeforeSettle = async () =>
        {
            dispatching.BeforeSettle = null;
            (await tenant.Repository().TakeUpOwedTransitionAsync(
                excursion, ExcursionTransitionType.ExcursionOpened, Opened, AfterLease.UtcDateTime, CancellationToken.None))
                .Should().BeTrue();
        };

        await tenant.HandleOpenedAsync(tenant.Process(dispatching), excursion);

        (await tenant.OwedAsync(excursion)).Should().Be(((DateTime?)AfterLease.UtcDateTime, (DateTime?)null),
            "the evaluation settles only the stamp it set, and the sweep now holds the opening");
        await tenant.Repository().SettleTransitionAsync(
            excursion, ExcursionTransitionType.ExcursionOpened, AfterLease.UtcDateTime, CancellationToken.None);
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task An_opening_a_sweep_took_up_while_a_resume_that_held_it_settled_stays_owed_to_the_sweep()
    {
        var (tenant, excursion, instance) = await OpenedAndLapsedAsync();
        var renotifiedAt = tenant.Time.GetUtcNow().UtcDateTime;
        var takenUpAt = renotifiedAt + AlertSweepJob.OwedTransitionLease + TimeSpan.FromSeconds(1);
        var resuming = tenant.Hooked();
        resuming.BeforeSettle = async () =>
        {
            resuming.BeforeSettle = null;
            (await tenant.Repository().TakeUpOwedTransitionAsync(
                excursion, ExcursionTransitionType.ExcursionOpened, renotifiedAt, takenUpAt, CancellationToken.None))
                .Should().BeTrue();
        };

        (await ResumeAsync(tenant.Process(resuming), instance)).Should().BeTrue();

        (await tenant.OwedAsync(excursion)).Should().Be(((DateTime?)takenUpAt, (DateTime?)null),
            "the resume settles only the opening it held");
    }

    [Fact]
    public async Task A_close_owed_again_while_its_first_run_settled_stays_owed()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        await tenant.HandleOpenedAsync(tenant.Process(), excursion);
        var close = await tenant.CloseAsync(Opened.AddMinutes(1));
        var owedAgainAt = Opened.AddMinutes(2);
        var closing = tenant.Hooked();
        closing.BeforeSettle = async () =>
        {
            closing.BeforeSettle = null;
            (await tenant.Repository().OweCloseIfEndedAsync(excursion, owedAgainAt, CancellationToken.None)).Should().BeTrue();
        };

        await tenant.Process(closing).GetRequiredService<IExcursionResolutionHandler>()
            .HandleClosedAsync(close, tenant.Id, CancellationToken.None);

        (await tenant.OwedAsync(excursion)).Should().Be(((DateTime?)null, (DateTime?)owedAgainAt),
            "a send that ran during the close owes the close again, and the first run settles only its own");
    }

    /// <summary>
    /// Readings that rise out of the rule's low over the ten minutes to the clock, which the
    /// trend gate reads as recovering, so the snooze pass extends a lapsed snooze.
    /// </summary>
    private static async Task<(Tenant Tenant, Guid Excursion, Guid Instance)> RecoveringAndLapsedAsync(
        Func<Task<(Tenant Tenant, Guid Excursion, Guid Instance)>> opened)
    {
        var (tenant, excursion, instance) = await opened();
        await tenant.SmartSnoozeAsync();
        var now = tenant.Time.GetUtcNow().UtcDateTime;
        tenant.Recent =
        [
            new SensorGlucose { Id = Guid.NewGuid(), Timestamp = now.AddMinutes(-1), Mgdl = 52 },
            new SensorGlucose { Id = Guid.NewGuid(), Timestamp = now.AddMinutes(-6), Mgdl = 45 },
            new SensorGlucose { Id = Guid.NewGuid(), Timestamp = now.AddMinutes(-11), Mgdl = 40 },
        ];
        return (tenant, excursion, instance);
    }

    [Fact]
    public async Task A_lapsed_snooze_on_a_recovering_low_is_extended()
    {
        var (tenant, excursion, _) = await RecoveringAndLapsedAsync(() => OpenedAndLapsedAsync());
        var now = tenant.Time.GetUtcNow();

        await tenant.SweepAsync(tenant.Process(), now);

        var row = (await tenant.InstancesAsync(excursion)).Single();
        (row.SnoozedUntil, row.SnoozeCount).Should().Be(
            ((DateTime?)now.UtcDateTime.AddMinutes(SmartSnoozeConfig.DefaultExtendMinutes), 1));
        tenant.Sends.Should().Be(1, "an extended snooze keeps the alert silent");
    }

    [Fact]
    public async Task An_extension_that_lands_after_a_resume_ended_the_snooze_writes_nothing()
    {
        var (tenant, excursion, instance) = await RecoveringAndLapsedAsync(() => OpenedAndLapsedAsync());
        var sweeping = tenant.Hooked();
        sweeping.AfterSnoozedRead = async () =>
        {
            sweeping.AfterSnoozedRead = null;
            (await ResumeAsync(tenant.Process(), instance)).Should().BeTrue();
        };

        await tenant.SweepAsync(tenant.Process(sweeping), tenant.Time.GetUtcNow());

        var row = (await tenant.InstancesAsync(excursion)).Single();
        (row.SnoozedUntil, row.SnoozeCount).Should().Be(((DateTime?)null, 0),
            "the resume ended the lapse the sweep read, so the sweep does not snooze the alert it re-notified");
        tenant.Sends.Should().Be(2, "the opening and the resume's re-notification");
    }

    [Fact]
    public async Task A_second_sweep_that_read_the_opening_before_the_first_took_it_up_does_not_send_it()
    {
        var tenant = await SeedAsync();
        await tenant.OpenAsync();
        var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = tenant.Hooked();
        first.AfterRead = () => secondRead.Task;
        var second = tenant.Hooked();
        second.AfterRead = () =>
        {
            secondRead.TrySetResult();
            return firstSending.Task;
        };
        tenant.OnSend = () =>
        {
            tenant.OnSend = null;
            firstSending.TrySetResult();
            return secondDone.Task;
        };

        var firstSweep = tenant.SweepAsync(tenant.Process(first), AfterLease);
        var secondSweep = tenant.SweepAsync(tenant.Process(second), AfterLease);
        await secondSweep.ContinueWith(_ => secondDone.TrySetResult(), TaskScheduler.Default).WaitAsync(Eventually.DefaultPatience);
        await Task.WhenAll(firstSweep, secondSweep).WaitAsync(Eventually.DefaultPatience);

        tenant.Sends.Should().Be(1, "the second sweep's take-up compares the value it read, which the first changed");
    }

    [Fact]
    public async Task A_close_that_lands_before_a_late_opening_takes_its_instance_leaves_nothing_sent()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        var process = tenant.Process();
        var sweeping = tenant.Hooked();
        sweeping.BeforeEnsure = async () =>
        {
            sweeping.BeforeEnsure = null;
            var close = await tenant.CloseAsync(Opened.AddMinutes(3));
            await process.GetRequiredService<IExcursionResolutionHandler>().HandleClosedAsync(close, tenant.Id, CancellationToken.None);
        };

        await tenant.SweepAsync(tenant.Process(sweeping), AfterLease);

        tenant.Sends.Should().Be(0);
        (await tenant.InstancesAsync(excursion)).Should().BeEmpty("no instance is raised that its close would never resolve");
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task A_close_that_lands_after_a_late_opening_took_its_instance_and_before_the_send_leaves_nothing_sent()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        var process = tenant.Process();
        var sweeping = tenant.Hooked();
        sweeping.AfterEnsure = async () =>
        {
            sweeping.AfterEnsure = null;
            var close = await tenant.CloseAsync(Opened.AddMinutes(3));
            await process.GetRequiredService<IExcursionResolutionHandler>().HandleClosedAsync(close, tenant.Id, CancellationToken.None);
        };

        await tenant.SweepAsync(tenant.Process(sweeping), AfterLease);

        tenant.Sends.Should().Be(0, "the close resolved the instance before the dispatch read it");
        (await tenant.InstancesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("resolved");
        (await tenant.DeliveriesAsync(excursion)).Should().BeEmpty();
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    /// <summary>
    /// Adds an in-app channel to the rule, and returns the alarm and notification events in the
    /// order the process makes them.
    /// </summary>
    /// <param name="afterNotification">Runs once the notification is made, inside its send.</param>
    private static async Task<ConcurrentQueue<string>> RecordAlarmsAndNotificationsAsync(
        Tenant tenant, string notificationType = InAppProvider.NotificationType, Action? afterNotification = null)
    {
        var member = Guid.CreateVersion7();
        var inApp = new AlertRuleChannelSnapshot(
            Guid.CreateVersion7(), tenant.Rule.Id, ChannelType.InApp, member.ToString(), null, 1);
        await using (var db = tenant.Context())
        {
            db.AlertRuleChannels.Add(new AlertRuleChannelEntity
            {
                Id = inApp.Id, TenantId = tenant.Id, AlertRuleId = tenant.Rule.Id,
                ChannelType = ChannelType.InApp, Destination = inApp.Destination, SortOrder = 1,
            });
            await db.SaveChangesAsync();
        }
        tenant.Rules
            .Setup(r => r.GetChannelsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<AlertRuleChannelSnapshot>> { [tenant.Rule.Id] = [tenant.Channel, inApp] });
        tenant.Members
            .Setup(m => m.ListLivePeopleAsync(tenant.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new TenantMemberRecord(
                    Id: Guid.CreateVersion7(), TenantId: tenant.Id, SubjectId: member, SubjectName: "Member",
                    SubjectUsername: null, SubjectIsActive: true, IsSystemSubject: false, IsDemoSubject: false,
                    IsPlatformAdmin: false, HasConfirmedEmail: true, Roles: [], DirectPermissions: ["*"],
                    Label: null, Username: null, LimitTo24Hours: false, AccessEndsAt: null, LastUsedAt: null,
                    CreatedFromInviteId: null, SysCreatedAt: Opened, Reasons: []),
            ]);

        var events = new ConcurrentQueue<string>();
        tenant.Alarms
            .Setup(a => a.RaiseAsync(It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
            .Callback(() => events.Enqueue("raised"))
            .Returns(Task.CompletedTask);
        tenant.Alarms
            .Setup(a => a.ClearIfQuietAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .Callback(() => events.Enqueue("cleared"))
            .Returns(Task.CompletedTask);
        tenant.Notifications
            .Setup(n => n.CreateNotificationAsync(
                member.ToString(), notificationType, It.IsAny<string>(), It.IsAny<NotificationCategory?>(),
                It.IsAny<NotificationUrgency?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                events.Enqueue("notified");
                afterNotification?.Invoke();
            })
            .ReturnsAsync(new InAppNotificationDto());
        tenant.Notifications
            .Setup(n => n.ArchiveBySourceAsync(
                member.ToString(), notificationType, It.IsAny<string>(),
                It.IsAny<NotificationArchiveReason>(), It.IsAny<CancellationToken>()))
            .Callback(() => events.Enqueue("archived"))
            .ReturnsAsync(true);

        return events;
    }

    /// <summary>Lands a close of the excursion at the dispatch's broadcast, once.</summary>
    private static void CloseAtTheDispatch(Tenant tenant, IServiceProvider process)
    {
        var closing = true;
        tenant.Broadcasts
            .Setup(b => b.BroadcastAlertEventAsync("alert_dispatch", It.IsAny<object>()))
            .Returns(async () =>
            {
                if (!closing) return;
                closing = false;
                var close = await tenant.CloseAsync(Opened.AddMinutes(3));
                await process.GetRequiredService<IExcursionResolutionHandler>().HandleClosedAsync(close, tenant.Id, CancellationToken.None);
            });
    }

    /// <summary>
    /// The close lands at the dispatch's broadcast, after the instance is taken and before the alarm,
    /// the delivery rows and the notification, so it resolves, archives and clears only what existed
    /// before them.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_close_that_lands_while_an_opening_sends_leaves_nothing_standing(bool late)
    {
        var tenant = await SeedAsync();
        var events = await RecordAlarmsAndNotificationsAsync(tenant);
        var excursion = await tenant.OpenAsync();
        var process = tenant.Process();
        CloseAtTheDispatch(tenant, process);

        if (late)
            await tenant.SweepAsync(process, AfterLease);
        else
            await tenant.HandleOpenedAsync(process, excursion);

        events.Take(2).Should().Equal(["cleared", "raised"], "the close cleared the alarm before the dispatch raised it");
        events.Last(e => e is "raised" or "cleared").Should().Be("cleared", "no alarm stays raised for a condition that ended");
        events.Last(e => e is "notified" or "archived").Should().Be("archived", "no notification stays up for a condition that ended");
        (await tenant.InstancesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("resolved");
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task A_death_after_an_opening_sent_through_a_close_is_finished_by_the_sweep()
    {
        var tenant = await SeedAsync();
        var events = await RecordAlarmsAndNotificationsAsync(tenant);
        var excursion = await tenant.OpenAsync();
        var dying = tenant.Hooked();
        dying.BeforeOweClose = () => throw new NpgsqlException("the process dies after the sends");
        var process = tenant.Process(dying);
        CloseAtTheDispatch(tenant, process);

        await tenant.Invoking(t => t.HandleOpenedAsync(process, excursion)).Should().ThrowAsync<NpgsqlException>();
        events.Last(e => e is "raised" or "cleared").Should().Be("raised", "the run died with its alarm standing");

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        events.Last(e => e is "raised" or "cleared").Should().Be("cleared");
        events.Last(e => e is "notified" or "archived").Should().Be("archived");
        (await tenant.InstancesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("resolved");
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    private static Task<bool> ResumeAsync(IServiceProvider process, Guid instance) =>
        process.GetRequiredService<IAlertSnoozeService>().ResumeAsync(instance, CancellationToken.None);

    /// <summary>
    /// The snooze has lapsed with the alert still standing, and the close lands at the
    /// re-notification's broadcast, before its alarm, its delivery rows and its notification.
    /// </summary>
    [Fact]
    public async Task A_close_that_lands_while_a_snooze_re_notifies_leaves_nothing_standing()
    {
        var tenant = await SeedAsync();
        var events = await RecordAlarmsAndNotificationsAsync(tenant);
        var excursion = await tenant.OpenAsync();
        var process = tenant.Process();
        await tenant.HandleOpenedAsync(process, excursion);
        var instance = (await tenant.InstancesAsync(excursion)).Single().Id;
        await tenant.LapseSnoozeAsync(instance);
        CloseAtTheDispatch(tenant, process);

        (await ResumeAsync(process, instance)).Should().BeTrue();

        events.Count(e => e == "notified").Should().Be(2, "the re-notification was sent after the close");
        events.Last(e => e is "raised" or "cleared").Should().Be("cleared", "no alarm stays raised for a condition that ended");
        events.Last(e => e is "notified" or "archived").Should().Be("archived", "no notification stays up for a condition that ended");
        (await tenant.InstancesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("resolved");
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task A_death_after_a_snooze_re_notified_through_a_close_owes_the_close_and_the_sweep_finishes_it()
    {
        var tenant = await SeedAsync();
        var events = await RecordAlarmsAndNotificationsAsync(tenant);
        var excursion = await tenant.OpenAsync();
        await tenant.HandleOpenedAsync(tenant.Process(), excursion);
        var instance = (await tenant.InstancesAsync(excursion)).Single().Id;
        await tenant.LapseSnoozeAsync(instance);
        var dying = tenant.Hooked();
        dying.AfterOweClose = () => throw new NpgsqlException("the process dies once the close is owed again");
        var process = tenant.Process(dying);
        CloseAtTheDispatch(tenant, process);

        await process.Invoking(p => ResumeAsync(p, instance)).Should().ThrowAsync<NpgsqlException>();
        events.Last(e => e is "notified" or "archived").Should().Be("notified", "the run died with its notification standing");

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        events.Last(e => e is "raised" or "cleared").Should().Be("cleared");
        events.Last(e => e is "notified" or "archived").Should().Be("archived");
        (await tenant.InstancesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("resolved");
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task A_death_after_a_snooze_re_notified_through_a_close_and_before_its_close_is_owed_is_finished_by_the_sweep()
    {
        var tenant = await SeedAsync();
        var events = await RecordAlarmsAndNotificationsAsync(tenant);
        var excursion = await tenant.OpenAsync();
        await tenant.HandleOpenedAsync(tenant.Process(), excursion);
        var instance = (await tenant.InstancesAsync(excursion)).Single().Id;
        await tenant.LapseSnoozeAsync(instance);
        var dying = tenant.Hooked();
        dying.BeforeOweClose = () => throw new NpgsqlException("the process dies once the re-notification is sent");
        var process = tenant.Process(dying);
        CloseAtTheDispatch(tenant, process);

        await process.Invoking(p => ResumeAsync(p, instance)).Should().ThrowAsync<NpgsqlException>();
        events.Last(e => e is "raised" or "cleared").Should().Be("raised", "the run died with its alarm standing");
        events.Last(e => e is "notified" or "archived").Should().Be("notified", "the run died with its notification standing");

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        events.Last(e => e is "raised" or "cleared").Should().Be("cleared");
        events.Last(e => e is "notified" or "archived").Should().Be("archived");
        events.Count(e => e == "notified").Should().Be(2, "the sweep sends nothing the run already sent");
        (await tenant.InstancesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("resolved");
        (await tenant.DeliveriesAsync(excursion)).Should().NotContain(d => d.Status == "pending");
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task A_snooze_re_notification_holds_its_opening_owed_while_it_sends_and_settles_it_once_done()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        var process = tenant.Process();
        await tenant.HandleOpenedAsync(process, excursion);
        var instance = (await tenant.InstancesAsync(excursion)).Single().Id;
        await tenant.LapseSnoozeAsync(instance);
        DateTime? owedDuringTheSend = null;
        tenant.OnSend = async () => owedDuringTheSend = (await tenant.OwedAsync(excursion)).Opened;

        (await ResumeAsync(process, instance)).Should().BeTrue();

        owedDuringTheSend.Should().NotBeNull("a run that dies during the send leaves its opening for the sweep");
        tenant.Sends.Should().Be(2);
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    /// <summary>
    /// A re-notification dies once it is sent, with its excursion still open, and the sweep re-runs
    /// the opening it holds owed.
    /// </summary>
    private static async Task<(Guid Excursion, ConcurrentQueue<string> Events)> ResumeThatDiesWhileOpenAsync(Tenant tenant)
    {
        var events = await RecordAlarmsAndNotificationsAsync(tenant);
        var excursion = await tenant.OpenAsync();
        await tenant.HandleOpenedAsync(tenant.Process(), excursion);
        var instance = (await tenant.InstancesAsync(excursion)).Single().Id;
        await tenant.LapseSnoozeAsync(instance);
        var dying = tenant.Hooked();
        dying.BeforeOweClose = () => throw new NpgsqlException("the process dies once the re-notification is sent");

        await tenant.Process(dying).Invoking(p => ResumeAsync(p, instance)).Should().ThrowAsync<NpgsqlException>();
        (await tenant.OwedAsync(excursion)).Opened.Should().NotBeNull();
        return (excursion, events);
    }

    [Fact]
    public async Task A_dead_re_notification_finished_under_a_scope_begun_since_is_not_recorded_as_silenced()
    {
        var tenant = await SeedAsync(AlertRuleSeverity.Warning);
        var (excursion, _) = await ResumeThatDiesWhileOpenAsync(tenant);
        tenant.Dnd = new HashSet<DndScope> { DndScope.Lows };

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        (await tenant.InstancesAsync(excursion)).Should().ContainSingle()
            .Which.SuppressionReason.Should().BeNull("the instance reached its channels before the scope began");
        (await tenant.AlarmingAsync()).Should().NotBeNull("the alarm the member received still sounds");
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task A_dead_re_notification_on_an_open_excursion_is_settled_by_the_sweep_without_a_send()
    {
        var tenant = await SeedAsync(AlertRuleSeverity.Warning);
        var (excursion, events) = await ResumeThatDiesWhileOpenAsync(tenant);
        var raised = events.Count(e => e == "raised");

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        tenant.Sends.Should().Be(2, "the opening and the re-notification each reached the channel once");
        events.Count(e => e == "raised").Should().Be(raised);
        (await tenant.AlarmingAsync()).Should().NotBeNull();
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task A_re_notification_leaves_an_opening_already_owed_with_its_stamp_for_the_sweep()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        var process = tenant.Process();
        await tenant.HandleOpenedAsync(process, excursion);
        var instance = (await tenant.InstancesAsync(excursion)).Single().Id;
        await tenant.LapseSnoozeAsync(instance);
        await tenant.UnsettleOpeningAsync(excursion);
        tenant.Time.Advance(TimeSpan.FromMinutes(1));

        (await ResumeAsync(process, instance)).Should().BeTrue();

        (await tenant.OwedAsync(excursion)).Opened.Should().Be(Opened, "the opening's own run owes its work, and holds its lease");
        await tenant.SweepAsync(tenant.Process(), AfterLease + TimeSpan.FromMinutes(1));
        tenant.Sends.Should().Be(2);
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    private static readonly DateTime Lapse = Opened.AddMinutes(15);

    /// <summary>
    /// The opening was sent to every channel and settled, its snooze lapsed at <see cref="Lapse"/>,
    /// and the clock reads a minute later, so a re-notification starts after the opening's rows.
    /// </summary>
    private async Task<(Tenant Tenant, Guid Excursion, Guid Instance)> OpenedAndLapsedAsync(int channels = 1)
    {
        var tenant = await SeedAsync(channels: channels);
        var excursion = await tenant.OpenAsync();
        await tenant.HandleOpenedAsync(tenant.Process(), excursion);
        var instance = (await tenant.InstancesAsync(excursion)).Single().Id;
        await tenant.LapseSnoozeAsync(instance, Lapse);
        tenant.Time.SetUtcNow(Lapse.AddMinutes(1));
        tenant.Sends.Should().Be(channels);
        return (tenant, excursion, instance);
    }

    private static async Task ShouldHaveSentEachChannelAsync(Tenant tenant, Guid excursion, int times)
    {
        var deliveries = await tenant.DeliveriesAsync(excursion);
        deliveries.Should().HaveCount(times * tenant.Channels.Count);
        deliveries.Should().OnlyContain(d => d.Status == "delivered");
        deliveries.GroupBy(d => d.AlertRuleChannelId).Should().OnlyContain(g => g.Count() == times,
            "each channel has one row per send");
    }

    [Fact]
    public async Task A_resume_that_dies_before_it_ends_the_snooze_is_resumed_by_the_next_sweep_and_sends_once()
    {
        var (tenant, excursion, instance) = await OpenedAndLapsedAsync(channels: 2);
        var dying = tenant.Hooked();
        dying.BeforeEndLapsedSnooze = () => throw new NpgsqlException("the process dies before the snooze ends");

        await tenant.Process(dying).Invoking(p => ResumeAsync(p, instance)).Should().ThrowAsync<NpgsqlException>();
        (await tenant.InstancesAsync(excursion)).Single().SnoozedUntil.Should().Be(Lapse, "the lapse is left for the next sweep");
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));

        var process = tenant.Process();
        await tenant.SweepAsync(process, tenant.Time.GetUtcNow());
        await tenant.SweepAsync(process, tenant.Time.GetUtcNow() + AlertSweepJob.OwedTransitionLease + TimeSpan.FromMinutes(1));

        tenant.Sends.Should().Be(4, "each channel had the opening, and has the re-notification once");
        await ShouldHaveSentEachChannelAsync(tenant, excursion, times: 2);
        (await tenant.InstancesAsync(excursion)).Single().SnoozedUntil.Should().BeNull();
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task A_resume_that_dies_after_it_ends_the_snooze_and_before_its_rows_is_sent_once_by_the_owed_pass()
    {
        var (tenant, excursion, instance) = await OpenedAndLapsedAsync(channels: 2);
        var dying = tenant.Hooked();
        dying.AfterEndLapsedSnooze = () => throw new NpgsqlException("the process dies once the snooze has ended");

        await tenant.Process(dying).Invoking(p => ResumeAsync(p, instance)).Should().ThrowAsync<NpgsqlException>();
        (await tenant.InstancesAsync(excursion)).Single().SnoozedUntil.Should().BeNull();
        (await tenant.OwedAsync(excursion)).Opened.Should().Be(Lapse.AddMinutes(1), "the re-notification holds its opening owed");
        (await tenant.DeliveriesAsync(excursion)).Should().HaveCount(2, "only the opening's rows were written");

        var process = tenant.Process();
        var afterLease = tenant.Time.GetUtcNow() + AlertSweepJob.OwedTransitionLease + TimeSpan.FromSeconds(1);
        await tenant.SweepAsync(process, afterLease);
        await tenant.SweepAsync(process, afterLease + AlertSweepJob.OwedTransitionLease + TimeSpan.FromMinutes(1));

        tenant.Sends.Should().Be(4, "the opening's answered rows do not stand for the re-notification the run lost");
        await ShouldHaveSentEachChannelAsync(tenant, excursion, times: 2);
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task A_resume_that_dies_after_every_channel_answered_sends_nothing_more()
    {
        var (tenant, excursion, instance) = await OpenedAndLapsedAsync(channels: 2);
        var dying = tenant.Hooked();
        dying.BeforeOweClose = () => throw new NpgsqlException("the process dies once the re-notification is sent");

        await tenant.Process(dying).Invoking(p => ResumeAsync(p, instance)).Should().ThrowAsync<NpgsqlException>();
        tenant.Sends.Should().Be(4);

        await tenant.SweepAsync(tenant.Process(), tenant.Time.GetUtcNow() + AlertSweepJob.OwedTransitionLease + TimeSpan.FromSeconds(1));

        tenant.Sends.Should().Be(4);
        await ShouldHaveSentEachChannelAsync(tenant, excursion, times: 2);
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task A_resume_that_dies_after_its_row_and_before_the_send_has_the_pending_row_sent()
    {
        var (tenant, excursion, instance) = await OpenedAndLapsedAsync();
        using var dies = new CancellationTokenSource();
        tenant.OnSend = () =>
        {
            tenant.OnSend = null;
            dies.Cancel();
            throw new OperationCanceledException(dies.Token);
        };

        var dying = tenant.Process().GetRequiredService<IAlertSnoozeService>().ResumeAsync(instance, dies.Token);
        await dying.Invoking(async t => await t).Should().ThrowAsync<OperationCanceledException>();
        (await tenant.DeliveriesAsync(excursion)).Should().ContainSingle(d => d.Status == "pending");

        await tenant.SweepAsync(tenant.Process(), tenant.Time.GetUtcNow() + AlertSweepJob.OwedTransitionLease + TimeSpan.FromSeconds(1));

        tenant.Sends.Should().Be(3, "the dying send never reached the instance, and the sweep's does");
        await ShouldHaveSentEachChannelAsync(tenant, excursion, times: 2);
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task A_second_re_notification_that_dies_before_its_rows_is_sent_although_the_first_was_answered()
    {
        var (tenant, excursion, instance) = await OpenedAndLapsedAsync(channels: 2);
        (await ResumeAsync(tenant.Process(), instance)).Should().BeTrue();
        var first = (await tenant.InstancesAsync(excursion)).Single().RenotifiedAt;
        var secondLapse = Lapse.AddMinutes(20);
        await tenant.LapseSnoozeAsync(instance, secondLapse);
        tenant.Time.SetUtcNow(secondLapse.AddMinutes(1));
        var dying = tenant.Hooked();
        dying.AfterEndLapsedSnooze = () => throw new NpgsqlException("the process dies once the second snooze has ended");

        await tenant.Process(dying).Invoking(p => ResumeAsync(p, instance)).Should().ThrowAsync<NpgsqlException>();
        (await tenant.InstancesAsync(excursion)).Single().RenotifiedAt
            .Should().Be(secondLapse.AddMinutes(1)).And.NotBe(first, "each re-notification starts its own send");
        tenant.Sends.Should().Be(4);

        await tenant.SweepAsync(tenant.Process(), tenant.Time.GetUtcNow() + AlertSweepJob.OwedTransitionLease + TimeSpan.FromSeconds(1));

        tenant.Sends.Should().Be(6, "the first re-notification's answered rows do not stand for the second");
        await ShouldHaveSentEachChannelAsync(tenant, excursion, times: 3);
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task Two_resumes_of_one_lapse_re_notify_once()
    {
        var (tenant, excursion, instance) = await OpenedAndLapsedAsync();
        var first = tenant.Hooked();
        first.BeforeEndLapsedSnooze = async () =>
        {
            first.BeforeEndLapsedSnooze = null;
            (await ResumeAsync(tenant.Process(), instance)).Should().BeTrue();
        };

        (await ResumeAsync(tenant.Process(first), instance))
            .Should().BeFalse("the other resume ended the snooze this one read");

        tenant.Sends.Should().Be(2);
        await ShouldHaveSentEachChannelAsync(tenant, excursion, times: 2);
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    /// <summary>
    /// The notice was sent and its dispatch settled; its retirement dies after it resolves the
    /// instance, with the reason, and before it archives the notification.
    /// </summary>
    [Fact]
    public async Task A_notice_whose_retirement_dies_after_its_resolve_is_archived_by_the_sweep()
    {
        var tenant = await SeedAsync();
        var events = await RecordAlarmsAndNotificationsAsync(tenant, InAppProvider.UnevaluableNotificationType);
        await UnevaluableNotice(tenant, tenant.Process())
            .CheckAsync(new EnabledAlertRules([tenant.Rule], []), CancellationToken.None);
        var notice = await NoticeOfAsync(tenant);
        (await tenant.OwedAsync(notice)).Should().Be((null, null));
        var dying = tenant.Hooked();
        dying.AfterResolveInstances = () => throw new NpgsqlException("the retiring process dies once the instance is resolved");

        await UnevaluableNotice(tenant, tenant.Process(dying))
            .Invoking(n => n.ResolveAsync(tenant.Rule.Id, ExcursionCloseReason.RuleDisabled, CancellationToken.None))
            .Should().ThrowAsync<NpgsqlException>();
        events.Last(e => e is "notified" or "archived").Should().Be("notified", "the run died with its notification standing");

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        events.Last(e => e is "notified" or "archived").Should().Be("archived", "no notification stays up for a resolved notice");
        (await tenant.InstancesAsync(notice)).Should().ContainSingle()
            .Which.ResolutionReason.Should().Be(ExcursionCloseReason.RuleDisabled.ToWireString());
        (await tenant.OwedAsync(notice)).Should().Be((null, null));
    }

    private static UnevaluableRuleNotice UnevaluableNotice(Tenant tenant, IServiceProvider process)
    {
        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([new RustValidationIssue("condition", "threshold", "field_missing", "value")]);
        return new UnevaluableRuleNotice(
            process.GetRequiredService<IAlertRepository>(), tenant.Rules.Object, process.GetRequiredService<ITenantAccessor>(),
            process.GetRequiredService<IAlertDeliveryService>(), process.GetRequiredService<ISensorContextEnricher>(),
            process.GetRequiredService<IExcursionResolutionHandler>(), new ConditionVersionLog(), validator.Object,
            tenant.Time, NullLogger<UnevaluableRuleNotice>.Instance);
    }

    private static HeldRuleNotice HeldNotice(Tenant tenant, IServiceProvider process) => new(
        process.GetRequiredService<IAlertRepository>(), tenant.Rules.Object, process.GetRequiredService<ITenantAccessor>(),
        process.GetRequiredService<IAlertDeliveryService>(), process.GetRequiredService<ISensorContextEnricher>(),
        process.GetRequiredService<IExcursionResolutionHandler>(), tenant.Time, NullLogger<HeldRuleNotice>.Instance);

    /// <summary>
    /// The rule's retirement resolves its notices at the notice's Home Assistant send, after its
    /// delivery rows and before its in-app notification.
    /// </summary>
    private static void RetireAtTheSend(Tenant tenant, IServiceProvider process) =>
        tenant.OnSend = async () =>
        {
            tenant.OnSend = null;
            await UnevaluableNotice(tenant, process).ResolveAsync(
                tenant.Rule.Id, ExcursionCloseReason.RuleDisabled, CancellationToken.None);
        };

    /// <summary>The process shuts down once the notification is made, before the dispatch returns.</summary>
    private static Action ShutDown(CancellationTokenSource shutdown) => () =>
    {
        shutdown.Cancel();
        shutdown.Token.ThrowIfCancellationRequested();
    };

    private static async Task<Guid> NoticeOfAsync(Tenant tenant)
    {
        await using var db = tenant.Context();
        return await db.AlertExcursions.AsNoTracking()
            .Where(e => e.AlertRuleId == tenant.Rule.Id && e.UnevaluableConditionVersion != null)
            .Select(e => e.Id)
            .SingleAsync();
    }

    /// <summary>
    /// The retirement during the send leaves the notification it could not yet see, and the run that
    /// sent it dies before it returns, so nothing of that run is left to archive it.
    /// </summary>
    [Fact]
    public async Task An_unevaluable_notice_resolved_during_its_send_by_a_run_that_dies_is_closed_by_the_sweep()
    {
        var tenant = await SeedAsync();
        using var shutdown = new CancellationTokenSource();
        var events = await RecordAlarmsAndNotificationsAsync(
            tenant, InAppProvider.UnevaluableNotificationType, ShutDown(shutdown));
        var process = tenant.Process();
        RetireAtTheSend(tenant, process);

        await UnevaluableNotice(tenant, process)
            .Invoking(n => n.CheckAsync(new EnabledAlertRules([tenant.Rule], []), shutdown.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        var notice = await NoticeOfAsync(tenant);
        events.Last(e => e is "notified" or "archived").Should().Be("notified", "the run died with its notification standing");

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        events.Last(e => e is "notified" or "archived").Should().Be("archived", "no notification stays up for a resolved notice");
        (await tenant.InstancesAsync(notice)).Should().ContainSingle().Which.Status.Should().Be("resolved");
        (await tenant.OwedAsync(notice)).Should().Be((null, null));
    }

    /// <inheritdoc cref="An_unevaluable_notice_resolved_during_its_send_by_a_run_that_dies_is_closed_by_the_sweep"/>
    [Fact]
    public async Task A_held_notice_resolved_during_its_send_by_a_run_that_dies_is_closed_by_the_sweep()
    {
        var tenant = await SeedAsync();
        await using (var db = tenant.Context())
        {
            db.AlertTrackerState.Add(new AlertTrackerStateEntity
            {
                AlertRuleId = tenant.Rule.Id,
                TenantId = tenant.Id,
                State = "idle",
                UpdatedAt = Opened,
                HeldSince = Opened - RustAlertEngine.HoldBound(),
                HeldFacts = "glucose:stale",
            });
            await db.SaveChangesAsync();
        }
        using var shutdown = new CancellationTokenSource();
        var events = await RecordAlarmsAndNotificationsAsync(
            tenant, InAppProvider.HeldNotificationType, ShutDown(shutdown));
        var process = tenant.Process();
        RetireAtTheSend(tenant, process);

        await HeldNotice(tenant, process)
            .Invoking(n => n.CheckAsync([tenant.Rule], shutdown.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        var notice = await NoticeOfAsync(tenant);
        events.Last(e => e is "notified" or "archived").Should().Be("notified", "the run died with its notification standing");

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        events.Last(e => e is "notified" or "archived").Should().Be("archived", "no notification stays up for a resolved notice");
        (await tenant.InstancesAsync(notice)).Should().ContainSingle().Which.Status.Should().Be("resolved");
        (await tenant.OwedAsync(notice)).Should().Be((null, null));
    }


    [Fact]
    public async Task A_notice_retired_during_its_send_by_a_run_that_completes_leaves_nothing_standing()
    {
        var tenant = await SeedAsync();
        var events = await RecordAlarmsAndNotificationsAsync(tenant, InAppProvider.UnevaluableNotificationType);
        var process = tenant.Process();
        RetireAtTheSend(tenant, process);

        await UnevaluableNotice(tenant, process).CheckAsync(new EnabledAlertRules([tenant.Rule], []), CancellationToken.None);
        var notice = await NoticeOfAsync(tenant);

        events.Should().Contain("notified", "the notification was made after the retirement");
        events.Last(e => e is "notified" or "archived").Should().Be("archived", "no notification stays up for a resolved notice");
        (await tenant.InstancesAsync(notice)).Should().ContainSingle().Which.Status.Should().Be("resolved");
        (await tenant.OwedAsync(notice)).Should().Be((null, null));
    }

    /// <summary>
    /// A send's rerun of the close lands after the retirement ends the notice and before the
    /// retirement resolves its instance, the step that writes its reason.
    /// </summary>
    [Fact]
    public async Task A_rerun_that_lands_between_a_notice_ending_and_its_resolve_keeps_the_resolve_reason()
    {
        var tenant = await SeedAsync();
        var events = await RecordAlarmsAndNotificationsAsync(tenant, InAppProvider.UnevaluableNotificationType);
        var process = tenant.Process();
        await UnevaluableNotice(tenant, process).CheckAsync(new EnabledAlertRules([tenant.Rule], []), CancellationToken.None);
        var notice = await NoticeOfAsync(tenant);
        var retiring = tenant.Hooked();
        retiring.AfterEndNotice = async () =>
        {
            retiring.AfterEndNotice = null;
            (await process.GetRequiredService<IExcursionResolutionHandler>()
                    .CloseAgainIfEndedAsync(notice, tenant.Id, CancellationToken.None))
                .Should().BeFalse("the notice stands until its resolve resolves its instance");
        };

        await UnevaluableNotice(tenant, tenant.Process(retiring))
            .ResolveAsync(tenant.Rule.Id, ExcursionCloseReason.RuleDisabled, CancellationToken.None);

        (await tenant.InstancesAsync(notice)).Should().ContainSingle()
            .Which.ResolutionReason.Should().Be(ExcursionCloseReason.RuleDisabled.ToWireString());
        events.Last(e => e is "notified" or "archived").Should().Be("archived");
        (await tenant.OwedAsync(notice)).Should().Be((null, null));
    }

    /// <summary>
    /// The retirement dies after it ends the notice and before it resolves its instance, during the
    /// notice's send, and the run that sends dies too, once the notification is made.
    /// </summary>
    [Fact]
    public async Task A_notice_whose_retirement_dies_before_its_resolve_is_resolved_with_its_reason_by_its_check()
    {
        var tenant = await SeedAsync();
        using var shutdown = new CancellationTokenSource();
        var events = await RecordAlarmsAndNotificationsAsync(
            tenant, InAppProvider.UnevaluableNotificationType, ShutDown(shutdown));
        var process = tenant.Process();
        var dying = tenant.Hooked();
        dying.AfterEndNotice = () => throw new NpgsqlException("the retiring process dies once the notice is ended");
        var retiring = tenant.Process(dying);
        tenant.OnSend = async () =>
        {
            tenant.OnSend = null;
            await UnevaluableNotice(tenant, retiring)
                .Invoking(n => n.ResolveAsync(tenant.Rule.Id, ExcursionCloseReason.RuleDisabled, CancellationToken.None))
                .Should().ThrowAsync<NpgsqlException>();
        };

        await UnevaluableNotice(tenant, process)
            .Invoking(n => n.CheckAsync(new EnabledAlertRules([tenant.Rule], []), shutdown.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        var notice = await NoticeOfAsync(tenant);

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        (await tenant.InstancesAsync(notice)).Should().ContainSingle()
            .Which.Status.Should().Be("triggered", "the sweep leaves a standing notice to its check, which has the reason");

        await UnevaluableNotice(tenant, process).CheckAsync(new EnabledAlertRules([], []), CancellationToken.None);
        await tenant.SweepAsync(tenant.Process(), AfterLease.AddMinutes(1));

        (await tenant.InstancesAsync(notice)).Should().ContainSingle()
            .Which.ResolutionReason.Should().Be(ExcursionCloseReason.RuleDisabled.ToWireString());
        events.Last(e => e is "notified" or "archived").Should().Be("archived");
        (await tenant.OwedAsync(notice)).Should().Be((null, null));
    }

    [Fact]
    public async Task An_opening_whose_own_evaluation_closes_it_leaves_the_close_and_its_reason_to_that_evaluation()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        var close = await tenant.CloseAsync(Opened.AddMinutes(3));
        var process = tenant.Process();

        await process.GetRequiredService<IExcursionOpenedHandler>().HandleOpenedAsync(
            tenant.Rule, excursion, Opened, new SensorContext { LatestValue = 52, LatestTimestamp = Opened, TrendRate = null, LastReadingAt = Opened },
            tenant.Id, late: false, metBy: [], callerCloses: true, CancellationToken.None);

        tenant.Sends.Should().Be(1);
        (await tenant.InstancesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("triggered");

        await process.GetRequiredService<IExcursionResolutionHandler>().HandleClosedAsync(close, tenant.Id, CancellationToken.None);

        (await tenant.InstancesAsync(excursion)).Should().ContainSingle()
            .Which.ResolutionReason.Should().Be(close.CloseReason!.Value.ToWireString());
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    /// <summary>Stores a value no member has in the rule's <paramref name="column"/>, as another image could.</summary>
    private async Task MakeUnreadableAsync(Tenant tenant, string column, string value)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await conn.PinTenantAsync(tenant.Id);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE alert_rules SET {column} = @value WHERE id = @rule;
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@rule", tenant.Rule.Id));
        cmd.Parameters.Add(new NpgsqlParameter("@value", value));
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task An_opening_whose_enabled_rule_cannot_be_read_stays_owed()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        await MakeUnreadableAsync(tenant, "condition_type", "no_such_condition");

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        tenant.Sends.Should().Be(0);
        (await tenant.OwedAsync(excursion)).Opened.Should().NotBeNull("an unreadable rule is not a disabled one; the opening is retried each lease");

        await tenant.CloseAsync(Opened.AddMinutes(10));
        await tenant.SweepAsync(tenant.Process(), AfterLease.AddMinutes(10));

        (await tenant.OwedAsync(excursion)).Opened.Should().BeNull("a closed excursion ends what its opening owed");
    }

    /// <summary>
    /// When the rule's notice that it cannot be read is recorded: an hour after its excursion opened,
    /// so the hold is told apart from one counted from the excursion or its tracker state.
    /// </summary>
    private static readonly DateTime NoticeAt = Opened.AddHours(1);

    /// <summary>Records the notice for the rule as it is now stored, which no member can read.</summary>
    private static async Task<string> RecordUnreadableNoticeAsync(Tenant tenant, DateTime at)
    {
        var repository = tenant.Repository();
        var rule = (await repository.ReadEnabledRulesAsync(CancellationToken.None)).Unreadable.Single();
        var version = UnevaluableRuleNotice.VersionOf(rule);
        (await repository.RecordUnreadableRuleNoticeAsync(rule, version, at, CancellationToken.None)).Should().NotBeNull();
        return version;
    }

    /// <summary>
    /// The rule's severity becomes one no member has while its excursion is open and dispatched, and
    /// its notice that it cannot be read is recorded at <see cref="NoticeAt"/>.
    /// </summary>
    private async Task<(Guid Excursion, ConcurrentQueue<string> Events, string Version)> OpenThenMakeUnreadableAsync(Tenant tenant)
    {
        var events = await RecordAlarmsAndNotificationsAsync(tenant);
        var excursion = await tenant.OpenAsync();
        await tenant.HandleOpenedAsync(tenant.Process(), excursion);
        await MakeUnreadableAsync(tenant, "severity", "normal");
        return (excursion, events, await RecordUnreadableNoticeAsync(tenant, NoticeAt));
    }

    private static async Task SweepAtAsync(Tenant tenant, DateTimeOffset at, AlertRepository? repository = null)
    {
        tenant.Time.SetUtcNow(at);
        await tenant.SweepAsync(tenant.Process(repository), at);
    }

    private static async Task<DateTime?> EndedAtAsync(Tenant tenant, Guid excursion)
    {
        await using var db = tenant.Context();
        return await db.AlertExcursions.AsNoTracking().Where(e => e.Id == excursion).Select(e => e.EndedAt).SingleAsync();
    }

    [NativeFact]
    public async Task An_open_excursion_of_a_rule_that_cannot_be_read_closes_once_its_notice_has_stood_for_the_hold_bound()
    {
        var tenant = await SeedAsync();
        var (excursion, events, version) = await OpenThenMakeUnreadableAsync(tenant);
        var bound = RustAlertEngine.HoldBound();

        await SweepAtAsync(tenant, Opened + bound);

        (await EndedAtAsync(tenant, excursion)).Should().BeNull("the hold counts from the notice, not from the excursion");

        await SweepAtAsync(tenant, NoticeAt + bound - TimeSpan.FromMinutes(1));

        (await EndedAtAsync(tenant, excursion)).Should().BeNull("a rule fixed within the bound keeps its alarm");
        (await tenant.AlarmingAsync()).Should().NotBeNull();
        events.Last(e => e is "notified" or "archived").Should().Be("notified");

        await SweepAtAsync(tenant, NoticeAt + bound);

        (await EndedAtAsync(tenant, excursion)).Should().Be(NoticeAt + bound);
        (await tenant.InstancesAsync(excursion)).Should().ContainSingle()
            .Which.ResolutionReason.Should().Be(ExcursionCloseReason.HoldBound.ToWireString());
        (await tenant.AlarmingAsync()).Should().BeNull("a rule left unreadable stops alarming at the bound");
        events.Last(e => e is "notified" or "archived").Should().Be("archived");
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
        (await tenant.Repository().GetStandingUnevaluableNoticesAsync(tenant.Rule.Id, CancellationToken.None))
            .Should().ContainSingle("the rule still cannot be read").Which.ConditionVersion.Should().Be(version);
        var archived = events.Count(e => e == "archived");

        await SweepAtAsync(tenant, NoticeAt + bound + TimeSpan.FromMinutes(5));

        events.Count(e => e == "archived").Should().Be(archived, "the excursion closes once");
        tenant.Broadcasts.Verify(b => b.BroadcastAlertResolvedAsync(
            It.Is<AlertResolvedEvent>(r => r.ExcursionId == excursion && r.Reason == ExcursionCloseReason.HoldBound)), Times.Once);
    }

    [NativeFact]
    public async Task A_hold_bound_close_whose_run_dies_after_the_commit_is_finished_by_the_sweep()
    {
        var tenant = await SeedAsync();
        var (excursion, events, _) = await OpenThenMakeUnreadableAsync(tenant);
        var bound = RustAlertEngine.HoldBound();
        var dying = tenant.Hooked();
        dying.AfterResolveInstances = () => throw new NpgsqlException("the process dies once the instance is resolved");

        await FluentActions.Invoking(() => SweepAtAsync(tenant, NoticeAt + bound, dying))
            .Should().ThrowAsync<NpgsqlException>();

        (await EndedAtAsync(tenant, excursion)).Should().NotBeNull();
        (await tenant.OwedAsync(excursion)).Closed.Should().NotBeNull("the close is owed until its work is done");
        events.Last(e => e is "notified" or "archived").Should().Be("notified");

        await SweepAtAsync(tenant, NoticeAt + bound + AlertSweepJob.OwedTransitionLease + TimeSpan.FromSeconds(1));

        events.Last(e => e is "notified" or "archived").Should().Be("archived");
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
        (await tenant.InstancesAsync(excursion)).Should().ContainSingle()
            .Which.ResolutionReason.Should().Be(ExcursionCloseReason.HoldBound.ToWireString());
    }

    [NativeFact]
    public async Task A_notice_past_the_bound_for_a_value_the_rule_no_longer_holds_closes_nothing()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        await tenant.HandleOpenedAsync(tenant.Process(), excursion);
        await MakeUnreadableAsync(tenant, "severity", "normal");
        await RecordUnreadableNoticeAsync(tenant, Opened);
        await MakeUnreadableAsync(tenant, "severity", "moderate");
        await RecordUnreadableNoticeAsync(tenant, NoticeAt);

        await SweepAtAsync(tenant, Opened + RustAlertEngine.HoldBound());

        (await EndedAtAsync(tenant, excursion)).Should().BeNull("the value the rule holds has been unreadable for an hour less than the bound");
    }

    [NativeFact]
    public async Task A_readable_rules_notice_past_the_bound_that_its_body_cannot_be_evaluated_closes_nothing()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        await tenant.HandleOpenedAsync(tenant.Process(), excursion);
        (await tenant.Repository().RecordUnevaluableNoticeAsync(tenant.Rule, "body", Opened, CancellationToken.None))
            .Should().NotBeNull();

        await SweepAtAsync(tenant, Opened + RustAlertEngine.HoldBound() + TimeSpan.FromMinutes(1));

        (await EndedAtAsync(tenant, excursion)).Should().BeNull("only a rule that cannot be read is closed at the bound");
    }
}
