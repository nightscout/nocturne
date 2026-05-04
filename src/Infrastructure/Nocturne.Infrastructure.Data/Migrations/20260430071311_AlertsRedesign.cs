using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Alerts redesign: composite condition tree becomes the single top-level rule shape,
    /// confirmation/hysteresis legacy fields fold into composite + sustained nodes and
    /// per-rule auto-resolve params, and a new <c>alert_condition_timers</c> table backs
    /// sustained-condition timer state.
    /// </summary>
    /// <remarks>
    /// Up sequence is: add new columns (so the data rewrite can write them), rewrite
    /// <c>condition_params</c> per tenant under RLS context, rewrite legacy
    /// <c>severity = 'normal'</c> to <c>'warning'</c>, drop legacy columns, create
    /// <c>alert_condition_timers</c>, enable RLS + policy.
    ///
    /// Down restores the legacy columns with sensible defaults and drops the new
    /// columns + table. The JSON data rewrite is intentionally not reversed: a forward
    /// rewrite of every existing rule into composite shape is a one-way migration.
    /// </remarks>
    public partial class AlertsRedesign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add new columns. They sit alongside the legacy columns so the data
            //    rewrite below can read both old and new state in the same UPDATE.
            migrationBuilder.AddColumn<bool>(
                name: "auto_resolve_enabled",
                table: "alert_rules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "auto_resolve_params",
                table: "alert_rules",
                type: "jsonb",
                nullable: true);

            // 2. Per-tenant data rewrite. alert_rules has FORCE ROW LEVEL SECURITY,
            //    so the migrator obeys the tenant_isolation policy too. We loop over
            //    distinct tenants, set app.current_tenant_id, then UPDATE within scope.
            //
            //    JSON shape produced (per legacy condition_type):
            //
            //      threshold     -> leaf = {type: "threshold", threshold: <existing>}
            //      rate_of_change-> leaf = {type: "rate_of_change", rate_of_change: <existing>}
            //      signal_loss   -> leaf = {type: "staleness",
            //                               staleness: {operator: ">",
            //                                           value: existing.timeout_minutes}}
            //      composite     -> leaf = <existing>  (already {operator, conditions})
            //
            //    If confirmation_readings > 1, the leaf is wrapped in a sustained node:
            //      sustained_leaf = {type: "sustained",
            //                        sustained: {minutes: confirmation_readings * 5,
            //                                    child: leaf}}
            //
            //    The new top-level shape is always composite (except for the
            //    already-composite case, which is kept as-is to avoid the redundant
            //    composite{and, [composite{...}]} wrap):
            //      condition_params = {operator: "and", conditions: [<sustained_leaf>]}
            //
            //    auto_resolve_params (when hysteresis_minutes > 0):
            //      {type: "sustained",
            //       sustained: {minutes: hysteresis_minutes,
            //                   child: {type: "not", not: {child: <leaf>}}}}
            //
            //    For composite rules, auto_resolve is intentionally skipped: the
            //    semantics of "not" applied to a multi-condition composite are murky
            //    and the legacy behaviour was an opaque hysteresis window over the
            //    whole composite that does not cleanly decompose into the new model.
            //    Composite-rule owners will need to opt back in via the editor.
            //
            //    JSON keys are snake_case to match the runtime serializer
            //    (JsonNamingPolicy.SnakeCaseLower).
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t_id uuid;
                BEGIN
                    FOR t_id IN SELECT DISTINCT tenant_id FROM alert_rules
                    LOOP
                        PERFORM set_config('app.current_tenant_id', t_id::text, true);

                        UPDATE alert_rules
                           SET condition_params = CASE
                                   WHEN condition_type = 'composite' THEN condition_params
                                   ELSE jsonb_build_object(
                                       'operator', 'and',
                                       'conditions', jsonb_build_array(
                                           CASE WHEN confirmation_readings > 1 THEN
                                               jsonb_build_object(
                                                   'type', 'sustained',
                                                   'sustained', jsonb_build_object(
                                                       'minutes', confirmation_readings * 5,
                                                       'child', CASE condition_type
                                                           WHEN 'threshold' THEN jsonb_build_object(
                                                               'type', 'threshold',
                                                               'threshold', condition_params)
                                                           WHEN 'rate_of_change' THEN jsonb_build_object(
                                                               'type', 'rate_of_change',
                                                               'rate_of_change', condition_params)
                                                           WHEN 'signal_loss' THEN jsonb_build_object(
                                                               'type', 'staleness',
                                                               'staleness', jsonb_build_object(
                                                                   'operator', '>',
                                                                   'value', COALESCE(
                                                                       (condition_params->>'timeout_minutes')::int,
                                                                       (condition_params->>'timeoutMinutes')::int,
                                                                       15)))
                                                           ELSE condition_params
                                                       END))
                                           ELSE
                                               CASE condition_type
                                                   WHEN 'threshold' THEN jsonb_build_object(
                                                       'type', 'threshold',
                                                       'threshold', condition_params)
                                                   WHEN 'rate_of_change' THEN jsonb_build_object(
                                                       'type', 'rate_of_change',
                                                       'rate_of_change', condition_params)
                                                   WHEN 'signal_loss' THEN jsonb_build_object(
                                                       'type', 'staleness',
                                                       'staleness', jsonb_build_object(
                                                           'operator', '>',
                                                           'value', COALESCE(
                                                               (condition_params->>'timeout_minutes')::int,
                                                               (condition_params->>'timeoutMinutes')::int,
                                                               15)))
                                                   ELSE condition_params
                                               END
                                           END))
                               END,
                               auto_resolve_enabled = (
                                   hysteresis_minutes > 0
                                   AND condition_type IN ('threshold', 'rate_of_change', 'signal_loss')
                               ),
                               auto_resolve_params = CASE
                                   WHEN hysteresis_minutes > 0
                                        AND condition_type IN ('threshold', 'rate_of_change', 'signal_loss') THEN
                                       jsonb_build_object(
                                           'type', 'sustained',
                                           'sustained', jsonb_build_object(
                                               'minutes', hysteresis_minutes,
                                               'child', jsonb_build_object(
                                                   'type', 'not',
                                                   'not', jsonb_build_object(
                                                       'child', CASE condition_type
                                                           WHEN 'threshold' THEN jsonb_build_object(
                                                               'type', 'threshold',
                                                               'threshold', condition_params)
                                                           WHEN 'rate_of_change' THEN jsonb_build_object(
                                                               'type', 'rate_of_change',
                                                               'rate_of_change', condition_params)
                                                           WHEN 'signal_loss' THEN jsonb_build_object(
                                                               'type', 'staleness',
                                                               'staleness', jsonb_build_object(
                                                                   'operator', '>',
                                                                   'value', COALESCE(
                                                                       (condition_params->>'timeout_minutes')::int,
                                                                       (condition_params->>'timeoutMinutes')::int,
                                                                       15)))
                                                       END))))
                                   ELSE NULL
                               END,
                               condition_type = 'composite'
                         WHERE tenant_id = t_id;
                    END LOOP;
                END $$;
                """);

            // 3. Severity rewrite: the legacy 'normal' value was renamed to 'warning'
            //    when the AlertRuleSeverity enum dropped its old default. RLS scopes
            //    UPDATE to whatever GUC is set, but at this point the loop above has
            //    rewritten every tenant's rules; running once with no GUC would skip
            //    everything, so do this inside a per-tenant loop too.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t_id uuid;
                BEGIN
                    FOR t_id IN SELECT DISTINCT tenant_id FROM alert_rules
                    LOOP
                        PERFORM set_config('app.current_tenant_id', t_id::text, true);
                        UPDATE alert_rules
                           SET severity = 'warning'
                         WHERE severity = 'normal'
                           AND tenant_id = t_id;
                    END LOOP;
                END $$;
                """);

            // 4. Drop legacy columns now that the data rewrite has consumed them.
            migrationBuilder.DropColumn(
                name: "confirmation_readings",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "hysteresis_minutes",
                table: "alert_rules");

            // 5. Create the sustained-condition timer table. Composite PK matches the
            //    entity's (AlertRuleId, ConditionPath) — alert_rule_id, not rule_id.
            migrationBuilder.CreateTable(
                name: "alert_condition_timers",
                columns: table => new
                {
                    alert_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    condition_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_true_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_condition_timers", x => new { x.alert_rule_id, x.condition_path });
                    table.ForeignKey(
                        name: "FK_alert_condition_timers_alert_rules_alert_rule_id",
                        column: x => x.alert_rule_id,
                        principalTable: "alert_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_alert_condition_timers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_condition_timers_tenant_id",
                table: "alert_condition_timers",
                column: "tenant_id");

            // 6. Enable RLS + tenant_isolation policy. Single combined USING + WITH CHECK
            //    policy, matching the codebase pattern (see DecompositionBatchRls).
            migrationBuilder.Sql("ALTER TABLE alert_condition_timers ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE alert_condition_timers FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS tenant_isolation ON alert_condition_timers;
                CREATE POLICY tenant_isolation ON alert_condition_timers
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down does not attempt to reverse the JSON data rewrite or severity
            // remap. Schema-only restore: drop the new table + columns, recreate the
            // legacy columns with sensible defaults.
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON alert_condition_timers;");
            migrationBuilder.Sql("ALTER TABLE alert_condition_timers NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE alert_condition_timers DISABLE ROW LEVEL SECURITY;");
            migrationBuilder.DropTable(
                name: "alert_condition_timers");

            migrationBuilder.DropColumn(
                name: "auto_resolve_enabled",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "auto_resolve_params",
                table: "alert_rules");

            migrationBuilder.AddColumn<int>(
                name: "confirmation_readings",
                table: "alert_rules",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "hysteresis_minutes",
                table: "alert_rules",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
