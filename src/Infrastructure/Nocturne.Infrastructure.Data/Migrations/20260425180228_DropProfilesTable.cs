using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropProfilesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "profiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    additional_properties = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    created_at_pg = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    default_profile = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "Default"),
                    entered_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    loop_settings_json = table.Column<string>(type: "jsonb", nullable: true),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    original_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    start_date = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    store_json = table.Column<string>(type: "text", nullable: false, defaultValue: "{}"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    units = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "mg/dl"),
                    updated_at_pg = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profiles", x => x.id);
                    table.ForeignKey(
                        name: "FK_profiles_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_profiles_default_profile",
                table: "profiles",
                column: "default_profile");

            migrationBuilder.CreateIndex(
                name: "ix_profiles_mills",
                table: "profiles",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_profiles_sys_created_at",
                table: "profiles",
                column: "created_at_pg");

            migrationBuilder.CreateIndex(
                name: "IX_profiles_tenant_id",
                table: "profiles",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_profiles_units",
                table: "profiles",
                column: "units");
        }
    }
}
