using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillAuthAuditActorSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AddAuthAuditActorAndTenant added actor_subject_id nullable and backfilled nothing, so
            // "everything this subject did" — the indexed predicate the column exists for — misses
            // every row written before it. Every one of those rows was written by a code path that
            // had no separate actor, which is exactly the case the writer now records by mirroring
            // subject_id, so mirroring it here reconstructs the same value.
            // auth_audit_log is not ITenantScoped: it carries no RLS policy and no tenant GUC is
            // needed, so the UPDATE runs once over the whole table rather than per tenant.
            // subject_id already has an FK to subjects, so every value copied satisfies
            // actor_subject_id's FK.
            migrationBuilder.Sql("""
                UPDATE auth_audit_log
                SET actor_subject_id = subject_id
                WHERE actor_subject_id IS NULL AND subject_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversed: a mirrored actor_subject_id is indistinguishable from one the writer set
            // organically, so clearing them would destroy attribution on rows written after this ran.
        }
    }
}
