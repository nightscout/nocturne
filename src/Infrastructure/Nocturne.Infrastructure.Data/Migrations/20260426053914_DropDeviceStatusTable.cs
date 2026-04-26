using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropDeviceStatusTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "devicestatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "devicestatus",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    additional_properties = table.Column<string>(type: "jsonb", nullable: true),
                    cgm = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "null"),
                    connect = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "null"),
                    created_at = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    device = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    insulin_pen = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "null"),
                    is_charging = table.Column<bool>(type: "boolean", nullable: true),
                    loop = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "null"),
                    meter = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "null"),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    openaps = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "null"),
                    original_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    @override = table.Column<string>(name: "override", type: "jsonb", nullable: true, defaultValue: "null"),
                    pump = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "null"),
                    radio_adapter = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "null"),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploader = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "null"),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    xdripjs = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "null")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devicestatus", x => x.id);
                    table.ForeignKey(
                        name: "FK_devicestatus_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_devicestatus_device",
                table: "devicestatus",
                column: "device");

            migrationBuilder.CreateIndex(
                name: "ix_devicestatus_device_timestamp",
                table: "devicestatus",
                columns: new[] { "device", "mills" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_devicestatus_sys_created_at",
                table: "devicestatus",
                column: "sys_created_at");

            migrationBuilder.CreateIndex(
                name: "IX_devicestatus_tenant_id",
                table: "devicestatus",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_devicestatus_timestamp",
                table: "devicestatus",
                column: "mills",
                descending: new bool[0]);
        }
    }
}
