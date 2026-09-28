using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscrepancyAnalysisTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discrepancy_analyses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    analysis_timestamp = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    request_method = table.Column<string>(
                        type: "character varying(10)",
                        maxLength: 10,
                        nullable: false
                    ),
                    request_path = table.Column<string>(
                        type: "character varying(2048)",
                        maxLength: 2048,
                        nullable: false
                    ),
                    overall_match = table.Column<int>(type: "integer", nullable: false),
                    status_code_match = table.Column<bool>(type: "boolean", nullable: false),
                    body_match = table.Column<bool>(type: "boolean", nullable: false),
                    nightscout_status_code = table.Column<int>(type: "integer", nullable: true),
                    nocturne_status_code = table.Column<int>(type: "integer", nullable: true),
                    nightscout_response_time_ms = table.Column<long>(
                        type: "bigint",
                        nullable: true
                    ),
                    nocturne_response_time_ms = table.Column<long>(type: "bigint", nullable: true),
                    total_processing_time_ms = table.Column<long>(type: "bigint", nullable: false),
                    summary = table.Column<string>(
                        type: "character varying(1000)",
                        maxLength: 1000,
                        nullable: false
                    ),
                    selected_response_target = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    selection_reason = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    critical_discrepancy_count = table.Column<int>(
                        type: "integer",
                        nullable: false
                    ),
                    major_discrepancy_count = table.Column<int>(type: "integer", nullable: false),
                    minor_discrepancy_count = table.Column<int>(type: "integer", nullable: false),
                    nightscout_missing = table.Column<bool>(type: "boolean", nullable: false),
                    nocturne_missing = table.Column<bool>(type: "boolean", nullable: false),
                    error_message = table.Column<string>(
                        type: "character varying(2000)",
                        maxLength: 2000,
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discrepancy_analyses", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "discrepancy_details",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    analysis_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discrepancy_type = table.Column<int>(type: "integer", nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    field = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: false
                    ),
                    nightscout_value = table.Column<string>(
                        type: "character varying(2000)",
                        maxLength: 2000,
                        nullable: false
                    ),
                    nocturne_value = table.Column<string>(
                        type: "character varying(2000)",
                        maxLength: 2000,
                        nullable: false
                    ),
                    description = table.Column<string>(
                        type: "character varying(1000)",
                        maxLength: 1000,
                        nullable: false
                    ),
                    recorded_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discrepancy_details", x => x.id);
                    table.ForeignKey(
                        name: "FK_discrepancy_details_discrepancy_analyses_analysis_id",
                        column: x => x.analysis_id,
                        principalTable: "discrepancy_analyses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_discrepancy_analyses_correlation_id",
                table: "discrepancy_analyses",
                column: "correlation_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_discrepancy_analyses_overall_match",
                table: "discrepancy_analyses",
                column: "overall_match"
            );

            migrationBuilder.CreateIndex(
                name: "ix_discrepancy_analyses_path_timestamp",
                table: "discrepancy_analyses",
                columns: new[] { "request_path", "analysis_timestamp" },
                descending: new[] { false, true }
            );

            migrationBuilder.CreateIndex(
                name: "ix_discrepancy_analyses_request_path",
                table: "discrepancy_analyses",
                column: "request_path"
            );

            migrationBuilder.CreateIndex(
                name: "ix_discrepancy_analyses_timestamp",
                table: "discrepancy_analyses",
                column: "analysis_timestamp",
                descending: new bool[0]
            );

            migrationBuilder.CreateIndex(
                name: "ix_discrepancy_details_analysis_id",
                table: "discrepancy_details",
                column: "analysis_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_discrepancy_details_severity",
                table: "discrepancy_details",
                column: "severity"
            );

            migrationBuilder.CreateIndex(
                name: "ix_discrepancy_details_type",
                table: "discrepancy_details",
                column: "discrepancy_type"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "discrepancy_details");

            migrationBuilder.DropTable(name: "discrepancy_analyses");
        }
    }
}
