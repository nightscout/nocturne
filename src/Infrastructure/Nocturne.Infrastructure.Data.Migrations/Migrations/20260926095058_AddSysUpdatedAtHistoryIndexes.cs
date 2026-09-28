using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the history-page indexes, all through <see cref="ConcurrentIndexBuilder"/>. See there
    /// for why CONCURRENTLY, and for what an interrupted build leaves behind. Why they exist is at
    /// their declaration in <c>NocturneDbContext.ConfigureSharedRecordIndexes</c>.
    /// </summary>
    public partial class AddSysUpdatedAtHistoryIndexes : Migration
    {
        /// <summary>Mirrors <c>NocturneDbContext.V4HistoryPagedEntities</c>.</summary>
        private static readonly string[] HistoryPagedTables =
        [
            "aps_snapshots",
            "bg_checks",
            "bolus_calculations",
            "boluses",
            "carb_intakes",
            "device_events",
            "notes",
            "temp_basals",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in HistoryPagedTables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_sys_updated_at",
                    $"ON {table} (tenant_id, sys_updated_at, id) WHERE deleted_at IS NULL");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in HistoryPagedTables)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_sys_updated_at");
            }
        }
    }
}
