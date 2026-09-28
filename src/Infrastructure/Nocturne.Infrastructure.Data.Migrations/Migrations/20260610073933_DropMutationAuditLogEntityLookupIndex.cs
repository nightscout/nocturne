using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropMutationAuditLogEntityLookupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_mutation_audit_log_entity_lookup",
                table: "mutation_audit_log");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_mutation_audit_log_entity_lookup",
                table: "mutation_audit_log",
                columns: new[] { "entity_type", "entity_id", "action", "created_at" });
        }
    }
}
