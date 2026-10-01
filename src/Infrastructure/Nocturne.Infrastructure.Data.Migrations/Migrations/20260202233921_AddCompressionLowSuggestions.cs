using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompressionLowSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "compression_low_suggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_mills = table.Column<long>(type: "bigint", nullable: false),
                    end_mills = table.Column<long>(type: "bigint", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    night_of = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false),
                    reviewed_at = table.Column<long>(type: "bigint", nullable: true),
                    state_span_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lowest_glucose = table.Column<double>(type: "double precision", nullable: true),
                    drop_rate = table.Column<double>(type: "double precision", nullable: true),
                    recovery_minutes = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compression_low_suggestions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_compression_low_suggestions_night_of",
                table: "compression_low_suggestions",
                column: "night_of");

            migrationBuilder.CreateIndex(
                name: "ix_compression_low_suggestions_status",
                table: "compression_low_suggestions",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "compression_low_suggestions");
        }
    }
}
