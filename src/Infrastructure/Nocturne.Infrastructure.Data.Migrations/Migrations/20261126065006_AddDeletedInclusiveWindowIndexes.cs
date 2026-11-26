using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the unfiltered tenant timestamp indexes the deleted-inclusive window reads need, and lifts
    /// the <c>deleted_at</c> filter off the profile activation one, all through
    /// <see cref="ConcurrentIndexBuilder"/>. See there for why CONCURRENTLY, and for what an
    /// interrupted build leaves behind. Why they exist is at their declaration in
    /// <c>ModelConventions.ConfigureSharedRecordIndexes</c>.
    /// </summary>
    public partial class AddDeletedInclusiveWindowIndexes : Migration
    {
        private static readonly string[] Tables = ["notes", "device_events", "bolus_calculations", "bg_checks"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_profile_activations_tenant_start_timestamp");
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_profile_activations_tenant_start_timestamp",
                "ON profile_activations (tenant_id, start_timestamp DESC)");

            foreach (var table in Tables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder, $"ix_{table}_tenant_timestamp", $"ON {table} (tenant_id, timestamp DESC)");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_timestamp");
            }

            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_profile_activations_tenant_start_timestamp");
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_profile_activations_tenant_start_timestamp",
                "ON profile_activations (tenant_id, start_timestamp DESC) WHERE deleted_at IS NULL");
        }
    }
}
