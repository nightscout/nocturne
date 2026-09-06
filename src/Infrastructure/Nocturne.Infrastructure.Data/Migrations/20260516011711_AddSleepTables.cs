using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSleepTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sleep_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    end_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    timezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detection_method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    is_main_sleep = table.Column<bool>(type: "boolean", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    total_sleep_ms = table.Column<long>(type: "bigint", nullable: false),
                    total_awake_ms = table.Column<long>(type: "bigint", nullable: true),
                    deep_sleep_ms = table.Column<long>(type: "bigint", nullable: true),
                    light_sleep_ms = table.Column<long>(type: "bigint", nullable: true),
                    rem_sleep_ms = table.Column<long>(type: "bigint", nullable: true),
                    sleep_latency_ms = table.Column<long>(type: "bigint", nullable: true),
                    efficiency = table.Column<float>(type: "real", nullable: true),
                    restless_periods = table.Column<int>(type: "integer", nullable: true),
                    sleep_score = table.Column<short>(type: "smallint", nullable: true),
                    avg_heart_rate = table.Column<float>(type: "real", nullable: true),
                    min_heart_rate = table.Column<float>(type: "real", nullable: true),
                    avg_hrv = table.Column<float>(type: "real", nullable: true),
                    avg_breath_rate = table.Column<float>(type: "real", nullable: true),
                    avg_spo2 = table.Column<float>(type: "real", nullable: true),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_device = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_app = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    original_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sleep_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_sleep_sessions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sleep_biometric_samples",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sleep_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    heart_rate = table.Column<float>(type: "real", nullable: true),
                    hrv = table.Column<float>(type: "real", nullable: true),
                    spo2 = table.Column<float>(type: "real", nullable: true),
                    respiration_rate = table.Column<float>(type: "real", nullable: true),
                    movement = table.Column<float>(type: "real", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sleep_biometric_samples", x => x.id);
                    table.ForeignKey(
                        name: "FK_sleep_biometric_samples_sleep_sessions_sleep_session_id",
                        column: x => x.sleep_session_id,
                        principalTable: "sleep_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sleep_biometric_samples_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sleep_stages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sleep_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    end_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    stage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sleep_stages", x => x.id);
                    table.ForeignKey(
                        name: "FK_sleep_stages_sleep_sessions_sleep_session_id",
                        column: x => x.sleep_session_id,
                        principalTable: "sleep_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sleep_stages_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sleep_biometric_samples_sleep_session_id",
                table: "sleep_biometric_samples",
                column: "sleep_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_sleep_biometric_samples_tenant_id",
                table: "sleep_biometric_samples",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sleep_sessions_tenant_start_time",
                table: "sleep_sessions",
                columns: new[] { "tenant_id", "start_time" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_sleep_sessions_tenant_source_original",
                table: "sleep_sessions",
                columns: new[] { "tenant_id", "source", "original_id" },
                unique: true,
                filter: "original_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sleep_stages_sleep_session_id",
                table: "sleep_stages",
                column: "sleep_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_sleep_stages_tenant_id",
                table: "sleep_stages",
                column: "tenant_id");

            // Enable RLS on sleep tables
            foreach (var table in new[] { "sleep_sessions", "sleep_stages", "sleep_biometric_samples" })
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"""
                    CREATE POLICY tenant_isolation ON {table}
                        USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "sleep_biometric_samples", "sleep_stages", "sleep_sessions" })
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
                migrationBuilder.Sql($"ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
            }

            migrationBuilder.DropTable(
                name: "sleep_biometric_samples");

            migrationBuilder.DropTable(
                name: "sleep_stages");

            migrationBuilder.DropTable(
                name: "sleep_sessions");
        }
    }
}
