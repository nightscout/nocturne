using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropActivitiesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activities");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "activities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    additional_properties = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    date_string = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    duration = table.Column<double>(type: "double precision", nullable: true),
                    entered_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    intensity = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    original_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<long>(type: "bigint", nullable: true),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    utc_offset = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activities", x => x.id);
                    table.ForeignKey(
                        name: "FK_activities_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_activities_mills",
                table: "activities",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_activities_sys_created_at",
                table: "activities",
                column: "sys_created_at");

            migrationBuilder.CreateIndex(
                name: "IX_activities_tenant_id",
                table: "activities",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_activities_type",
                table: "activities",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "ix_activities_type_timestamp",
                table: "activities",
                columns: new[] { "type", "mills" },
                descending: new[] { false, true });
        }
    }
}
