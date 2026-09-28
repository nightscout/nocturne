using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CollapseMicroBolusIntoBolus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add bolus_kind discriminator column with default 'Manual'
            migrationBuilder.AddColumn<string>(
                name: "bolus_kind",
                table: "boluses",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Manual");

            // 2. Migrate micro_boluses data into boluses table as Algorithm boluses
            migrationBuilder.Sql("""
                INSERT INTO boluses (
                    "Id", mills, utc_offset, device, app, data_source,
                    correlation_id, legacy_id, sys_created_at, sys_updated_at,
                    insulin, sync_identifier, pump_device_id, pump_record_id,
                    bolus_kind, automatic
                )
                SELECT
                    "Id", mills, utc_offset, device, app, data_source,
                    correlation_id, legacy_id, sys_created_at, sys_updated_at,
                    insulin, sync_identifier, pump_device_id, pump_record_id,
                    'Algorithm', true
                FROM micro_boluses
                """);

            // 3. Drop micro_boluses table (data has been migrated)
            migrationBuilder.DropTable(
                name: "micro_boluses");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bolus_kind",
                table: "boluses");

            migrationBuilder.CreateTable(
                name: "micro_boluses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    data_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    insulin = table.Column<double>(type: "double precision", nullable: false),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    pump_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pump_record_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    sync_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_micro_boluses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_micro_boluses_correlation_id",
                table: "micro_boluses",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_micro_boluses_legacy_id",
                table: "micro_boluses",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_micro_boluses_mills",
                table: "micro_boluses",
                column: "mills",
                descending: new bool[0]);
        }
    }
}
