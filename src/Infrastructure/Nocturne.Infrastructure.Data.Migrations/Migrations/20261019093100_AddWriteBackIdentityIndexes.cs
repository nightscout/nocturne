using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// A partial expression index per table for each id form its records go upstream under, so the
    /// Nightscout connector's pull resolves one without scanning the tenant's rows: the 24-hex prefix
    /// of a uuid-shaped legacy id (<c>UuidLegacyIdPrefix</c>) on every legacy-keyed table, and
    /// <c>legacy_id_wire_hash</c> (<see cref="AddWriteBackIdentityTracking"/>) of any other
    /// non-ObjectId legacy id (<c>HashedLegacyId</c>) on the treatment tables.
    /// <para>
    /// Built through <see cref="ConcurrentIndexBuilder"/>, whose remarks cover why
    /// <c>CONCURRENTLY</c> and how a retry clears what an interrupted build left; this migration holds
    /// nothing else, so a restart after an interruption reruns only idempotent steps. Non-unique, so
    /// no existing row can fail a build and no loser cleanup is needed. The expressions and
    /// predicates are frozen here and must stay identical to <c>UuidLegacyIdPrefix</c> and
    /// <c>HashedLegacyId</c>, whose queries the planner matches to them.
    /// </para>
    /// </summary>
    public partial class AddWriteBackIdentityIndexes : Migration
    {
        private static readonly string[] EntryAndStatusTables =
        [
            "sensor_glucose",
            "meter_glucose",
            "calibrations",
            "aps_snapshots",
            "pump_snapshots",
            "uploader_snapshots",
        ];

        private static readonly string[] TreatmentTables =
        [
            "boluses",
            "carb_intakes",
            "bg_checks",
            "notes",
            "device_events",
            "bolus_calculations",
            "temp_basals",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in EntryAndStatusTables.Concat(TreatmentTables))
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_uuid_legacy_id_prefix",
                    $"ON {table} (tenant_id, left(regexp_replace(lower(legacy_id), '[^0-9a-f]', '', 'g'), 24)) "
                    + "WHERE legacy_id ~* '^[{(]?[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}[})]?$'");
            }

            foreach (var table in TreatmentTables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_hashed_legacy_id",
                    $"ON {table} (tenant_id, public.legacy_id_wire_hash(legacy_id)) "
                    + "WHERE legacy_id !~ '^[0-9a-f]{24}$'");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TreatmentTables)
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_hashed_legacy_id");

            foreach (var table in EntryAndStatusTables.Concat(TreatmentTables))
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_uuid_legacy_id_prefix");
        }
    }
}
