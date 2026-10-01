using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInAppNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "in_app_notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    urgency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    subtitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    source_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    actions_json = table.Column<string>(type: "jsonb", nullable: true),
                    resolution_conditions_json = table.Column<string>(type: "jsonb", nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    archived_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    archive_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_in_app_notifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_in_app_notifications_created_at",
                table: "in_app_notifications",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_in_app_notifications_is_archived",
                table: "in_app_notifications",
                column: "is_archived");

            migrationBuilder.CreateIndex(
                name: "ix_in_app_notifications_source_id",
                table: "in_app_notifications",
                column: "source_id",
                filter: "source_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_in_app_notifications_type",
                table: "in_app_notifications",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "ix_in_app_notifications_user_archived",
                table: "in_app_notifications",
                columns: new[] { "user_id", "is_archived" });

            migrationBuilder.CreateIndex(
                name: "ix_in_app_notifications_user_id",
                table: "in_app_notifications",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_in_app_notifications_user_type_archived",
                table: "in_app_notifications",
                columns: new[] { "user_id", "type", "is_archived" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "in_app_notifications");
        }
    }
}
