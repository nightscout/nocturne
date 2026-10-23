using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Indexes the temp basals whose origin is still to be corroborated, by tenant and start: the
    /// pages TempBasalOriginCorroborator walks. Every existing row qualifies until its first run, and
    /// a corroborated row leaves the index, so a run with nothing new reads only its pending rows.
    /// </summary>
    public partial class AddTempBasalOriginPendingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_temp_basals_origin_pending",
                "ON temp_basals (tenant_id, start_timestamp, id) WHERE deleted_at IS NULL AND (origin_evidence IS NULL OR origin_evidence = 'Uploader')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_temp_basals_origin_pending");
        }
    }
}
