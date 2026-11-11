using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds <c>sensor_glucose.is_secondary_stream</c> and makes every tenant's
    /// <c>glucose-stream-recompute</c> row due now, so the job sets the flag from the stream rule.
    /// </summary>
    /// <remarks>
    /// The rule lives in one place (<c>CanonicalGlucoseStream</c>), so the backfill is the job and not
    /// SQL. Until a tenant's first run its readings all read as canonical. Both steps are re-runnable
    /// after a boot killed between them: the column is added only where it is missing, and the ledger
    /// row is upserted.
    /// </remarks>
    public partial class AddSecondaryStreamFlagToSensorGlucose : Migration
    {
        internal const string JobName = "glucose-stream-recompute";

        private const string RequestSql = $"""
            INSERT INTO job_ledger (id, job, tenant_id, next_due_at, attempts)
            SELECT {AddJobLedger.UuidV7}, '{JobName}', id, now(), 0 FROM tenants
            ON CONFLICT (job, tenant_id) DO UPDATE SET next_due_at = now();
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE sensor_glucose ADD COLUMN IF NOT EXISTS is_secondary_stream boolean NOT NULL DEFAULT false;");
            migrationBuilder.Sql(RequestSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"DELETE FROM job_ledger WHERE job = '{JobName}';");
            migrationBuilder.DropColumn(
                name: "is_secondary_stream",
                table: "sensor_glucose");
        }
    }
}
