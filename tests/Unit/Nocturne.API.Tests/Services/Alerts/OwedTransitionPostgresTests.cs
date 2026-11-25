using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Identity;
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

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("owed_transition");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

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
        public required AlertRuleChannelSnapshot Channel { get; init; }
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

        public AlertRepository Repository() => new(new SuppliedContextFactory(Context));

        /// <summary>A repository that runs a hook at a chosen step, to interleave two processes.</summary>
        public HookedRepository Hooked() => new(new SuppliedContextFactory(Context));

        public IAlertAcknowledgementService RealAcknowledgements() => new AlertAcknowledgementService(
            new SuppliedContextFactory(Context), Mock.Of<ISignalRBroadcastService>(), Mock.Of<ITenantMemberService>(),
            Mock.Of<IEngineAlarmRelay>(), NullLogger<AlertAcknowledgementService>.Instance, Time);

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
            connections.Add(Id, HomeAssistant, "connection");

            var tenantAccessor = Mock.Of<ITenantAccessor>(a => a.TenantId == Id);
            var canonical = new Mock<ICanonicalGlucoseService>();

            return new ServiceCollection()
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .AddSingleton<TimeProvider>(Time)
                .AddSingleton(tenantAccessor)
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
                .AddSingleton<IAlertDeliveryService, AlertDeliveryService>()
                .AddSingleton<IExcursionOpenedHandler, ExcursionOpenedHandler>()
                .AddSingleton<IExcursionResolutionHandler, ExcursionResolutionHandler>()
                .AddSingleton(Mock.Of<IUnevaluableRuleNotice>())
                .AddSingleton(NoHeldRules.Notice())
                .AddSingleton(Mock.Of<IClockAnomalyNotice>())
                .AddSingleton(Mock.Of<IAlertChannelNotice>())
                .AddSingleton(canonical.Object)
                .AddSingleton(EnricherOf())
                .BuildServiceProvider();
        }

        private static ISensorContextEnricher EnricherOf()
        {
            var enricher = new Mock<ISensorContextEnricher>();
            enricher
                .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) => c);
            return enricher.Object;
        }

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
                Rule, excursion, new SensorContext { LatestValue = 52, LatestTimestamp = Opened, TrendRate = null, LastReadingAt = Opened },
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
            return await base.OweCloseIfEndedAsync(excursionId, now, ct);
        }
    }

    private async Task<Tenant> SeedAsync(AlertRuleSeverity severity = AlertRuleSeverity.Critical)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'owed-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"owed-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var rule = Guid.CreateVersion7();
        var channel = Guid.CreateVersion7();
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
                IsEnabled = true,
                CreatedAt = Opened,
                UpdatedAt = Opened,
            });
            seed.AlertRuleChannels.Add(new AlertRuleChannelEntity
            {
                Id = channel,
                TenantId = tenant,
                AlertRuleId = rule,
                ChannelType = ChannelType.HomeAssistant,
                Destination = HomeAssistant,
            });
            await seed.SaveChangesAsync();
        }

        var snapshot = new AlertRuleSnapshot(
            rule, tenant, "Urgent Low", AlertConditionType.Threshold, """{"direction":"below","value":55}""",
            severity, "{}", 0, false, null);
        var channelSnapshot = new AlertRuleChannelSnapshot(channel, rule, ChannelType.HomeAssistant, HomeAssistant, null, 0);
        var seeded = new Tenant
        {
            Id = tenant,
            Rule = snapshot,
            Channel = channelSnapshot,
            Context = () => Context(tenant),
        };
        seeded.Rules
            .Setup(r => r.GetChannelsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<AlertRuleChannelSnapshot>> { [rule] = [channelSnapshot] });
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
            tenant.Rule, excursion, new SensorContext { LatestValue = 52, LatestTimestamp = Opened, TrendRate = null, LastReadingAt = Opened },
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
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @tenant::text, false);
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
    public async Task The_owed_pass_leaves_a_notice_to_its_own_check()
    {
        var tenant = await SeedAsync();
        const string version = "0000000000000000000000000000000000000000000000000000000000000001";
        var notice = (await tenant.Repository().RecordUnevaluableNoticeAsync(tenant.Rule, version, Opened, CancellationToken.None))!;

        (await tenant.Repository().GetOwedTransitionsAsync(AfterLease.UtcDateTime, CancellationToken.None))
            .Should().BeEmpty("a notice owes its dispatch to IUnevaluableRuleNotice, which builds its own payload");

        await tenant.Repository().EndNoticeAsync(notice.ExcursionId, Opened.AddMinutes(1), CancellationToken.None);
        (await tenant.OwedAsync(notice.ExcursionId)).Opened.Should().BeNull("a resolved notice owes no dispatch");
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
    private static async Task<ConcurrentQueue<string>> RecordAlarmsAndNotificationsAsync(Tenant tenant)
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
                member.ToString(), InAppProvider.NotificationType, It.IsAny<string>(), It.IsAny<NotificationCategory?>(),
                It.IsAny<NotificationUrgency?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<List<NotificationActionDto>?>(), It.IsAny<ResolutionConditions?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => events.Enqueue("notified"))
            .ReturnsAsync(new InAppNotificationDto());
        tenant.Notifications
            .Setup(n => n.ArchiveBySourceAsync(
                member.ToString(), InAppProvider.NotificationType, It.IsAny<string>(),
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

    [Fact]
    public async Task An_opening_whose_own_evaluation_closes_it_leaves_the_close_and_its_reason_to_that_evaluation()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        var close = await tenant.CloseAsync(Opened.AddMinutes(3));
        var process = tenant.Process();

        await process.GetRequiredService<IExcursionOpenedHandler>().HandleOpenedAsync(
            tenant.Rule, excursion, new SensorContext { LatestValue = 52, LatestTimestamp = Opened, TrendRate = null, LastReadingAt = Opened },
            tenant.Id, late: false, metBy: [], callerCloses: true, CancellationToken.None);

        tenant.Sends.Should().Be(1);
        (await tenant.InstancesAsync(excursion)).Should().ContainSingle().Which.Status.Should().Be("triggered");

        await process.GetRequiredService<IExcursionResolutionHandler>().HandleClosedAsync(close, tenant.Id, CancellationToken.None);

        (await tenant.InstancesAsync(excursion)).Should().ContainSingle()
            .Which.ResolutionReason.Should().Be(close.CloseReason!.Value.ToWireString());
        (await tenant.OwedAsync(excursion)).Should().Be((null, null));
    }

    [Fact]
    public async Task An_opening_whose_enabled_rule_cannot_be_read_stays_owed()
    {
        var tenant = await SeedAsync();
        var excursion = await tenant.OpenAsync();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @tenant::text, false);
                UPDATE alert_rules SET condition_type = 'no_such_condition' WHERE id = @rule;
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@tenant", tenant.Id));
            cmd.Parameters.Add(new NpgsqlParameter("@rule", tenant.Rule.Id));
            await cmd.ExecuteNonQueryAsync();
        }

        await tenant.SweepAsync(tenant.Process(), AfterLease);

        tenant.Sends.Should().Be(0);
        (await tenant.OwedAsync(excursion)).Opened.Should().NotBeNull("an unreadable rule is not a disabled one; the opening is retried each lease");

        await tenant.CloseAsync(Opened.AddMinutes(10));
        await tenant.SweepAsync(tenant.Process(), AfterLease.AddMinutes(10));

        (await tenant.OwedAsync(excursion)).Opened.Should().BeNull("a closed excursion ends what its opening owed");
    }
}
