using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Drops the single-column <c>target_subject_id</c> index on <c>auth_audit_log</c>, the leading column of
    /// <c>ix_auth_audit_log_target_subject_created</c>, through <see cref="ConcurrentIndexBuilder"/>.
    /// </summary>
    public partial class DropAuthAuditTargetSubjectIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_auth_audit_log_target_subject_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_auth_audit_log_target_subject_id",
                "ON auth_audit_log (target_subject_id)");
        }
    }
}
