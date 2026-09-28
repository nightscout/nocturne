using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBodyWeightsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "body_weights",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    weight_kg = table.Column<decimal>(type: "numeric", nullable: false),
                    body_fat_percent = table.Column<decimal>(type: "numeric", nullable: true),
                    lean_mass_kg = table.Column<decimal>(type: "numeric", nullable: true),
                    device = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    entered_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_body_weights", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_body_weights_mills",
                table: "body_weights",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_body_weights_sys_created_at",
                table: "body_weights",
                column: "sys_created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "body_weights");
        }
    }
}
