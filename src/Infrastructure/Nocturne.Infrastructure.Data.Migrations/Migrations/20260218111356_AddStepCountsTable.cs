using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStepCountsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "step_counts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    metric = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    device = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    entered_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_step_counts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_step_counts_mills",
                table: "step_counts",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_step_counts_sys_created_at",
                table: "step_counts",
                column: "sys_created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "step_counts");
        }
    }
}
