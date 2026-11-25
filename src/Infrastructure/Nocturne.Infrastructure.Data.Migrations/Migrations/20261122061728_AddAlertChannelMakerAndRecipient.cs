using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertChannelMakerAndRecipient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "added_by_subject_id",
                table: "alert_rule_channels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "recipient_subject_id",
                table: "alert_rule_channels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "stops_at",
                table: "alert_rule_channels",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "added_by_subject_id",
                table: "alert_rule_channels");

            migrationBuilder.DropColumn(
                name: "recipient_subject_id",
                table: "alert_rule_channels");

            migrationBuilder.DropColumn(
                name: "stops_at",
                table: "alert_rule_channels");
        }
    }
}
