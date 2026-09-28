using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropTreatmentsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_connector_food_entries_treatments_matched_treatment_id",
                table: "connector_food_entries");

            migrationBuilder.DropForeignKey(
                name: "FK_decomposition_batches_treatments_source_treatment_id",
                table: "decomposition_batches");

            migrationBuilder.DropTable(
                name: "treatments");

            migrationBuilder.DropIndex(
                name: "IX_decomposition_batches_source_treatment_id",
                table: "decomposition_batches");

            migrationBuilder.DropIndex(
                name: "IX_connector_food_entries_matched_treatment_id",
                table: "connector_food_entries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "treatments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    additional_properties = table.Column<string>(type: "jsonb", nullable: true),
                    carbs = table.Column<double>(type: "double precision", nullable: true),
                    created_at = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    cuttedby = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    cutting = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    data_source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    date = table.Column<long>(type: "bigint", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    duration = table.Column<double>(type: "double precision", nullable: true),
                    end = table.Column<bool>(type: "boolean", nullable: true),
                    entered_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    event_time = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    event_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    first = table.Column<bool>(type: "boolean", nullable: true),
                    insulin = table.Column<double>(type: "double precision", nullable: true),
                    insulin_context = table.Column<string>(type: "jsonb", nullable: true),
                    is_announcement = table.Column<bool>(type: "boolean", nullable: true),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    nsclient_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    original_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    split = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    status = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    target_bottom = table.Column<double>(type: "double precision", nullable: true),
                    target_top = table.Column<double>(type: "double precision", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<long>(type: "bigint", nullable: true),
                    transmitter_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    end_id = table.Column<long>(type: "bigint", nullable: true),
                    is_basal_insulin = table.Column<bool>(type: "boolean", nullable: true),
                    is_read_only = table.Column<bool>(type: "boolean", nullable: true),
                    is_valid = table.Column<bool>(type: "boolean", nullable: true),
                    original_customized_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    original_duration = table.Column<int>(type: "integer", nullable: true),
                    original_end = table.Column<long>(type: "bigint", nullable: true),
                    original_percentage = table.Column<int>(type: "integer", nullable: true),
                    original_profile_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    original_timeshift = table.Column<int>(type: "integer", nullable: true),
                    pump_id = table.Column<long>(type: "bigint", nullable: true),
                    pump_serial = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    pump_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    absolute = table.Column<double>(type: "double precision", nullable: true),
                    duration_in_milliseconds = table.Column<long>(type: "bigint", nullable: true),
                    durationType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    endmills = table.Column<long>(type: "bigint", nullable: true),
                    percent = table.Column<double>(type: "double precision", nullable: true),
                    rate = table.Column<double>(type: "double precision", nullable: true),
                    relative = table.Column<double>(type: "double precision", nullable: true),
                    blood_glucose_input = table.Column<double>(type: "double precision", nullable: true),
                    blood_glucose_input_source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    boluscalc = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "{}"),
                    bolus_calculator_result = table.Column<string>(type: "text", nullable: true),
                    CR = table.Column<double>(type: "double precision", nullable: true),
                    calculation_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    enteredinsulin = table.Column<double>(type: "double precision", nullable: true),
                    insulin_delivered = table.Column<double>(type: "double precision", nullable: true),
                    insulin_on_board = table.Column<double>(type: "double precision", nullable: true),
                    insulin_programmed = table.Column<double>(type: "double precision", nullable: true),
                    insulin_recommendation_for_carbs = table.Column<double>(type: "double precision", nullable: true),
                    insulin_recommendation_for_correction = table.Column<double>(type: "double precision", nullable: true),
                    preBolus = table.Column<double>(type: "double precision", nullable: true),
                    splitExt = table.Column<double>(type: "double precision", nullable: true),
                    splitNow = table.Column<double>(type: "double precision", nullable: true),
                    glucose = table.Column<double>(type: "double precision", nullable: true),
                    glucoseType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    mgdl = table.Column<double>(type: "double precision", nullable: true),
                    mmol = table.Column<double>(type: "double precision", nullable: true),
                    units = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    otp = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    reasonDisplay = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    remoteAbsorption = table.Column<double>(type: "double precision", nullable: true),
                    remoteBolus = table.Column<double>(type: "double precision", nullable: true),
                    remoteCarbs = table.Column<double>(type: "double precision", nullable: true),
                    absorptionTime = table.Column<int>(type: "integer", nullable: true),
                    carbTime = table.Column<int>(type: "integer", nullable: true),
                    fat = table.Column<double>(type: "double precision", nullable: true),
                    foodType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    protein = table.Column<double>(type: "double precision", nullable: true),
                    CircadianPercentageProfile = table.Column<bool>(type: "boolean", nullable: true),
                    endprofile = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    insulinNeedsScaleFactor = table.Column<double>(type: "double precision", nullable: true),
                    percentage = table.Column<double>(type: "double precision", nullable: true),
                    profile = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    profileJson = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "null"),
                    timeshift = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_treatments", x => x.id);
                    table.ForeignKey(
                        name: "FK_treatments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_decomposition_batches_source_treatment_id",
                table: "decomposition_batches",
                column: "source_treatment_id");

            migrationBuilder.CreateIndex(
                name: "IX_connector_food_entries_matched_treatment_id",
                table: "connector_food_entries",
                column: "matched_treatment_id");

            migrationBuilder.CreateIndex(
                name: "ix_treatments_deleted_at",
                table: "treatments",
                column: "deleted_at",
                filter: "deleted_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_treatments_event_type",
                table: "treatments",
                column: "event_type");

            migrationBuilder.CreateIndex(
                name: "ix_treatments_event_type_timestamp",
                table: "treatments",
                columns: new[] { "event_type", "mills" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_treatments_mills",
                table: "treatments",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_treatments_sys_created_at",
                table: "treatments",
                column: "sys_created_at");

            migrationBuilder.CreateIndex(
                name: "IX_treatments_tenant_id",
                table: "treatments",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "FK_connector_food_entries_treatments_matched_treatment_id",
                table: "connector_food_entries",
                column: "matched_treatment_id",
                principalTable: "treatments",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_decomposition_batches_treatments_source_treatment_id",
                table: "decomposition_batches",
                column: "source_treatment_id",
                principalTable: "treatments",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
