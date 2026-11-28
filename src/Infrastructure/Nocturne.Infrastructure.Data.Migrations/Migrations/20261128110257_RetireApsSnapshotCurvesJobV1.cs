using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Deletes the <c>job_ledger</c> rows of the <c>aps-snapshot-curves</c> job, which a job of another
    /// name replaced so that every tenant walks its snapshots again, as <c>IScheduledJob.Name</c> requires.
    /// </summary>
    public partial class RetireApsSnapshotCurvesJobV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM job_ledger WHERE job = 'aps-snapshot-curves';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
