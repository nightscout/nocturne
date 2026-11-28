using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Deletes the <c>job_ledger</c> rows of the <c>alert-rule-scope-class-backfill</c> job, which a job
    /// of another name replaced, as <c>IScheduledJob.Name</c> requires.
    /// </summary>
    public partial class RetireScopeClassBackfillJobV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM job_ledger WHERE job = 'alert-rule-scope-class-backfill';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
