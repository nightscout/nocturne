using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Creates <c>job_ledger</c>, which the job scheduler runs every scheduled job from, and moves the
    /// two tables it replaces into it: a <c>backfill_completions</c> row becomes a finished row (never
    /// due again), a <c>pending_tenant_jobs</c> row one due from when it was requested.
    /// </summary>
    /// <remarks>Both old tables are platform tables with no tenant policy, so no tenant is pinned.</remarks>
    public partial class AddJobLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "job_ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_due_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    lease_holder = table.Column<string>(type: "text", nullable: true),
                    lease_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    cursor = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_ledger", x => x.id);
                    table.ForeignKey(
                        name: "FK_job_ledger_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_job_ledger_due",
                table: "job_ledger",
                column: "next_due_at",
                filter: "next_due_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_job_ledger_tenant_id",
                table: "job_ledger",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_job_ledger_job_tenant",
                table: "job_ledger",
                columns: new[] { "job", "tenant_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.Sql(CopyIntoLedgerSql);

            migrationBuilder.DropTable(
                name: "backfill_completions");

            migrationBuilder.DropTable(
                name: "pending_tenant_jobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backfill_completions",
                columns: table => new
                {
                    backfill = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backfill_completions", x => new { x.backfill, x.tenant_id });
                    table.ForeignKey(
                        name: "FK_backfill_completions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pending_tenant_jobs",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_tenant_jobs", x => new { x.tenant_id, x.job });
                    table.ForeignKey(
                        name: "FK_pending_tenant_jobs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_backfill_completions_tenant_id",
                table: "backfill_completions",
                column: "tenant_id");

            migrationBuilder.Sql(CopyOutOfLedgerSql);

            migrationBuilder.DropTable(
                name: "job_ledger");
        }

        /// <summary>A UUIDv7 built in SQL: PostgreSQL 17 has no <c>uuidv7()</c>.</summary>
        private const string UuidV7 = """
            encode(set_bit(set_bit(
                overlay(uuid_send(gen_random_uuid())
                        placing substring(int8send((extract(epoch from clock_timestamp()) * 1000)::bigint) from 3)
                        from 1 for 6),
                52, 1), 53, 1), 'hex')::uuid
            """;

        private const string CopyIntoLedgerSql = $"""
            INSERT INTO job_ledger (id, job, tenant_id, last_completed_at, next_due_at, attempts)
            SELECT {UuidV7}, backfill, tenant_id, completed_at, NULL, 0 FROM backfill_completions;

            INSERT INTO job_ledger (id, job, tenant_id, next_due_at, attempts)
            SELECT {UuidV7}, job, tenant_id, requested_at, 0 FROM pending_tenant_jobs;
            """;

        /// <remarks>
        /// A backfill part-way through a tenant has no completion to go back to, and the job still due
        /// for a tenant is the only pending one the old tables could hold.
        /// </remarks>
        private const string CopyOutOfLedgerSql = """
            INSERT INTO backfill_completions (backfill, tenant_id, completed_at)
            SELECT job, tenant_id, last_completed_at FROM job_ledger
            WHERE job = 'aps-snapshot-curves' AND tenant_id IS NOT NULL
              AND next_due_at IS NULL AND last_completed_at IS NOT NULL;

            INSERT INTO pending_tenant_jobs (tenant_id, job, requested_at)
            SELECT tenant_id, job, next_due_at FROM job_ledger
            WHERE job = 'profile-versions-moved' AND tenant_id IS NOT NULL AND next_due_at IS NOT NULL;
            """;
    }
}
