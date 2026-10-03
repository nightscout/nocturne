using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// An edit of a rule's conditions or enablement saves the rule and clears its re-arm hold
/// (docs/alerts/engine-semantics.md §6.3) in one transaction, on PostgreSQL under the runtime role
/// and Row Level Security, so a failure of either leaves both as they were.
/// </summary>
[Trait("Category", "Integration")]
public class AlertRulesControllerRearmPostgresTests(AlertRulesControllerRearmPostgresTests.Database database)
    : IClassFixture<AlertRulesControllerRearmPostgresTests.Database>
{
    private const string Body = """{"direction":"below","value":70}""";
    private const string EditedBody = """{"direction":"below","value":60}""";
    private const string Resolve = """{"type":"threshold","threshold":{"direction":"above","value":80}}""";

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("alert_rule_rearm");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>Fails the first statement writing <see cref="Table"/>, or the commit, once armed.</summary>
    private sealed class Failure : DbCommandInterceptor, IDbTransactionInterceptor
    {
        public string? Table { get; set; }
        public bool Commit { get; set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (Table is not null && command.CommandText.Contains($"UPDATE {Table} "))
                throw new InvalidOperationException($"injected failure writing {Table}");
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Table is not null && command.CommandText.Contains($"UPDATE {Table} "))
                throw new InvalidOperationException($"injected failure writing {Table}");
            return ValueTask.FromResult(result);
        }

        public ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            Commit
                ? throw new InvalidOperationException("injected commit failure")
                : ValueTask.FromResult(result);
    }

    private sealed class Factory(Func<NocturneDbContext> create) : ITenantDbContextFactory
    {
        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => ValueTask.FromResult(create());
    }

    private NocturneDbContext Context(Guid tenant, Failure failure) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString, npgsql => npgsql.EnableRetryOnFailure(
                maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null))
            .AddInterceptors(new TenantConnectionInterceptor(), failure)
            .Options) { TenantId = tenant };

    private async Task<(AlertRulesController Controller, Guid Tenant, Guid Rule, Failure Failure)> CreateAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'rearm-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"rearm-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var failure = new Failure();
        var rule = Guid.CreateVersion7();
        await using (var seed = Context(tenant, failure))
        {
            seed.AlertRules.Add(new AlertRuleEntity
            {
                Id = rule,
                TenantId = tenant,
                Name = "Low",
                ConditionType = AlertConditionType.Threshold,
                ConditionParams = Body,
                ClientConfiguration = "{}",
                Severity = AlertRuleSeverity.Warning,
                IsEnabled = true,
                AutoResolveEnabled = true,
                AutoResolveParams = Resolve,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            seed.AlertTrackerState.Add(new AlertTrackerStateEntity
            {
                AlertRuleId = rule,
                TenantId = tenant,
                State = "idle",
                AwaitingRearm = true,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.ValidateUpdate(
                It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<StoredConditionTrees>()))
            .Returns((AlertConditionType _, string body, bool _, string? autoResolve, string? client, StoredConditionTrees _) =>
                new ConditionUpdateCheck([], body, autoResolve, client, []));
        var controller = new AlertRulesController(
            new Factory(() => Context(tenant, failure)),
            Mock.Of<IAlertReferenceService>(),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            validator.Object,
            Mock.Of<ISecretEncryptionService>(),
            new AlertRuleRearm(new AlertRuleEvaluationGate(), Mock.Of<IAlertTrackerRepository>()),
            new AlertRuleRetirement(Mock.Of<IExcursionTracker>(), Mock.Of<IExcursionResolutionHandler>()),
            Mock.Of<ILogger<AlertRulesController>>());
        return (controller, tenant, rule, failure);
    }

    private static UpdateAlertRuleRequest EditedConditions() => new()
    {
        Name = "Low",
        ConditionType = AlertConditionType.Threshold,
        ConditionParams = JsonSerializer.Deserialize<JsonElement>(EditedBody),
        IsEnabled = true,
        AutoResolveEnabled = true,
        AutoResolveParams = JsonSerializer.Deserialize<JsonElement>(Resolve),
    };

    private async Task<(int Threshold, bool Enabled, bool AwaitingRearm)> StoredAsync(Guid tenant, Guid rule)
    {
        await using var db = Context(tenant, new Failure());
        var stored = await db.AlertRules.AsNoTracking().SingleAsync(r => r.Id == rule);
        var state = await db.AlertTrackerState.AsNoTracking().SingleAsync(s => s.AlertRuleId == rule);
        var threshold = JsonDocument.Parse(stored.ConditionParams).RootElement.GetProperty("value").GetInt32();
        return (threshold, stored.IsEnabled, state.AwaitingRearm);
    }

    [Fact]
    public async Task An_edit_saves_the_rule_and_clears_the_hold()
    {
        var (controller, tenant, rule, _) = await CreateAsync();

        await controller.UpdateRule(rule, EditedConditions(), CancellationToken.None);

        (await StoredAsync(tenant, rule)).Should().Be((60, true, false));
    }

    /// <summary>
    /// Read last-wins ignoring case, the request says 70, the stored value. The store re-orders
    /// the keys to <c>{"Value":70,"value":60,…}</c>, which the engines read as 60, so the edit is real.
    /// </summary>
    [Fact]
    public async Task An_edit_whose_names_differ_only_in_case_is_saved()
    {
        var (controller, tenant, rule, _) = await CreateAsync();
        var request = EditedConditions();
        request.ConditionParams = JsonSerializer.Deserialize<JsonElement>(
            """{"direction":"below","value":60,"Value":70}""");

        await controller.UpdateRule(rule, request, CancellationToken.None);

        (await StoredAsync(tenant, rule)).Should().Be((60, true, false));
    }

    [Fact]
    public async Task A_failure_clearing_the_hold_rolls_back_the_edit()
    {
        var (controller, tenant, rule, failure) = await CreateAsync();
        failure.Table = "alert_tracker_state";

        var act = () => controller.UpdateRule(rule, EditedConditions(), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        (await StoredAsync(tenant, rule)).Should().Be((70, true, true));
    }

    [Fact]
    public async Task A_failure_clearing_the_hold_rolls_back_the_toggle()
    {
        var (controller, tenant, rule, failure) = await CreateAsync();
        failure.Table = "alert_tracker_state";

        var act = () => controller.ToggleRule(rule, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        (await StoredAsync(tenant, rule)).Should().Be((70, true, true));
    }

    [Fact]
    public async Task A_failure_saving_the_rule_keeps_the_hold()
    {
        var (controller, tenant, rule, failure) = await CreateAsync();
        failure.Table = "alert_rules";

        var act = () => controller.UpdateRule(rule, EditedConditions(), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        (await StoredAsync(tenant, rule)).Should().Be((70, true, true));
    }

    [Fact]
    public async Task A_save_that_fails_to_commit_rolls_back_the_clear()
    {
        var (controller, tenant, rule, failure) = await CreateAsync();
        failure.Commit = true;

        var act = () => controller.UpdateRule(rule, EditedConditions(), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        (await StoredAsync(tenant, rule)).Should().Be((70, true, true));
    }
}
