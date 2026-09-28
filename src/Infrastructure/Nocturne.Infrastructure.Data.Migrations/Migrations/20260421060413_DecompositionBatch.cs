using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DecompositionBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "decomposition_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_record_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    source_treatment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decomposition_batches", x => x.id);
                    table.ForeignKey(
                        name: "FK_decomposition_batches_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_decomposition_batches_treatments_source_treatment_id",
                        column: x => x.source_treatment_id,
                        principalTable: "treatments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_decomposition_batches_source_treatment_id",
                table: "decomposition_batches",
                column: "source_treatment_id");

            migrationBuilder.CreateIndex(
                name: "IX_decomposition_batches_tenant_id",
                table: "decomposition_batches",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "FK_basal_schedules_decomposition_batches_correlation_id",
                table: "basal_schedules",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_bg_checks_decomposition_batches_correlation_id",
                table: "bg_checks",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_bolus_calculations_decomposition_batches_correlation_id",
                table: "bolus_calculations",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_boluses_decomposition_batches_correlation_id",
                table: "boluses",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_calibrations_decomposition_batches_correlation_id",
                table: "calibrations",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_carb_intakes_decomposition_batches_correlation_id",
                table: "carb_intakes",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_carb_ratio_schedules_decomposition_batches_correlation_id",
                table: "carb_ratio_schedules",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_device_events_decomposition_batches_correlation_id",
                table: "device_events",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_meter_glucose_decomposition_batches_correlation_id",
                table: "meter_glucose",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_notes_decomposition_batches_correlation_id",
                table: "notes",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_sensitivity_schedules_decomposition_batches_correlation_id",
                table: "sensitivity_schedules",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_sensor_glucose_decomposition_batches_correlation_id",
                table: "sensor_glucose",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_target_range_schedules_decomposition_batches_correlation_id",
                table: "target_range_schedules",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_temp_basals_decomposition_batches_correlation_id",
                table: "temp_basals",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_therapy_settings_decomposition_batches_correlation_id",
                table: "therapy_settings",
                column: "correlation_id",
                principalTable: "decomposition_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_basal_schedules_decomposition_batches_correlation_id",
                table: "basal_schedules");

            migrationBuilder.DropForeignKey(
                name: "FK_bg_checks_decomposition_batches_correlation_id",
                table: "bg_checks");

            migrationBuilder.DropForeignKey(
                name: "FK_bolus_calculations_decomposition_batches_correlation_id",
                table: "bolus_calculations");

            migrationBuilder.DropForeignKey(
                name: "FK_boluses_decomposition_batches_correlation_id",
                table: "boluses");

            migrationBuilder.DropForeignKey(
                name: "FK_calibrations_decomposition_batches_correlation_id",
                table: "calibrations");

            migrationBuilder.DropForeignKey(
                name: "FK_carb_intakes_decomposition_batches_correlation_id",
                table: "carb_intakes");

            migrationBuilder.DropForeignKey(
                name: "FK_carb_ratio_schedules_decomposition_batches_correlation_id",
                table: "carb_ratio_schedules");

            migrationBuilder.DropForeignKey(
                name: "FK_device_events_decomposition_batches_correlation_id",
                table: "device_events");

            migrationBuilder.DropForeignKey(
                name: "FK_meter_glucose_decomposition_batches_correlation_id",
                table: "meter_glucose");

            migrationBuilder.DropForeignKey(
                name: "FK_notes_decomposition_batches_correlation_id",
                table: "notes");

            migrationBuilder.DropForeignKey(
                name: "FK_sensitivity_schedules_decomposition_batches_correlation_id",
                table: "sensitivity_schedules");

            migrationBuilder.DropForeignKey(
                name: "FK_sensor_glucose_decomposition_batches_correlation_id",
                table: "sensor_glucose");

            migrationBuilder.DropForeignKey(
                name: "FK_target_range_schedules_decomposition_batches_correlation_id",
                table: "target_range_schedules");

            migrationBuilder.DropForeignKey(
                name: "FK_temp_basals_decomposition_batches_correlation_id",
                table: "temp_basals");

            migrationBuilder.DropForeignKey(
                name: "FK_therapy_settings_decomposition_batches_correlation_id",
                table: "therapy_settings");

            migrationBuilder.DropTable(
                name: "decomposition_batches");
        }
    }
}
