using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the member-change indexes on <c>auth_audit_log</c> through <see cref="ConcurrentIndexBuilder"/>.
    /// See there for why CONCURRENTLY, and for what an interrupted build leaves behind. Why they exist is at
    /// their declaration in <c>AuthAuditLogEntityConfiguration</c>.
    /// </summary>
    public partial class AddMemberHistoryIndex : Migration
    {
        private const string MemberChanges = "event_type IN ('role_assigned', 'role_removed', 'history_limit_changed', 'access_end_changed', 'role_permissions_changed', 'member_joined', 'member_removed', 'team_access_given', 'team_access_stopped', 'team_access_changed', 'team_staff_approved', 'team_staff_declined', 'team_staff_rule_changed', 'team_staff_blocked', 'team_staff_unblocked', 'team_group_person_added', 'team_group_person_removed', 'team_group_staff_added', 'team_group_staff_removed', 'team_group_deleted', 'team_staff_reach_changed')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_auth_audit_log_tenant_member_changes",
                $"ON auth_audit_log (tenant_id, created_at DESC, id DESC) WHERE {MemberChanges}");
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_auth_audit_log_tenant_target_member_changes",
                $"ON auth_audit_log (tenant_id, target_subject_id, created_at DESC, id DESC) WHERE {MemberChanges}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_auth_audit_log_tenant_target_member_changes");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_auth_audit_log_tenant_member_changes");
        }
    }
}
