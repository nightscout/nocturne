using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddV4DeviceEventsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_events",
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
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    notes = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_events_correlation_id",
                table: "device_events",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_events_legacy_id",
                table: "device_events",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_events_mills",
                table: "device_events",
                column: "mills",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_events");
        }
    }
}
