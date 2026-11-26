using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    sender_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    sender_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    withdrawn_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_notifications", x => x.id);
                    table.ForeignKey(
                        name: "FK_external_notifications_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_external_notifications_sender_tenant_created",
                table: "external_notifications",
                columns: new[] { "sender_kind", "sender_id", "tenant_id", "sys_created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_external_notifications_tenant_id",
                table: "external_notifications",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_external_notifications_sender_tenant_subject_key",
                table: "external_notifications",
                columns: new[] { "sender_kind", "sender_id", "tenant_id", "subject_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_notifications");
        }
    }
}
