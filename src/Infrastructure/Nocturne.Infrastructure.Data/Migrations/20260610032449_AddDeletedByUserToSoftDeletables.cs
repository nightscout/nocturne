using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeletedByUserToSoftDeletables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "uploader_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "therapy_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "temp_basals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "target_range_schedules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "sensor_glucose",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "sensitivity_schedules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "pump_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "patient_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "patient_insulins",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "patient_devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "notes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "meter_glucose",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "device_status_extras",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "device_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "decomposition_batches",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "carb_ratio_schedules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "carb_intakes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "calibrations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "boluses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "bolus_calculations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "bg_checks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "basal_schedules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "basal_injections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "aps_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill the dedup flag for existing soft-deleted rows so prior user
            // deletions keep blocking connector resync (parity with the previous
            // audit-row discriminator). Driven from the audit log — only entities with a
            // user-attributed delete are touched, so tables of purely system-swept rows
            // (e.g. temp_basals) cost a single empty index probe. Looped per tenant
            // because both the entity tables and mutation_audit_log are RLS-scoped.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t uuid;
                    m record;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t::text, true);
                        FOR m IN SELECT * FROM (VALUES
                            ('ApsSnapshot','aps_snapshots'),
                            ('BGCheck','bg_checks'),
                            ('BasalSchedule','basal_schedules'),
                            ('BolusCalculation','bolus_calculations'),
                            ('Bolus','boluses'),
                            ('Calibration','calibrations'),
                            ('CarbIntake','carb_intakes'),
                            ('CarbRatioSchedule','carb_ratio_schedules'),
                            ('DeviceEvent','device_events'),
                            ('DeviceStatusExtras','device_status_extras'),
                            ('MeterGlucose','meter_glucose'),
                            ('Note','notes'),
                            ('PumpSnapshot','pump_snapshots'),
                            ('SensitivitySchedule','sensitivity_schedules'),
                            ('SensorGlucose','sensor_glucose'),
                            ('TargetRangeSchedule','target_range_schedules'),
                            ('TempBasal','temp_basals'),
                            ('TherapySettings','therapy_settings'),
                            ('UploaderSnapshot','uploader_snapshots')
                        ) AS v(etype, tbl) LOOP
                            EXECUTE format(
                                'UPDATE %I x SET deleted_by_user = true
                                 WHERE x.deleted_at IS NOT NULL AND x.id IN (
                                   SELECT DISTINCT a.entity_id FROM mutation_audit_log a
                                   WHERE a.entity_type = %L AND a.action = ''delete''
                                     AND a.auth_type IS NOT NULL)',
                                m.tbl, m.etype);
                        END LOOP;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "uploader_snapshots");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "therapy_settings");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "target_range_schedules");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "sensitivity_schedules");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "patient_records");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "patient_insulins");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "patient_devices");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "meter_glucose");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "device_status_extras");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "decomposition_batches");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "carb_ratio_schedules");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "calibrations");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "bg_checks");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "basal_schedules");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "basal_injections");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "aps_snapshots");
        }
    }
}
