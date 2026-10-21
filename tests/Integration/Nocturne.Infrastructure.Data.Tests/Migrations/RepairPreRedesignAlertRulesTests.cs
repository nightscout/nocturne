using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>RepairPreRedesignAlertRules</c> over alert rules in the shapes the setup wizard and the
/// rule editor stored before the alerts redesign, on a real PostgreSQL under FORCE RLS.
/// </summary>
/// <remarks>
/// <see cref="Wizard"/> holds what the setup wizard wrote before the enum columns, including a
/// <c>threshold_low</c> condition type no enum member has. <see cref="Editor"/> holds what the
/// rule editor and the later wizard wrote, beside rules already in today's shapes. The migration
/// runs, is rolled back and runs again, so <see cref="Rerun"/> shows a second pass changes nothing.
/// </remarks>
public class RepairPreRedesignAlertRulesFixture : IAsyncLifetime
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20261021145304_AddTherapySettingsDocumentDefaultProfile";

    private const string UnderTest = "20261021161830_RepairPreRedesignAlertRules";

    private string _migratorConnectionString = string.Empty;

    internal static readonly Guid Wizard = Guid.Parse("85385385-0000-7000-8000-00000000000a");
    internal static readonly Guid Editor = Guid.Parse("85385385-0000-7000-8000-00000000000b");

    /// <summary>An instance where AlertsRedesign did run, holding what it wrote.</summary>
    internal static readonly Guid Redesigned = Guid.Parse("85385385-0000-7000-8000-00000000000c");

    private static readonly Guid[] Tenants = [Wizard, Editor, Redesigned];

    internal static readonly Guid RanUrgentLow = Rule(21);
    internal static readonly Guid RanSustainedLow = Rule(22);
    internal static readonly Guid RanThresholdWithAutoResolve = Rule(23);
    internal static readonly Guid RanSensorLost = Rule(24);
    internal static readonly Guid OldEditorLow = Rule(25);
    internal static readonly Guid MmolLow = Rule(26);
    internal static readonly Guid FractionalSignalLoss = Rule(27);
    internal static readonly Guid StringThreshold = Rule(28);
    internal static readonly Guid CamelCaseChild = Rule(29);
    internal static readonly Guid MissingThreshold = Rule(30);
    internal static readonly Guid MissingNestedThreshold = Rule(31);

    internal static readonly Guid UrgentLow = Rule(1);
    internal static readonly Guid High = Rule(2);
    internal static readonly Guid FastDrop = Rule(3);
    internal static readonly Guid SensorLost = Rule(4);
    internal static readonly Guid UnknownType = Rule(5);
    internal static readonly Guid UnknownSeverity = Rule(6);
    internal static readonly Guid NullParams = Rule(7);

    internal static readonly Guid EditorLow = Rule(11);
    internal static readonly Guid EditorRise = Rule(12);
    internal static readonly Guid EditorLoss = Rule(13);
    internal static readonly Guid EnumWizardFastDrop = Rule(14);
    internal static readonly Guid CurrentComposite = Rule(15);
    internal static readonly Guid CurrentStaleness = Rule(16);
    internal static readonly Guid CurrentLow = Rule(17);

    private static Guid Rule(int n) => Guid.Parse($"85385385-0000-7000-8000-0000000001{n:D2}");

    /// <summary>What reading the wizard tenant's rule enums through EF threw before the migration.</summary>
    internal Exception? ReadBefore { get; private set; }

    internal IReadOnlyDictionary<Guid, StoredRule> FirstRun { get; private set; } = new Dictionary<Guid, StoredRule>();

    internal IReadOnlyDictionary<Guid, StoredRule> Rerun { get; private set; } = new Dictionary<Guid, StoredRule>();

    internal IReadOnlyList<(AlertConditionType Type, AlertRuleSeverity Severity)> ReadAfter { get; private set; } = [];

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("repair_pre_redesign_alert_rules");
        _migratorConnectionString = database.MigratorConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        ReadBefore = (await ReadEnumsThroughEfAsync(Wizard)).Exception;

        await MigrateToAsync(UnderTest);
        FirstRun = await RulesAsync();
        var read = new List<(AlertConditionType, AlertRuleSeverity)>();
        foreach (var tenantId in Tenants)
            read.AddRange((await ReadEnumsThroughEfAsync(tenantId)).Rows ?? []);
        ReadAfter = read;

        await MigrateToAsync(PriorMigration);
        await MigrateToAsync(UnderTest);
        Rerun = await RulesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string targetMigration)
    {
        await using var context = new NocturneDbContext(Options());
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private DbContextOptions<NocturneDbContext> Options() =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

    private async Task<(List<(AlertConditionType, AlertRuleSeverity)>? Rows, Exception? Exception)> ReadEnumsThroughEfAsync(Guid tenantId)
    {
        await using var context = new NocturneDbContext(Options());
        context.TenantId = tenantId;
        try
        {
            var rows = await context.AlertRules
                .AsNoTracking()
                .Select(r => new { r.ConditionType, r.Severity })
                .ToListAsync();
            return (rows.Select(r => (r.ConditionType, r.Severity)).ToList(), null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return (null, ex);
        }
    }

    private async Task<Dictionary<Guid, StoredRule>> RulesAsync()
    {
        var rules = new Dictionary<Guid, StoredRule>();
        foreach (var tenantId in Tenants)
        {
            await using var conn = new NpgsqlConnection(_migratorConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT set_config('app.current_tenant_id', '{tenantId}', false);
                SELECT id, condition_type, condition_params::text, severity, is_enabled, review_reason,
                       auto_resolve_params::text, pre_repair::text
                  FROM alert_rules;
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.NextResultAsync();
            while (await reader.ReadAsync())
            {
                rules[reader.GetGuid(0)] = new StoredRule(
                    reader.GetString(1),
                    JsonNode.Parse(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetBoolean(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : JsonNode.Parse(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : JsonNode.Parse(reader.GetString(7)));
            }
        }
        return rules;
    }

    private async Task ExecuteAsync(Guid tenantId, string sql)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT set_config('app.current_tenant_id', '{tenantId}', false); {sql}";
        await cmd.ExecuteNonQueryAsync();
    }

    private static string RuleRow(
        Guid tenantId, Guid ruleId, string conditionType, string conditionParams, string severity,
        string? autoResolve = null) => $"""
        INSERT INTO alert_rules (id, tenant_id, name, condition_type, condition_params, auto_resolve_enabled,
                                 auto_resolve_params, severity, client_configuration, is_enabled, allow_through_dnd,
                                 scope_class, sort_order, created_at, updated_at)
        VALUES ('{ruleId}', '{tenantId}', 'Rule', '{conditionType}', '{conditionParams}'::jsonb, {(autoResolve is null ? "false" : "true")},
                {(autoResolve is null ? "NULL" : $"'{autoResolve}'::jsonb")}, '{severity}', '{"{}"}', true, false,
                'undirected', 0, now() - interval '200 days', now() - interval '200 days');
        """;

    private async Task SeedAsync()
    {
        await ExecuteAsync(Guid.Empty, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{Wizard}', 'wizard-853', 'Wizard', true, now(), now()),
              ('{Editor}', 'editor-853', 'Editor', true, now(), now()),
              ('{Redesigned}', 'redesigned-853', 'Redesigned', true, now(), now());
            """);

        const string ranAutoResolve = """{"type": "sustained", "sustained": {"minutes": 15, "child": {"type": "not", "not": {"child": {"type": "threshold", "threshold": {"threshold": 54, "direction": "below"}}}}}}""";
        await ExecuteAsync(Redesigned, string.Concat(
            RuleRow(Redesigned, RanUrgentLow, "composite", """{"operator": "and", "conditions": [{"threshold": 54, "direction": "below"}]}""", "critical"),
            RuleRow(Redesigned, RanSustainedLow, "composite", """{"operator": "and", "conditions": [{"type": "sustained", "sustained": {"minutes": 10, "child": {"threshold": 70, "direction": "below"}}}]}""", "warning"),
            RuleRow(Redesigned, RanThresholdWithAutoResolve, "composite", """{"operator": "and", "conditions": [{"type": "threshold", "threshold": {"threshold": 54, "direction": "below"}}]}""", "critical", ranAutoResolve),
            RuleRow(Redesigned, RanSensorLost, "composite", """{"operator": "and", "conditions": [{"type": "staleness", "staleness": {"operator": ">", "value": 15}}]}""", "warning"),
            RuleRow(Redesigned, OldEditorLow, "threshold_low", """{"direction": "below", "value": 70, "threshold": 70}""", "warning"),
            RuleRow(Redesigned, MmolLow, "threshold_low", """{"direction": "below", "value": 3.9, "threshold": 3.9}""", "critical"),
            RuleRow(Redesigned, FractionalSignalLoss, "signal_loss", """{"minutes": 12.6}""", "warning"),
            RuleRow(Redesigned, StringThreshold, "threshold_low", """{"threshold": "54", "direction": "below"}""", "critical"),
            RuleRow(Redesigned, CamelCaseChild, "composite", """{"operator": "or", "conditions": [{"type": "RateOfChange", "rateOfChange": {"direction": "falling", "rate": 2}}]}""", "warning"),
            RuleRow(Redesigned, MissingThreshold, "threshold_low", """{"direction": "below", "value": null, "threshold": null}""", "critical"),
            RuleRow(Redesigned, MissingNestedThreshold, "composite", """{"conditions": [{"type": "threshold_low", "threshold": {"direction": "below"}}]}""", "critical")));

        (Guid Id, string Type, string Params, string Severity)[] wizard =
        [
            (UrgentLow, "threshold_low", """{"threshold": 54, "direction": "below"}""", "critical"),
            (High, "threshold_high", """{"threshold": 250, "direction": "above"}""", "normal"),
            (FastDrop, "composite", """{"conditions": [{"type": "threshold_low", "threshold": 100}, {"type": "rate_of_change", "rateThreshold": 3.0, "direction": "falling"}]}""", "normal"),
            (SensorLost, "signal_loss", """{"minutes": 15}""", "normal"),
            (UnknownType, "glucose_spike", """{"spike": 40}""", "critical"),
            (UnknownSeverity, "threshold", """{"direction": "below", "value": 60}""", "urgent"),
            (NullParams, "threshold_high", "null", "normal"),
        ];
        (Guid Id, string Type, string Params, string Severity)[] editor =
        [
            (EditorLow, "threshold", """{"direction": "below", "value": 70, "threshold": 70}""", "warning"),
            (EditorRise, "rate_of_change", """{"direction": "rising", "rate": 2, "rateThreshold": 2}""", "warning"),
            (EditorLoss, "signal_loss", """{"timeout_minutes": 20, "minutes": 20}""", "warning"),
            (EnumWizardFastDrop, "composite", """{"conditions": [{"type": "threshold", "threshold": 100, "direction": "below"}, {"type": "rate_of_change", "rateThreshold": 3.0, "direction": "falling"}]}""", "warning"),
            (CurrentComposite, "composite", """{"operator": "or", "conditions": [{"type": "sustained", "sustained": {"minutes": 15, "child": {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}}, {"type": "signal_loss", "signal_loss": {"timeout_minutes": 20}}]}""", "critical"),
            (CurrentStaleness, "staleness", """{"operator": ">", "value": 20}""", "info"),
            (CurrentLow, "threshold", """{"direction": "below", "value": 70}""", "warning"),
        ];

        await ExecuteAsync(Wizard, string.Concat(wizard.Select(r => RuleRow(Wizard, r.Id, r.Type, r.Params, r.Severity))));
        await ExecuteAsync(Editor, string.Concat(editor.Select(r => RuleRow(Editor, r.Id, r.Type, r.Params, r.Severity))));
    }
}

/// <summary>An alert rule's repaired columns, read under its tenant's context.</summary>
public sealed record StoredRule(
    string ConditionType, JsonNode? ConditionParams, string Severity, bool IsEnabled, string? ReviewReason,
    JsonNode? AutoResolveParams, JsonNode? PreRepair);

/// <inheritdoc cref="RepairPreRedesignAlertRulesFixture"/>
[Trait("Category", "Integration")]
public class RepairPreRedesignAlertRulesTests(RepairPreRedesignAlertRulesFixture fixture)
    : IClassFixture<RepairPreRedesignAlertRulesFixture>
{
    private const string Converted = AlertRuleReviewReasons.LegacyConditionConverted;

    private StoredRule After(Guid ruleId) => fixture.FirstRun[ruleId];

    private static void ShouldHold(StoredRule rule, string conditionType, string conditionParams)
    {
        rule.ConditionType.Should().Be(conditionType);
        JsonNode.DeepEquals(rule.ConditionParams, JsonNode.Parse(conditionParams)).Should()
            .BeTrue($"{rule.ConditionParams?.ToJsonString()} should equal {conditionParams}");
    }

    [Fact]
    public void Before_the_repair_one_legacy_condition_type_stops_every_rule_of_its_tenant_being_read()
    {
        fixture.ReadBefore.Should().NotBeNull();
    }

    [Fact]
    public void After_the_repair_every_rule_of_every_tenant_is_read()
    {
        fixture.ReadAfter.Should().HaveCount(25);
    }

    [Fact]
    public void A_repaired_rule_keeps_what_it_held_before()
    {
        JsonNode.DeepEquals(After(RepairPreRedesignAlertRulesFixture.High).PreRepair, JsonNode.Parse("""
            {"condition_type": "threshold_high", "condition_params": {"threshold": 250, "direction": "above"},
             "auto_resolve_enabled": false, "auto_resolve_params": null, "severity": "normal", "is_enabled": true}
            """)).Should().BeTrue(After(RepairPreRedesignAlertRulesFixture.High).PreRepair?.ToJsonString());
        After(RepairPreRedesignAlertRulesFixture.CurrentLow).PreRepair.Should().BeNull();
    }

    [Fact]
    public void A_type_less_threshold_alerts_redesign_wrapped_into_a_composite_becomes_a_threshold()
    {
        var rule = After(RepairPreRedesignAlertRulesFixture.RanUrgentLow);
        ShouldHold(rule, "composite", """
            {"operator": "and", "conditions": [{"type": "threshold", "threshold": {"direction": "below", "value": 54}}]}
            """);
        rule.ReviewReason.Should().Be(Converted);
    }

    [Fact]
    public void A_type_less_threshold_under_alerts_redesigns_sustained_becomes_a_threshold()
    {
        ShouldHold(After(RepairPreRedesignAlertRulesFixture.RanSustainedLow), "composite", """
            {"operator": "and", "conditions": [{"type": "sustained", "sustained": {"minutes": 10,
              "child": {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}}]}
            """);
    }

    [Fact]
    public void A_threshold_alerts_redesign_wrapped_without_a_value_is_repaired_in_its_auto_resolve_tree_too()
    {
        var rule = After(RepairPreRedesignAlertRulesFixture.RanThresholdWithAutoResolve);
        ShouldHold(rule, "composite", """
            {"operator": "and", "conditions": [{"type": "threshold", "threshold": {"direction": "below", "value": 54}}]}
            """);
        JsonNode.DeepEquals(rule.AutoResolveParams, JsonNode.Parse("""
            {"type": "sustained", "sustained": {"minutes": 15, "child": {"type": "not", "not": {"child":
              {"type": "threshold", "threshold": {"direction": "below", "value": 54}}}}}}
            """)).Should().BeTrue(rule.AutoResolveParams?.ToJsonString());
    }

    [Fact]
    public void Alerts_redesigns_staleness_is_already_readable_and_left_alone()
    {
        After(RepairPreRedesignAlertRulesFixture.RanSensorLost).ReviewReason.Should().BeNull();
    }

    [Fact]
    public void The_old_editors_threshold_low_drops_its_alias()
    {
        var rule = After(RepairPreRedesignAlertRulesFixture.OldEditorLow);
        ShouldHold(rule, "threshold", """{"direction": "below", "value": 70}""");
        rule.ReviewReason.Should().Be(Converted);
    }

    [Fact]
    public void A_threshold_below_40_mg_dl_is_flagged_as_implausible()
    {
        var rule = After(RepairPreRedesignAlertRulesFixture.MmolLow);
        ShouldHold(rule, "threshold", """{"direction": "below", "value": 3.9}""");
        rule.ReviewReason.Should().Be(AlertRuleReviewReasons.LegacyThresholdImplausible);
    }

    [Fact]
    public void A_fractional_timeout_is_rounded_down_so_signal_loss_only_fires_sooner()
    {
        ShouldHold(After(RepairPreRedesignAlertRulesFixture.FractionalSignalLoss), "signal_loss", """{"timeout_minutes": 12}""");
    }

    [Fact]
    public void A_threshold_with_no_value_is_flagged_as_implausible()
    {
        var rule = After(RepairPreRedesignAlertRulesFixture.MissingThreshold);
        rule.ConditionType.Should().Be("threshold");
        rule.ReviewReason.Should().Be(AlertRuleReviewReasons.LegacyThresholdImplausible);
    }

    [Fact]
    public void A_nested_threshold_with_no_value_is_flagged_as_implausible()
    {
        After(RepairPreRedesignAlertRulesFixture.MissingNestedThreshold).ReviewReason
            .Should().Be(AlertRuleReviewReasons.LegacyThresholdImplausible);
    }

    [Fact]
    public void A_numeric_string_threshold_becomes_a_number()
    {
        ShouldHold(After(RepairPreRedesignAlertRulesFixture.StringThreshold), "threshold", """{"direction": "below", "value": 54}""");
    }

    [Fact]
    public void A_camel_case_kind_gets_its_wire_name_and_payload_key()
    {
        ShouldHold(After(RepairPreRedesignAlertRulesFixture.CamelCaseChild), "composite", """
            {"operator": "or", "conditions": [{"type": "rate_of_change", "rate_of_change": {"direction": "falling", "rate": 2}}]}
            """);
    }

    [Fact]
    public void A_wizard_urgent_low_gets_the_value_and_direction_it_was_set_with()
    {
        var rule = After(RepairPreRedesignAlertRulesFixture.UrgentLow);
        ShouldHold(rule, "threshold", """{"direction": "below", "value": 54}""");
        rule.Severity.Should().Be("critical");
        rule.IsEnabled.Should().BeTrue();
        rule.ReviewReason.Should().Be(Converted);
    }

    [Fact]
    public void A_wizard_high_is_converted_and_its_normal_severity_becomes_warning()
    {
        var rule = After(RepairPreRedesignAlertRulesFixture.High);
        ShouldHold(rule, "threshold", """{"direction": "above", "value": 250}""");
        rule.Severity.Should().Be("warning");
        rule.ReviewReason.Should().Be(Converted);
    }

    [Fact]
    public void A_wizard_fast_drop_becomes_an_and_of_canonical_children()
    {
        var rule = After(RepairPreRedesignAlertRulesFixture.FastDrop);
        ShouldHold(rule, "composite", """
            {"operator": "and", "conditions": [
              {"type": "threshold", "threshold": {"direction": "below", "value": 100}},
              {"type": "rate_of_change", "rate_of_change": {"direction": "falling", "rate": 3.0}}
            ]}
            """);
        rule.ReviewReason.Should().Be(Converted);
    }

    [Fact]
    public void A_wizard_signal_loss_gets_its_timeout()
    {
        var rule = After(RepairPreRedesignAlertRulesFixture.SensorLost);
        ShouldHold(rule, "signal_loss", """{"timeout_minutes": 15}""");
        rule.ReviewReason.Should().Be(Converted);
    }

    [Fact]
    public void An_unknown_condition_type_is_disabled_with_its_parameters_kept()
    {
        var rule = After(RepairPreRedesignAlertRulesFixture.UnknownType);
        ShouldHold(rule, "composite", """{"spike": 40}""");
        rule.IsEnabled.Should().BeFalse();
        rule.Severity.Should().Be("critical");
        rule.ReviewReason.Should().Be(AlertRuleReviewReasons.ConditionTypeUnrecognized);
    }

    [Fact]
    public void An_unknown_severity_becomes_critical()
    {
        var rule = After(RepairPreRedesignAlertRulesFixture.UnknownSeverity);
        ShouldHold(rule, "threshold", """{"direction": "below", "value": 60}""");
        rule.Severity.Should().Be("critical");
        rule.IsEnabled.Should().BeTrue();
        rule.ReviewReason.Should().Be(AlertRuleReviewReasons.SeverityUnrecognized);
    }

    [Fact]
    public void A_legacy_type_with_no_parameters_still_becomes_readable()
    {
        var rule = After(RepairPreRedesignAlertRulesFixture.NullParams);
        rule.ConditionType.Should().Be("threshold");
        rule.ReviewReason.Should().Be(AlertRuleReviewReasons.LegacyThresholdImplausible, "it has no threshold at all");
    }

    [Fact]
    public void The_editors_alias_keys_are_dropped()
    {
        ShouldHold(After(RepairPreRedesignAlertRulesFixture.EditorLow), "threshold", """{"direction": "below", "value": 70}""");
        ShouldHold(After(RepairPreRedesignAlertRulesFixture.EditorRise), "rate_of_change", """{"direction": "rising", "rate": 2}""");
        ShouldHold(After(RepairPreRedesignAlertRulesFixture.EditorLoss), "signal_loss", """{"timeout_minutes": 20}""");
        After(RepairPreRedesignAlertRulesFixture.EditorLow).ReviewReason.Should().Be(Converted);
    }

    [Fact]
    public void The_later_wizards_flat_children_are_nested()
    {
        ShouldHold(After(RepairPreRedesignAlertRulesFixture.EnumWizardFastDrop), "composite", """
            {"operator": "and", "conditions": [
              {"type": "threshold", "threshold": {"direction": "below", "value": 100}},
              {"type": "rate_of_change", "rate_of_change": {"direction": "falling", "rate": 3.0}}
            ]}
            """);
    }

    [Fact]
    public void Rules_already_in_todays_shapes_are_left_alone()
    {
        var composite = After(RepairPreRedesignAlertRulesFixture.CurrentComposite);
        ShouldHold(composite, "composite", """
            {"operator": "or", "conditions": [
              {"type": "sustained", "sustained": {"minutes": 15, "child": {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}},
              {"type": "signal_loss", "signal_loss": {"timeout_minutes": 20}}
            ]}
            """);
        composite.ReviewReason.Should().BeNull();
        composite.Severity.Should().Be("critical");

        var staleness = After(RepairPreRedesignAlertRulesFixture.CurrentStaleness);
        ShouldHold(staleness, "staleness", """{"operator": ">", "value": 20}""");
        staleness.ReviewReason.Should().BeNull();
        staleness.Severity.Should().Be("info");

        After(RepairPreRedesignAlertRulesFixture.CurrentLow).ReviewReason.Should().BeNull();
    }

    [Fact]
    public void A_second_run_changes_nothing()
    {
        fixture.Rerun.Should().HaveCount(fixture.FirstRun.Count);
        foreach (var (id, first) in fixture.FirstRun)
        {
            var again = fixture.Rerun[id];
            again.ConditionType.Should().Be(first.ConditionType);
            JsonNode.DeepEquals(again.ConditionParams, first.ConditionParams).Should().BeTrue(
                $"rule {id} held {first.ConditionParams?.ToJsonString()} and then {again.ConditionParams?.ToJsonString()}");
            again.Severity.Should().Be(first.Severity);
            again.IsEnabled.Should().Be(first.IsEnabled);
            JsonNode.DeepEquals(again.AutoResolveParams, first.AutoResolveParams).Should().BeTrue();
            again.ReviewReason.Should().BeNull("a rule the first run repaired needs nothing the second time");
            again.PreRepair.Should().BeNull();
        }
    }
}
