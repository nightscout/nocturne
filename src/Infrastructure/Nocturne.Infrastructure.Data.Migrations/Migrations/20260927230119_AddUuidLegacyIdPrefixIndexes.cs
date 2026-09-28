using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Indexes each uuid-shaped legacy id by the 24-hex prefix Nightscout write-back sends it upstream
    /// under, on the tables the Nightscout connector pulls written-back entries and device statuses
    /// back into, so the pull resolves that prefix to the stored record
    /// (<c>V4RepositoryBase.ResolveUuidLegacyIdsAsync</c>) without scanning the tenant's rows.
    /// Partial and non-unique, so no existing row can fail the build; built through
    /// <see cref="ConcurrentIndexBuilder"/>. The expression and predicate are written out here, frozen,
    /// and must stay identical to <c>UuidLegacyIdPrefix</c>, whose query the planner matches to them.
    /// </summary>
    public partial class AddUuidLegacyIdPrefixIndexes : Migration
    {
        private static readonly string[] Tables =
        [
            "sensor_glucose",
            "meter_glucose",
            "calibrations",
            "aps_snapshots",
            "pump_snapshots",
            "uploader_snapshots",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_uuid_legacy_id_prefix",
                    $"ON {table} (tenant_id, left(regexp_replace(lower(legacy_id), '[^0-9a-f]', '', 'g'), 24)) "
                    + "WHERE legacy_id ~* '^[{(]?[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}[})]?$'");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_uuid_legacy_id_prefix");
        }
    }
}
