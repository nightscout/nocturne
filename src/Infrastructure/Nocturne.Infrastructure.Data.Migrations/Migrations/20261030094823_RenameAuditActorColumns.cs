using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameAuditActorColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "subject_name",
                table: "read_access_log",
                newName: "actor_name");

            migrationBuilder.RenameColumn(
                name: "subject_id",
                table: "read_access_log",
                newName: "actor_subject_id");

            migrationBuilder.RenameIndex(
                name: "ix_read_access_log_subject",
                table: "read_access_log",
                newName: "ix_read_access_log_actor_subject");

            migrationBuilder.RenameColumn(
                name: "subject_name",
                table: "mutation_audit_log",
                newName: "actor_name");

            migrationBuilder.RenameColumn(
                name: "subject_id",
                table: "mutation_audit_log",
                newName: "actor_subject_id");

            migrationBuilder.RenameIndex(
                name: "ix_mutation_audit_log_subject",
                table: "mutation_audit_log",
                newName: "ix_mutation_audit_log_actor_subject");

            migrationBuilder.RenameColumn(
                name: "subject_id",
                table: "auth_audit_log",
                newName: "target_subject_id");

            migrationBuilder.RenameIndex(
                name: "ix_auth_audit_log_subject_id",
                table: "auth_audit_log",
                newName: "ix_auth_audit_log_target_subject_id");

            migrationBuilder.RenameIndex(
                name: "ix_auth_audit_log_subject_created",
                table: "auth_audit_log",
                newName: "ix_auth_audit_log_target_subject_created");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "actor_subject_id",
                table: "read_access_log",
                newName: "subject_id");

            migrationBuilder.RenameColumn(
                name: "actor_name",
                table: "read_access_log",
                newName: "subject_name");

            migrationBuilder.RenameIndex(
                name: "ix_read_access_log_actor_subject",
                table: "read_access_log",
                newName: "ix_read_access_log_subject");

            migrationBuilder.RenameColumn(
                name: "actor_subject_id",
                table: "mutation_audit_log",
                newName: "subject_id");

            migrationBuilder.RenameColumn(
                name: "actor_name",
                table: "mutation_audit_log",
                newName: "subject_name");

            migrationBuilder.RenameIndex(
                name: "ix_mutation_audit_log_actor_subject",
                table: "mutation_audit_log",
                newName: "ix_mutation_audit_log_subject");

            migrationBuilder.RenameColumn(
                name: "target_subject_id",
                table: "auth_audit_log",
                newName: "subject_id");

            migrationBuilder.RenameIndex(
                name: "ix_auth_audit_log_target_subject_id",
                table: "auth_audit_log",
                newName: "ix_auth_audit_log_subject_id");

            migrationBuilder.RenameIndex(
                name: "ix_auth_audit_log_target_subject_created",
                table: "auth_audit_log",
                newName: "ix_auth_audit_log_subject_created");
        }
    }
}
