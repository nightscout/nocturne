using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectorHealthTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_healthy",
                table: "connector_configurations",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_error_at",
                table: "connector_configurations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_error_message",
                table: "connector_configurations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_successful_sync",
                table: "connector_configurations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_sync_attempt",
                table: "connector_configurations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_healthy",
                table: "connector_configurations");

            migrationBuilder.DropColumn(
                name: "last_error_at",
                table: "connector_configurations");

            migrationBuilder.DropColumn(
                name: "last_error_message",
                table: "connector_configurations");

            migrationBuilder.DropColumn(
                name: "last_successful_sync",
                table: "connector_configurations");

            migrationBuilder.DropColumn(
                name: "last_sync_attempt",
                table: "connector_configurations");
        }
    }
}
