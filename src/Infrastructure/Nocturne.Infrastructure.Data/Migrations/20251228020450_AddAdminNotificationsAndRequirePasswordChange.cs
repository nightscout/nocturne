using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminNotificationsAndRequirePasswordChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "require_password_change",
                table: "local_users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "admin_notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    related_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    related_entity_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    dismissed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    dismissed_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dismissed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_admin_notifications_subjects_dismissed_by_id",
                        column: x => x.dismissed_by_id,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_notifications_active",
                table: "admin_notifications",
                column: "dismissed",
                filter: "dismissed = false");

            migrationBuilder.CreateIndex(
                name: "IX_admin_notifications_dismissed_by_id",
                table: "admin_notifications",
                column: "dismissed_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_admin_notifications_type",
                table: "admin_notifications",
                column: "type");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_notifications");

            migrationBuilder.DropColumn(
                name: "require_password_change",
                table: "local_users");
        }
    }
}
