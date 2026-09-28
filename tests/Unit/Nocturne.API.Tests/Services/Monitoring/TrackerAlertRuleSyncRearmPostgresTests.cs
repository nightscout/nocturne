using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

/// <summary>
/// A tracker sync that changes a managed rule's condition or disables it saves the rule and clears
/// its re-arm hold (docs/alerts/engine-semantics.md §6.3) in one transaction, on PostgreSQL under the
/// runtime role and Row Level Security, so a failure clearing the hold leaves the rule as it was.
/// </summary>
[Trait("Category", "Integration")]
public class TrackerAlertRuleSyncRearmPostgresTests(TrackerAlertRuleSyncRearmPostgresTests.Database database)
    : IClassFixture<TrackerAlertRuleSyncRearmPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("tracker_rule_rearm");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>Fails every statement writing <see cref="Table"/> once armed.</summary>
    private sealed class Failure : DbCommandInterceptor
    {
        public string? Table { get; set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return ValueTask.FromResult(result);
        }

        private void Check(DbCommand command)
        {
            if (Table is not null && command.CommandText.Contains($"UPDATE {Table} "))
                throw new InvalidOperationException($"injected failure writing {Table}");
        }
    }

    private sealed class Factory(Func<NocturneDbContext> create) : ITenantDbContextFactory
    {
        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => ValueTask.FromResult(create());
    }

    private sealed record Setup(
        TrackerAlertRuleSyncService Sync,
        Mock<IAlertReferenceService> References,
        Guid Tenant,
        Guid Definition,
        Guid Rule,
        Failure Failure);

    private NocturneDbContext Context(Guid tenant, Failure failure) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString, npgsql => npgsql.EnableRetryOnFailure(
                maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null))
            .AddInterceptors(new TenantConnectionInterceptor(), failure)
            .Options) { TenantId = tenant };

    /// <summary>A definition with one 24h threshold, synced to its managed rule, which holds re-arm.</summary>
    private async Task<Setup> CreateAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'tracker-rearm-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"tracker-rearm-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var failure = new Failure();
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            UserId = "user-1",
            Name = "Sensor",
            Mode = TrackerMode.Duration,
            LifespanHours = 240,
            Category = TrackerCategory.Consumable,
        };
        definition.NotificationThresholds.Add(new TrackerNotificationThresholdEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            TrackerDefinitionId = definition.Id,
            Hours = 24,
            Urgency = NotificationUrgency.Warn,
            RespectQuietHours = true,
        });
        await using (var seed = Context(tenant, failure))
        {
            seed.TrackerDefinitions.Add(definition);
            await seed.SaveChangesAsync();
        }

        var classifier = new Mock<IRuleScopeClassifier>();
        classifier
            .Setup(c => c.Classify(It.IsAny<AlertConditionType>(), It.IsAny<string>()))
            .Returns(RuleScopeClass.Undirected);
        var references = new Mock<IAlertReferenceService>();
        references
            .Setup(r => r.FindReferencingRulesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Guid>());
        var sync = new TrackerAlertRuleSyncService(
            new Factory(() => Context(tenant, failure)),
            classifier.Object,
            references.Object,
            new AlertRuleRearm(new AlertRuleEvaluationGate(), Mock.Of<IAlertTrackerRepository>()),
            new AlertRuleRetirement(Mock.Of<IExcursionTracker>(), Mock.Of<IExcursionResolutionHandler>()),
            NullLogger<TrackerAlertRuleSyncService>.Instance);
        await sync.SyncDefinitionAsync(definition.Id);

        Guid rule;
        await using (var db = Context(tenant, failure))
        {
            rule = (await db.AlertRules.AsNoTracking().SingleAsync()).Id;
            db.AlertTrackerState.Add(new AlertTrackerStateEntity
            {
                AlertRuleId = rule,
                TenantId = tenant,
                State = "idle",
                AwaitingRearm = true,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        return new Setup(sync, references, tenant, definition.Id, rule, failure);
    }

    private async Task MoveThresholdAsync(Guid tenant, int hours)
    {
        await using var db = Context(tenant, new Failure());
        var threshold = await db.TrackerNotificationThresholds.SingleAsync();
        threshold.Hours = hours;
        await db.SaveChangesAsync();
    }

    private void Referenced(Setup setup) =>
        setup.References
            .Setup(r => r.FindReferencingRulesAsync(setup.Rule, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Guid.NewGuid()]);

    private async Task<(int Minutes, bool Enabled, bool AwaitingRearm)> StoredAsync(Guid tenant, Guid rule)
    {
        await using var db = Context(tenant, new Failure());
        var stored = await db.AlertRules.AsNoTracking().SingleAsync(r => r.Id == rule);
        var state = await db.AlertTrackerState.AsNoTracking().SingleAsync(s => s.AlertRuleId == rule);
        var minutes = JsonDocument.Parse(stored.ConditionParams).RootElement.GetProperty("minutes").GetInt32();
        return (minutes, stored.IsEnabled, state.AwaitingRearm);
    }

    [Fact]
    public async Task A_sync_changing_the_condition_saves_the_rule_and_clears_the_hold()
    {
        var setup = await CreateAsync();
        await MoveThresholdAsync(setup.Tenant, 48);

        await setup.Sync.SyncDefinitionAsync(setup.Definition);

        (await StoredAsync(setup.Tenant, setup.Rule)).Should().Be((48 * 60, true, false));
    }

    [Fact]
    public async Task A_failure_clearing_the_hold_rolls_back_the_sync()
    {
        var setup = await CreateAsync();
        await MoveThresholdAsync(setup.Tenant, 48);
        setup.Failure.Table = "alert_tracker_state";

        var act = () => setup.Sync.SyncDefinitionAsync(setup.Definition);

        await act.Should().ThrowAsync<Exception>();
        (await StoredAsync(setup.Tenant, setup.Rule)).Should().Be((24 * 60, true, true));
    }

    [Fact]
    public async Task A_failure_clearing_the_hold_rolls_back_the_disable_on_definition_delete()
    {
        var setup = await CreateAsync();
        Referenced(setup);
        setup.Failure.Table = "alert_tracker_state";

        var act = () => setup.Sync.DeleteRulesForDefinitionAsync(setup.Definition);

        await act.Should().ThrowAsync<Exception>();
        (await StoredAsync(setup.Tenant, setup.Rule)).Should().Be((24 * 60, true, true));
    }

    [Fact]
    public async Task A_disable_on_definition_delete_clears_the_hold()
    {
        var setup = await CreateAsync();
        Referenced(setup);

        await setup.Sync.DeleteRulesForDefinitionAsync(setup.Definition);

        (await StoredAsync(setup.Tenant, setup.Rule)).Should().Be((24 * 60, false, false));
    }
}
