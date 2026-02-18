using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHeartRatesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "sys_updated_at",
                table: "step_counts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.CreateTable(
                name: "heart_rates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    bpm = table.Column<int>(type: "integer", nullable: false),
                    accuracy = table.Column<int>(type: "integer", nullable: false),
                    device = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    entered_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_heart_rates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_heart_rates_mills",
                table: "heart_rates",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_heart_rates_sys_created_at",
                table: "heart_rates",
                column: "sys_created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "heart_rates");

            migrationBuilder.AlterColumn<DateTime>(
                name: "sys_updated_at",
                table: "step_counts",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "CURRENT_TIMESTAMP");
        }
    }
}
