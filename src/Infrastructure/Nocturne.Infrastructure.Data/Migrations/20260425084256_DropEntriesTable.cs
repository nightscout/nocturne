using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropEntriesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    additional_properties = table.Column<string>(type: "jsonb", nullable: true),
                    app = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    data_source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    date_string = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delta = table.Column<double>(type: "double precision", nullable: true),
                    device = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    direction = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    filtered = table.Column<double>(type: "double precision", nullable: true),
                    intercept = table.Column<double>(type: "double precision", nullable: true),
                    is_calibration = table.Column<bool>(type: "boolean", nullable: false),
                    is_read_only = table.Column<bool>(type: "boolean", nullable: true),
                    is_valid = table.Column<bool>(type: "boolean", nullable: true),
                    meta = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "{}"),
                    mgdl = table.Column<double>(type: "double precision", nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    mmol = table.Column<double>(type: "double precision", nullable: true),
                    modified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    noise = table.Column<int>(type: "integer", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    original_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    rssi = table.Column<int>(type: "integer", nullable: true),
                    scale = table.Column<double>(type: "double precision", nullable: true),
                    scaled = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "null"),
                    sgv = table.Column<double>(type: "double precision", nullable: true),
                    slope = table.Column<double>(type: "double precision", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_time = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trend = table.Column<int>(type: "integer", nullable: true),
                    trend_rate = table.Column<double>(type: "double precision", nullable: true),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "sgv"),
                    unfiltered = table.Column<double>(type: "double precision", nullable: true),
                    units = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    utc_offset = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_entries_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_entries_deleted_at",
                table: "entries",
                column: "deleted_at",
                filter: "deleted_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_entries_duplicate_detection",
                table: "entries",
                columns: new[] { "device", "type", "sgv", "mills" });

            migrationBuilder.CreateIndex(
                name: "ix_entries_mills",
                table: "entries",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_entries_sys_created_at",
                table: "entries",
                column: "sys_created_at");

            migrationBuilder.CreateIndex(
                name: "IX_entries_tenant_id",
                table: "entries",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_entries_type",
                table: "entries",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "ix_entries_type_timestamp",
                table: "entries",
                columns: new[] { "type", "mills" },
                descending: new[] { false, true });
        }
    }
}
