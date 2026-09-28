using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "treatments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_treatments_deleted_at",
                table: "treatments",
                column: "deleted_at",
                filter: "deleted_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_entries_deleted_at",
                table: "entries",
                column: "deleted_at",
                filter: "deleted_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_treatments_deleted_at",
                table: "treatments");

            migrationBuilder.DropIndex(
                name: "ix_entries_deleted_at",
                table: "entries");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "entries");
        }
    }
}
