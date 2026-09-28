using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInsulinDeliveryModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_basal_insulin",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "pump_serial",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "pump_type",
                table: "boluses");

            migrationBuilder.RenameColumn(
                name: "pump_id",
                table: "boluses",
                newName: "pump_record_id");

            migrationBuilder.AddColumn<Guid>(
                name: "pump_device_id",
                table: "boluses",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "micro_boluses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    data_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    insulin = table.Column<double>(type: "double precision", nullable: false),
                    sync_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    pump_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pump_record_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_micro_boluses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pump_devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    pump_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    pump_serial = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    first_seen_mills = table.Column<long>(type: "bigint", nullable: false),
                    last_seen_mills = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pump_devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "temp_basals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_mills = table.Column<long>(type: "bigint", nullable: false),
                    end_mills = table.Column<long>(type: "bigint", nullable: true),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    data_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legacy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    rate = table.Column<double>(type: "double precision", nullable: false),
                    scheduled_rate = table.Column<double>(type: "double precision", nullable: true),
                    origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    pump_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pump_record_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_temp_basals", x => x.Id);
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

            migrationBuilder.CreateIndex(
                name: "IX_pump_devices_pump_type_pump_serial",
                table: "pump_devices",
                columns: new[] { "pump_type", "pump_serial" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_correlation_id",
                table: "temp_basals",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_end_mills",
                table: "temp_basals",
                column: "end_mills");

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_legacy_id",
                table: "temp_basals",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_start_mills",
                table: "temp_basals",
                column: "start_mills",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "micro_boluses");

            migrationBuilder.DropTable(
                name: "pump_devices");

            migrationBuilder.DropTable(
                name: "temp_basals");

            migrationBuilder.DropColumn(
                name: "pump_device_id",
                table: "boluses");

            migrationBuilder.RenameColumn(
                name: "pump_record_id",
                table: "boluses",
                newName: "pump_id");

            migrationBuilder.AddColumn<bool>(
                name: "is_basal_insulin",
                table: "boluses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "pump_serial",
                table: "boluses",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pump_type",
                table: "boluses",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }
    }
}
