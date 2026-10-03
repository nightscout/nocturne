using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// What the Nightscout connector needs to recognise a record Nightscout write-back sent upstream
    /// when a pull brings its copy back, in one transaction.
    /// <list type="bullet">
    /// <item><c>written_live</c> on every legacy-keyed table: whether a live write has touched the row
    /// (<c>IWriteBackTracked</c>). Only such a row can have been written back, so only its copy is an
    /// echo. Existing rows start false: their copies update them as any upstream record does. A
    /// constant default makes each column a catalog-only change.</item>
    /// <item><c>legacy_id_wire_hash</c>, the hash <c>MongoObjectId.Coerce</c> gives a non-ObjectId
    /// legacy id, declared immutable so an index expression can use it in place of the stable
    /// <c>convert_to</c>.</item>
    /// </list>
    /// The indexes over them are built in <see cref="AddWriteBackIdentityIndexes"/>: a
    /// <c>CONCURRENTLY</c> build cannot run in a transaction, and these columns, added without
    /// <c>IF NOT EXISTS</c>, would crash-loop a restart after an interrupted build if they shared its
    /// non-transactional migration.
    /// </summary>
    public partial class AddWriteBackIdentityTracking : Migration
    {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
