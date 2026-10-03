using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// What the Nightscout connector needs to recognise a record Nightscout write-back sent upstream
    /// when a pull brings its copy back.
    /// <list type="bullet">
    /// <item><c>written_live</c> on every legacy-keyed table: whether a live write has touched the row
    /// (<c>IWriteBackTracked</c>). Only such a row can have been written back, so only its copy is an
    /// echo. Existing rows start false: their copies update them as any upstream record does.</item>
    /// <item>A partial expression index per table for each id form its records go upstream under,
    /// so the pull resolves one without scanning the tenant's rows: the 24-hex prefix of a
    /// uuid-shaped legacy id (<c>UuidLegacyIdPrefix</c>) on every table, and the hash
    /// <c>MongoObjectId.Coerce</c> gives any other non-ObjectId legacy id (<c>HashedLegacyId</c>) on
    /// the treatment tables, through the immutable <c>legacy_id_wire_hash</c> an index expression
    /// needs in place of the stable <c>convert_to</c>.</item>
    /// </list>
    /// The indexes are non-unique, so no existing row can fail the build, and built through
    /// <see cref="ConcurrentIndexBuilder"/>. Their expressions and predicates are written out here,
    /// frozen, and must stay identical to <c>UuidLegacyIdPrefix</c> and <c>HashedLegacyId</c>, whose
    /// queries the planner matches to them.
    /// </summary>
    public partial class AddWriteBackIdentityTracking : Migration
    {
        private static readonly string[] EntryAndStatusTables =
        [
            "sensor_glucose",
            "meter_glucose",
            "calibrations",
            "aps_snapshots",
            "pump_snapshots",
            "uploader_snapshots",
        ];

        private static readonly string[] TreatmentTables =
        [
            "boluses",
            "carb_intakes",
            "bg_checks",
            "notes",
            "device_events",
            "bolus_calculations",
            "temp_basals",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "uploader_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "temp_basals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "sensor_glucose",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "pump_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "notes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "meter_glucose",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "device_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "carb_intakes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "calibrations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "boluses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "bolus_calculations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "bg_checks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "written_live",
                table: "aps_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.legacy_id_wire_hash(legacy_id text) RETURNS text
                LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
                AS $$ SELECT left(encode(sha256(convert_to(legacy_id, 'UTF8')), 'hex'), 24) $$;
                """);

            foreach (var table in EntryAndStatusTables.Concat(TreatmentTables))
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_uuid_legacy_id_prefix",
                    $"ON {table} (tenant_id, left(regexp_replace(lower(legacy_id), '[^0-9a-f]', '', 'g'), 24)) "
                    + "WHERE legacy_id ~* '^[{(]?[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}[})]?$'");
            }

            foreach (var table in TreatmentTables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_hashed_legacy_id",
                    $"ON {table} (tenant_id, public.legacy_id_wire_hash(legacy_id)) "
                    + "WHERE legacy_id !~ '^[0-9a-f]{24}$'");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TreatmentTables)
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_hashed_legacy_id");

            foreach (var table in EntryAndStatusTables.Concat(TreatmentTables))
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_uuid_legacy_id_prefix");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.legacy_id_wire_hash(text);");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "uploader_snapshots");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "meter_glucose");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "calibrations");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "bg_checks");

            migrationBuilder.DropColumn(
                name: "written_live",
                table: "aps_snapshots");
        }
    }
}
