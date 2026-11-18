using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <remarks>
    /// The backfill reads each row's writer from the mutation audit log, which records the grant of
    /// an insert a credential made unless the save was system-attributed: an uploader's entry and
    /// device-status batches are, so those rows stay without a writer, as does a row older than the
    /// log. A token id that is no grant (a session cookie's JWT id) is skipped.
    /// </remarks>
    public partial class AddWriterGrantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "uploader_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "therapy_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "temp_basals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "target_range_schedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "step_counts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "state_spans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "sensor_glucose",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "sensitivity_schedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "pump_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "profile_activations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "notes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "meter_glucose",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "heart_rates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "device_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "carb_ratio_schedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "carb_intakes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "calibrations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "boluses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "bolus_calculations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "body_weights",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "bg_checks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "basal_schedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "basal_injections",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "writer_grant_id",
                table: "aps_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                    target RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        FOR target IN
                            SELECT * FROM (VALUES
                                ('sensor_glucose', 'SensorGlucose'),
                                ('meter_glucose', 'MeterGlucose'),
                                ('calibrations', 'Calibration'),
                                ('boluses', 'Bolus'),
                                ('carb_intakes', 'CarbIntake'),
                                ('bolus_calculations', 'BolusCalculation'),
                                ('device_events', 'DeviceEvent'),
                                ('bg_checks', 'BGCheck'),
                                ('notes', 'Note'),
                                ('aps_snapshots', 'ApsSnapshot'),
                                ('pump_snapshots', 'PumpSnapshot'),
                                ('uploader_snapshots', 'UploaderSnapshot'),
                                ('temp_basals', 'TempBasal'),
                                ('basal_injections', 'BasalInjection'),
                                ('state_spans', 'StateSpan'),
                                ('profile_activations', 'ProfileActivation'),
                                ('therapy_settings', 'TherapySettings'),
                                ('basal_schedules', 'BasalSchedule'),
                                ('carb_ratio_schedules', 'CarbRatioSchedule'),
                                ('sensitivity_schedules', 'SensitivitySchedule'),
                                ('target_range_schedules', 'TargetRangeSchedule')
                            ) AS pairs(table_name, entity_type)
                        LOOP
                            EXECUTE format($sql$
                                UPDATE %I r
                                SET writer_grant_id = written.token_id
                                FROM (
                                    SELECT DISTINCT ON (a.entity_id) a.entity_id, a.token_id
                                    FROM mutation_audit_log a
                                    WHERE a.tenant_id = $1
                                      AND a.entity_type = $2
                                      AND a.action = 'create'
                                      AND EXISTS (SELECT 1 FROM oauth_grants g WHERE g.id = a.token_id)
                                    ORDER BY a.entity_id, a.created_at
                                ) written
                                WHERE r.id = written.entity_id AND r.tenant_id = $1
                                $sql$, target.table_name)
                            USING t.id, target.entity_type;
                        END LOOP;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "uploader_snapshots");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "therapy_settings");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "target_range_schedules");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "step_counts");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "state_spans");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "sensitivity_schedules");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "profile_activations");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "meter_glucose");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "heart_rates");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "carb_ratio_schedules");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "calibrations");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "body_weights");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "bg_checks");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "basal_schedules");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "basal_injections");

            migrationBuilder.DropColumn(
                name: "writer_grant_id",
                table: "aps_snapshots");
        }
    }
}
