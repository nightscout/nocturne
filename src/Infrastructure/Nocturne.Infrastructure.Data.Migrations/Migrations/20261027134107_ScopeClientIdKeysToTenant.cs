using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Widens the primary key of the tables whose ids are derived from a client's record id from
    /// <c>(id)</c> to <c>(tenant_id, id)</c>, for the reason given on
    /// <c>NocturneDbContext.ScopeKeyToTenant</c>. Rows unique on <c>id</c> are unique on any key that
    /// contains it, so no row can fail the new key and there is no loser cleanup. Each key is built
    /// with <see cref="ConcurrentIndexBuilder"/> and then swapped in, so the write lock is held only
    /// for the swap.
    /// </summary>
    /// <remarks><c>Down</c> fails once two tenants hold the same id.</remarks>
    public partial class ScopeClientIdKeysToTenant : Migration
    {
        private static readonly string[] Tables =
        [
            "settings",
            "step_counts",
            "heart_rates",
            "body_weights",
            "system_events",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                ConcurrentIndexBuilder.BuildUnique(
                    migrationBuilder, $"pk_{table}_tenant_id_id", $"ON {table} (tenant_id, id)");

                migrationBuilder.Sql($"""
                    ALTER TABLE {table}
                        DROP CONSTRAINT "PK_{table}",
                        ADD CONSTRAINT "PK_{table}" PRIMARY KEY USING INDEX pk_{table}_tenant_id_id;
                    """);
            }

            ConcurrentIndexBuilder.Drop(migrationBuilder, "\"IX_system_events_tenant_id\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.DropPrimaryKey(name: $"PK_{table}", table: table);
                migrationBuilder.AddPrimaryKey(name: $"PK_{table}", table: table, column: "id");
            }

            migrationBuilder.CreateIndex(
                name: "IX_system_events_tenant_id",
                table: "system_events",
                column: "tenant_id");
        }
    }
}
