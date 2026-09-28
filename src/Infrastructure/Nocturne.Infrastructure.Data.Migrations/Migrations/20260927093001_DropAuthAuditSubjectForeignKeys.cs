using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropAuthAuditSubjectForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_auth_audit_log_subjects_actor_subject_id",
                table: "auth_audit_log");

            migrationBuilder.DropForeignKey(
                name: "FK_auth_audit_log_subjects_subject_id",
                table: "auth_audit_log");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_auth_audit_log_subjects_actor_subject_id",
                table: "auth_audit_log",
                column: "actor_subject_id",
                principalTable: "subjects",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_auth_audit_log_subjects_subject_id",
                table: "auth_audit_log",
                column: "subject_id",
                principalTable: "subjects",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
