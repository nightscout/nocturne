using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.SetupHub;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// An enabled rule whose stored condition type, severity or scope class no engine member has is not
/// evaluated: the sweep reads it as an <see cref="UnreadableAlertRule"/> and tells its recipients
/// once, as for a body that cannot be evaluated. The rule can still be listed, opened, saved and
/// deleted. Run on PostgreSQL under the runtime role, since no other store holds such a value.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnreadableRuleNoticePostgresTests(UnreadableRuleNoticePostgresTests.Database database)
    : IClassFixture<UnreadableRuleNoticePostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var created = await SharedPostgres.CreateMigratedDatabaseAsync("unreadable_rule_notice");
            AppConnectionString = created.AppConnectionString;
            MigratorConnectionString = created.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private static readonly DateTime Now = new(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc);

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    private sealed class Harness
    {
        public required Guid Tenant { get; init; }
        public required AlertRepository Repository { get; init; }
        public required AlertRuleRepository Rules { get; init; }
        public required UnevaluableRuleNotice Notice { get; init; }
        public required Mock<IAlertDeliveryService> Delivery { get; init; }
        public required Mock<IExcursionResolutionHandler> Resolution { get; init; }
        public required StrongBox<IReadOnlySet<DndScope>> ActiveDnd { get; init; }
    }

    private async Task<Harness> CreateAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await conn.SeedTenantAsync(tenant, $"unreadable-{tenant:N}", "unreadable-rule-notice");
        }

        var contexts = new SuppliedContextFactory(() => Context(tenant));
        var repository = new AlertRepository(contexts);
        var rules = new AlertRuleRepository(contexts, TimeProvider.System);

        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(t => t.TenantId).Returns(tenant);

        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([]);

        var delivery = new Mock<IAlertDeliveryService>();
        delivery
            .Setup(d => d.DispatchAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
                It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid instanceId, IReadOnlyList<AlertRuleChannelSnapshot> _, AlertPayload _, CancellationToken ct) =>
            {
                await using var db = await contexts.CreateAsync(ct);
                db.AlertDeliveries.Add(new AlertDeliveryEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenant,
                    AlertInstanceId = instanceId,
                    ChannelType = ChannelType.Webhook,
                    Destination = "https://example.test/hook",
                    Payload = "{}",
                    Status = "delivered",
                    CreatedAt = Now,
                });
                await db.SaveChangesAsync(ct);
            });

        var resolution = new Mock<IExcursionResolutionHandler>();
        resolution
            .Setup(h => h.HandleClosedAsync(It.IsAny<ExcursionTransition>(), tenant, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var dnd = new StrongBox<IReadOnlySet<DndScope>>(new HashSet<DndScope>());
        var enricher = new Mock<ISensorContextEnricher>();
        enricher
            .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) =>
                c with { ActiveDndScopes = dnd.Value! });

        return new Harness
        {
            Tenant = tenant,
            Repository = repository,
            Rules = rules,
            Delivery = delivery,
            Resolution = resolution,
            ActiveDnd = dnd,
            Notice = new UnevaluableRuleNotice(
                repository,
                rules,
                accessor.Object,
                delivery.Object,
                enricher.Object,
                resolution.Object,
                new ConditionVersionLog(),
                validator.Object,
                new FakeTimeProvider(new DateTimeOffset(Now)),
                new ListLogger<UnevaluableRuleNotice>()),
        };
    }

    private async Task<Guid> SeedRuleAsync(Guid tenant, string name, string conditionType, string severity,
        string scopeClass = "undirected", string conditionParams = """{"direction":"below","value":70}""")
    {
        var id = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await conn.PinTenantAsync(tenant);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO alert_rules (id, tenant_id, name, condition_type, condition_params, auto_resolve_enabled,
                                     severity, client_configuration, is_enabled, allow_through_dnd, scope_class,
                                     sort_order, created_at, updated_at)
            VALUES ('{{id}}', '{{tenant}}', @name, @type, @params::jsonb, false,
                    @severity, '{}', true, false, @scope, 1, now(), now());
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@name", name));
        cmd.Parameters.Add(new NpgsqlParameter("@type", conditionType));
        cmd.Parameters.Add(new NpgsqlParameter("@severity", severity));
        cmd.Parameters.Add(new NpgsqlParameter("@scope", scopeClass));
        cmd.Parameters.Add(new NpgsqlParameter("@params", conditionParams));
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private Task SetConditionTypeAsync(Guid tenant, Guid ruleId, string conditionType) =>
        UpdateRuleAsync(tenant, ruleId, "condition_type = @value", conditionType);

    private Task SetEnabledAsync(Guid tenant, Guid ruleId, bool enabled) =>
        UpdateRuleAsync(tenant, ruleId, $"is_enabled = {(enabled ? "true" : "false")}", null);

    private async Task UpdateRuleAsync(Guid tenant, Guid ruleId, string assignment, string? value)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await conn.PinTenantAsync(tenant);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE alert_rules SET {assignment} WHERE id = '{ruleId}';
            """;
        if (value is not null)
            cmd.Parameters.Add(new NpgsqlParameter("@value", value));
        await cmd.ExecuteNonQueryAsync();
    }

    private static void VerifyNoticeSent(Harness harness, Times times, string ruleName,
        AlertRuleSeverity severity) =>
        harness.Delivery.Verify(d => d.DispatchAsync(
                It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
                It.Is<AlertPayload>(p => p.Kind == AlertPayloadKind.Unevaluable
                                         && p.RuleName == ruleName
                                         && p.AlertType == null
                                         && p.Severity == severity
                                         && p.GlucoseValue == null),
                It.IsAny<CancellationToken>()),
            times);

    [Fact]
    public async Task A_rule_with_a_condition_type_no_member_has_is_told_once_at_its_own_severity()
    {
        var harness = await CreateAsync();
        var readable = await SeedRuleAsync(harness.Tenant, "Readable", "threshold", "warning");
        await SeedRuleAsync(harness.Tenant, "Legacy type", "threshold_low", "warning");

        var first = await harness.Repository.ReadEnabledRulesAsync(CancellationToken.None);
        first.Rules.Select(r => r.Id).Should().Equal(readable);
        first.Unreadable.Should().ContainSingle().Which.Should().Match<UnreadableAlertRule>(r =>
            r.Name == "Legacy type" && r.StoredConditionType == "threshold_low" && r.Severity == AlertRuleSeverity.Warning);

        await harness.Notice.CheckAsync(first, CancellationToken.None);
        await harness.Notice.CheckAsync(await harness.Repository.ReadEnabledRulesAsync(CancellationToken.None), CancellationToken.None);

        VerifyNoticeSent(harness, Times.Once(), "Legacy type", AlertRuleSeverity.Warning);
        harness.Delivery.Verify(d => d.DispatchAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
            It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()), Times.Once, "the readable rule is not told");
        (await harness.Repository.GetStandingUnevaluableNoticesAsync(null, CancellationToken.None))
            .Should().ContainSingle().Which.AlertRuleId.Should().NotBe(readable);
    }

    [Fact]
    public async Task A_rule_with_a_severity_no_member_has_is_told_once_as_critical_through_do_not_disturb()
    {
        var harness = await CreateAsync();
        harness.ActiveDnd.Value = new HashSet<DndScope> { DndScope.All };
        await SeedRuleAsync(harness.Tenant, "Legacy severity", "threshold", "normal");

        for (var pass = 0; pass < 2; pass++)
        {
            await harness.Notice.CheckAsync(
                await harness.Repository.ReadEnabledRulesAsync(CancellationToken.None), CancellationToken.None);
        }

        harness.Delivery.Verify(d => d.DispatchAsync(
                It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
                It.Is<AlertPayload>(p => p.Kind == AlertPayloadKind.Unevaluable
                                         && p.RuleName == "Legacy severity"
                                         && p.AlertType == null
                                         && p.Severity == AlertRuleSeverity.Critical),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_notice_is_resolved_once_the_rule_is_edited_to_a_condition_type_the_engine_has()
    {
        var harness = await CreateAsync();
        var rule = await SeedRuleAsync(harness.Tenant, "Legacy type", "threshold_low", "warning");
        await harness.Notice.CheckAsync(
            await harness.Repository.ReadEnabledRulesAsync(CancellationToken.None), CancellationToken.None);
        var notice = (await harness.Repository.GetStandingUnevaluableNoticesAsync(rule, CancellationToken.None)).Single();

        await SetConditionTypeAsync(harness.Tenant, rule, "threshold");
        var repaired = await harness.Repository.ReadEnabledRulesAsync(CancellationToken.None);
        await harness.Notice.CheckAsync(repaired, CancellationToken.None);

        repaired.Unreadable.Should().BeEmpty();
        repaired.Rules.Select(r => r.Id).Should().Equal(rule);
        harness.Resolution.Verify(h => h.HandleClosedAsync(
            It.Is<ExcursionTransition>(t => t.ExcursionId == notice.ExcursionId && t.CloseReason == ExcursionCloseReason.Manual),
            harness.Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_rule_with_a_scope_class_no_member_has_is_reported_and_the_tenants_other_rules_are_still_read()
    {
        var harness = await CreateAsync();
        var readable = await SeedRuleAsync(harness.Tenant, "Readable", "threshold", "warning");
        await SeedRuleAsync(harness.Tenant, "Legacy scope", "threshold", "warning", scopeClass: "sideways");

        var read = await harness.Repository.ReadEnabledRulesAsync(CancellationToken.None);
        await harness.Notice.CheckAsync(read, CancellationToken.None);
        await harness.Notice.CheckAsync(
            await harness.Repository.ReadEnabledRulesAsync(CancellationToken.None), CancellationToken.None);

        read.Rules.Select(r => r.Id).Should().Equal(readable);
        read.Unreadable.Should().ContainSingle().Which.Should().Match<UnreadableAlertRule>(r =>
            r.StoredScopeClass == "sideways" && r.ScopeClass == RuleScopeClass.Undirected
            && r.Severity == AlertRuleSeverity.Warning);
        VerifyNoticeSent(harness, Times.Once(), "Legacy scope", AlertRuleSeverity.Warning);
    }

    [Fact]
    public async Task A_run_that_read_the_rule_before_it_was_disabled_records_and_sends_nothing()
    {
        var harness = await CreateAsync();
        var rule = await SeedRuleAsync(harness.Tenant, "Legacy type", "threshold_low", "warning");
        var readBeforeTheDisable = await harness.Repository.ReadEnabledRulesAsync(CancellationToken.None);
        await SetEnabledAsync(harness.Tenant, rule, false);

        await harness.Notice.CheckAsync(readBeforeTheDisable, CancellationToken.None);

        harness.Delivery.VerifyNoOtherCalls();
        (await harness.Repository.GetStandingUnevaluableNoticesAsync(null, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_that_read_a_condition_type_the_rule_no_longer_has_records_and_sends_nothing()
    {
        var harness = await CreateAsync();
        var rule = await SeedRuleAsync(harness.Tenant, "Legacy type", "threshold_low", "warning");
        var readBeforeTheEdit = await harness.Repository.ReadEnabledRulesAsync(CancellationToken.None);
        await SetConditionTypeAsync(harness.Tenant, rule, "threshold_high");

        await harness.Notice.CheckAsync(readBeforeTheEdit, CancellationToken.None);

        harness.Delivery.VerifyNoOtherCalls();
        (await harness.Repository.GetStandingUnevaluableNoticesAsync(null, CancellationToken.None)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(DndScope.Lows)]
    [InlineData(DndScope.Highs)]
    public async Task A_directional_do_not_disturb_window_does_not_silence_the_notice_of_a_rule_with_an_unreadable_scope_class(
        DndScope window)
    {
        var harness = await CreateAsync();
        harness.ActiveDnd.Value = new HashSet<DndScope> { window };
        await SeedRuleAsync(harness.Tenant, "Legacy scope", "threshold", "warning", scopeClass: "sideways");

        await harness.Notice.CheckAsync(
            await harness.Repository.ReadEnabledRulesAsync(CancellationToken.None), CancellationToken.None);

        VerifyNoticeSent(harness, Times.Once(), "Legacy scope", AlertRuleSeverity.Warning);
    }

    [Fact]
    public async Task An_all_window_silences_the_notice_of_a_non_critical_rule_with_an_unreadable_scope_class()
    {
        var harness = await CreateAsync();
        harness.ActiveDnd.Value = new HashSet<DndScope> { DndScope.All };
        await SeedRuleAsync(harness.Tenant, "Legacy scope", "threshold", "warning", scopeClass: "sideways");

        await harness.Notice.CheckAsync(
            await harness.Repository.ReadEnabledRulesAsync(CancellationToken.None), CancellationToken.None);

        harness.Delivery.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("condition_type", "threshold_low", AlertRuleUnreadableValues.ConditionType, AlertRuleReviewReasons.ConditionTypeUnrecognized)]
    [InlineData("severity", "normal", AlertRuleUnreadableValues.Severity, AlertRuleReviewReasons.SeverityUnrecognized)]
    [InlineData("scope_class", "sideways", AlertRuleUnreadableValues.ScopeClass, null)]
    public async Task A_rule_holding_a_value_no_member_has_is_listed_and_opened_with_readable_values(
        string column, string unreadable, AlertRuleUnreadableValues unreadableValues, string? shownReason)
    {
        var harness = await CreateAsync();
        var readable = await SeedRuleAsync(harness.Tenant, "Readable", "threshold", "warning");
        var rule = await SeedRuleAsync(harness.Tenant, "Legacy", "threshold", "warning");
        await UpdateRuleAsync(harness.Tenant, rule, $"{column} = @value", unreadable);

        var listed = await harness.Repository.GetRulesAsync(CancellationToken.None);
        var opened = await harness.Repository.GetRuleAsync(rule, CancellationToken.None);

        listed.Select(r => r.Rule.Id).Should().BeEquivalentTo([readable, rule]);
        foreach (var record in new[] { listed.Single(r => r.Rule.Id == rule), opened! })
        {
            Enum.IsDefined(record.Rule.ConditionType).Should().BeTrue();
            Enum.IsDefined(record.Rule.Severity).Should().BeTrue();
            Enum.IsDefined(record.Rule.ScopeClass).Should().BeTrue();
            record.Unreadable.Should().Be(unreadableValues);
            record.ReviewReason.Should().BeNull("nothing stored one");
            record.ShownReviewReason.Should().Be(shownReason);
        }
    }

    [Theory]
    [InlineData("condition_type", "threshold_low")]
    [InlineData("severity", "normal")]
    public async Task A_writer_that_rewrites_a_rule_from_its_record_stores_no_review_reason_it_did_not_read(
        string column, string unreadable)
    {
        var harness = await CreateAsync();
        var rule = await SeedRuleAsync(harness.Tenant, "Tracker", "threshold", "warning");
        await UpdateRuleAsync(harness.Tenant, rule, $"{column} = @value", unreadable);
        var opened = (await harness.Repository.GetRuleAsync(rule, CancellationToken.None))!;

        await harness.Rules.UpdateAsync(
            rule,
            AlertRuleWrite.Of(opened) with { ConditionType = AlertConditionType.Threshold, Severity = AlertRuleSeverity.Warning },
            CancellationToken.None);

        (await StoredValuesAsync(harness.Tenant, rule)).Should().Be(("threshold", "warning", "undirected", (string?)null));
        (await harness.Repository.GetRuleAsync(rule, CancellationToken.None))!.ShownReviewReason
            .Should().BeNull("the rule now holds members, so nothing is left to review");
    }

    [NativeTheory]
    [InlineData("severity", "normal")]
    [InlineData("scope_class", "sideways")]
    public async Task A_rule_holding_a_value_no_member_has_still_counts_as_referencing_the_rule_its_body_names(
        string column, string unreadable)
    {
        var harness = await CreateAsync();
        var target = await SeedRuleAsync(harness.Tenant, "Low", "threshold", "warning");
        var referencing = await SeedRuleAsync(harness.Tenant, "After low", "alert_state", "warning",
            conditionParams: $$"""{"alert_id":"{{target}}","state":"firing"}""");
        await UpdateRuleAsync(harness.Tenant, referencing, $"{column} = @value", unreadable);
        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(t => t.IsResolved).Returns(true);
        accessor.SetupGet(t => t.TenantId).Returns(harness.Tenant);
        var references = new AlertReferenceService(
            harness.Repository, accessor.Object, NullLogger<AlertReferenceService>.Instance);

        (await references.FindReferencingRulesAsync(target, CancellationToken.None))
            .Should().Equal([referencing], "deleting the target would leave the reference dangling");
        (await references.DetectCycleAsync(target, [referencing], CancellationToken.None))
            .Should().BeTrue("a reference back through the rule closes a cycle");
    }

    [Fact]
    public async Task The_scope_class_backfill_classifies_a_rule_holding_a_scope_class_no_member_has()
    {
        var harness = await CreateAsync();
        var rule = await SeedRuleAsync(harness.Tenant, "Legacy scope", "threshold", "warning", scopeClass: "sideways");
        var classifier = new Mock<IRuleScopeClassifier>();
        classifier.Setup(c => c.Classify(It.IsAny<AlertConditionType>(), It.IsAny<string>())).Returns(RuleScopeClass.Low);
        await using var services = new ServiceCollection()
            .AddSingleton(classifier.Object)
            .AddSingleton<IAlertRepository>(harness.Repository)
            .AddSingleton<IAlertRuleRepository>(harness.Rules)
            .BuildServiceProvider();

        await new RuleScopeClassBackfillJob(NullLogger<RuleScopeClassBackfillJob>.Instance).RunAsync(
            new JobRun(new TenantContext(harness.Tenant, "t", "T", true, IsDemo: false), null, 0, services, DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        (await StoredValuesAsync(harness.Tenant, rule)).ScopeClass.Should().Be("low");
        (await harness.Repository.ReadEnabledRulesAsync(CancellationToken.None)).Rules.Select(r => r.Id).Should().Equal(rule);
    }

    [Theory]
    [InlineData("condition_type", "threshold_low")]
    [InlineData("severity", "normal")]
    [InlineData("scope_class", "sideways")]
    public async Task A_rule_holding_a_value_no_member_has_can_be_deleted(string column, string unreadable)
    {
        var harness = await CreateAsync();
        var rule = await SeedRuleAsync(harness.Tenant, "Legacy", "threshold", "warning");
        await UpdateRuleAsync(harness.Tenant, rule, $"{column} = @value", unreadable);

        var deleted = await harness.Rules.DeleteAsync(rule, CancellationToken.None);

        deleted.Should().BeTrue();
        (await harness.Repository.GetRulesAsync(CancellationToken.None)).Should().BeEmpty();
    }

    private static AlertRuleRetirement Retirement() =>
        new(Mock.Of<IExcursionTracker>(), Mock.Of<IExcursionResolutionHandler>(), Mock.Of<IUnevaluableRuleNotice>());

    private static IRuleScopeClassifier Classifier() =>
        Mock.Of<IRuleScopeClassifier>(c => c.Classify(It.IsAny<AlertConditionType>(), It.IsAny<string>()) == RuleScopeClass.Low);

    /// <summary>The tracker sync, with each managed rule referenced by another rule so a delete disables it.</summary>
    private TrackerAlertRuleSyncService TrackerSync(Harness harness)
    {
        var references = new Mock<IAlertReferenceService>();
        references
            .Setup(r => r.FindReferencingRulesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Guid.NewGuid()]);
        return new TrackerAlertRuleSyncService(
            new SuppliedContextFactory(() => Context(harness.Tenant)), Classifier(), references.Object,
            Rearms.Unannounced(), Retirement(), TimeProvider.System,
            NullLogger<TrackerAlertRuleSyncService>.Instance, AccountLinkRoutes.Off);
    }

    /// <summary>A tracker definition with one threshold, synced to its managed rule.</summary>
    private async Task<(Guid Definition, Guid Rule)> SyncedTrackerRuleAsync(Harness harness)
    {
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = harness.Tenant,
            CreatedBy = "user-1",
            Name = "Sensor",
            Mode = TrackerMode.Duration,
            LifespanHours = 240,
            Category = TrackerCategory.Consumable,
        };
        definition.NotificationThresholds.Add(new TrackerNotificationThresholdEntity
        {
            Id = Guid.NewGuid(),
            TenantId = harness.Tenant,
            TrackerDefinitionId = definition.Id,
            Hours = 24,
            Urgency = NotificationUrgency.Warn,
        });
        await using (var db = Context(harness.Tenant))
        {
            db.TrackerDefinitions.Add(definition);
            await db.SaveChangesAsync();
        }

        await TrackerSync(harness).SyncDefinitionAsync(definition.Id);
        await using var read = Context(harness.Tenant);
        return (definition.Id, (await read.AlertRules.AsNoTracking().SingleAsync()).Id);
    }

    private async Task SetThresholdUrgencyAsync(Guid tenant, NotificationUrgency urgency)
    {
        await using var db = Context(tenant);
        (await db.TrackerNotificationThresholds.SingleAsync()).Urgency = urgency;
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("severity", "normal", "tracker_age", "critical", AlertRuleReviewReasons.SeverityUnrecognized)]
    [InlineData("condition_type", "tracker_age_legacy", "composite", "warning", AlertRuleReviewReasons.ConditionTypeUnrecognized)]
    public async Task A_tracker_disable_of_a_rule_holding_a_value_no_member_has_stores_the_reason_for_its_stand_in(
        string column, string unreadable, string conditionType, string severity, string reason)
    {
        var harness = await CreateAsync();
        var (definition, rule) = await SyncedTrackerRuleAsync(harness);
        await UpdateRuleAsync(harness.Tenant, rule, $"{column} = @value", unreadable);

        await TrackerSync(harness).DeleteRulesForDefinitionAsync(definition);

        var stored = await StoredValuesAsync(harness.Tenant, rule);
        (stored.ConditionType, stored.Severity, stored.ReviewReason).Should().Be((conditionType, severity, reason));
    }

    [Fact]
    public async Task A_tracker_sync_that_sets_a_severity_over_a_stand_in_stores_no_reason()
    {
        var harness = await CreateAsync();
        var (definition, rule) = await SyncedTrackerRuleAsync(harness);
        await UpdateRuleAsync(harness.Tenant, rule, "severity = @value", "normal");

        await TrackerSync(harness).SyncDefinitionAsync(definition);

        (await StoredValuesAsync(harness.Tenant, rule)).Should().Be(("tracker_age", "warning", "low", (string?)null));
    }

    [Fact]
    public async Task A_tracker_sync_whose_severity_is_the_stand_in_stores_the_reason_as_if_it_kept_the_stand_in()
    {
        var harness = await CreateAsync();
        var (definition, rule) = await SyncedTrackerRuleAsync(harness);
        await UpdateRuleAsync(harness.Tenant, rule, "severity = @value", "normal");
        await SetThresholdUrgencyAsync(harness.Tenant, NotificationUrgency.Urgent);

        await TrackerSync(harness).SyncDefinitionAsync(definition);

        (await StoredValuesAsync(harness.Tenant, rule)).Should().Be(
            ("tracker_age", "critical", "low", AlertRuleReviewReasons.SeverityUnrecognized),
            "a sync cannot tell its critical from the stand-in it copied; the owner's next save clears it");
    }

    [Fact]
    public async Task A_starter_save_of_a_rule_with_a_severity_no_member_has_stores_severity_unrecognized()
    {
        var harness = await CreateAsync();
        var contexts = new SuppliedContextFactory(() => Context(harness.Tenant));
        var setup = new AlertSetupService(
            contexts,
            new TenantRepository(contexts),
            Mock.Of<ITenantGlucoseUnits>(u => u.ResolveAsync(It.IsAny<CancellationToken>()) == Task.FromResult("mg/dl")),
            new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
            new SubjectRepository(contexts, AccountLinkRoutes.Off),
            harness.Rules,
            Classifier(),
            Mock.Of<ISecretEncryptionService>(),
            harness.Delivery.Object,
            Rearms.Unannounced(),
            Retirement(),
            TimeProvider.System);
        var request = new SaveAlertSetupRequest
        {
            Rules = [new StarterAlertRuleRequest { Kind = StarterAlertKind.Low, IsEnabled = true, Threshold = 70 }],
            Channels = [new CreateAlertRuleChannelRequest { ChannelType = ChannelType.TelegramDm, Destination = "864203571" }],
        };
        var caller = Guid.NewGuid();
        await setup.SaveAsync(caller, null, request, CancellationToken.None);
        var low = (await harness.Rules.GetStarterRulesAsync(CancellationToken.None))[StarterAlertKind.Low].Rule.Id;
        await UpdateRuleAsync(harness.Tenant, low, "severity = @value", "normal");

        await setup.SaveAsync(caller, null, request, CancellationToken.None);

        var stored = await StoredValuesAsync(harness.Tenant, low);
        (stored.Severity, stored.ReviewReason).Should().Be(("critical", AlertRuleReviewReasons.SeverityUnrecognized));
    }

    [Fact]
    public async Task An_editor_save_of_the_stand_in_severity_stores_no_reason()
    {
        var harness = await CreateAsync();
        var rule = await SeedRuleAsync(harness.Tenant, "Legacy", "threshold", "normal");
        var opened = (await harness.Repository.GetRuleAsync(rule, CancellationToken.None))!;

        await harness.Rules.UpdateAsync(
            rule,
            new AlertRuleWrite(
                opened.Rule.Name, null, AlertConditionType.Threshold, opened.Rule.ConditionParams, RuleScopeClass.Low,
                AlertRuleSeverity.Critical, IsEnabled: true, SortOrder: 1, AllowThroughDnd: false,
                AutoResolveEnabled: false, AutoResolveParams: null, ClientConfiguration: "{}"),
            CancellationToken.None);

        (await StoredValuesAsync(harness.Tenant, rule)).Should().Be(("threshold", "critical", "low", (string?)null),
            "the owner looked at the rule and kept critical");
    }

    private async Task<(string ConditionType, string Severity, string ScopeClass, string? ReviewReason)> StoredValuesAsync(
        Guid tenant, Guid ruleId)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await conn.PinTenantAsync(tenant);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT condition_type, severity, scope_class, review_reason FROM alert_rules WHERE id = '{ruleId}';";
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }
}
