using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// A partial expression index on each treatment table over <c>legacy_id_wire_hash</c>
    /// (<see cref="AddWriteBackIdentityTracking"/>) of a non-ObjectId legacy id, so the Nightscout
    /// connector's pull resolves the hash a treatment was written back under without hashing every
    /// row of the tenant (<c>HashedLegacyId</c>).
    /// <para>
    /// Built through <see cref="ConcurrentIndexBuilder"/>, whose remarks cover why
    /// <c>CONCURRENTLY</c> and how a retry clears what an interrupted build left; this migration holds
    /// nothing else, so a restart after an interruption reruns only idempotent steps. Non-unique, so
    /// no existing row can fail a build and no loser cleanup is needed. The expression and predicate
    /// are frozen here and must stay identical to <c>HashedLegacyId</c>, whose queries the planner
    /// matches to them.
    /// </para>
    /// </summary>
    public partial class AddWriteBackIdentityIndexes : Migration
    {
        private static readonly string[] TreatmentTables =
        [
            "boluses",
            "carb_intakes",
            "bg_checks",
            "notes",
            "device_events",
            "bolus_calculations",
            "temp_basals",
            "profile_activations",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TreatmentTables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_hashed_legacy_id",
                    $"ON {table} (tenant_id, public.legacy_id_wire_hash(legacy_id)) "
                    + "WHERE NOT (length(legacy_id) = 24 AND legacy_id ~ '^[0-9a-f]+$')");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TreatmentTables)
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_hashed_legacy_id");
        }
    }
}
