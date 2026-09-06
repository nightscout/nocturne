using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthMetricsSyncDedup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_step_counts_tenant_id",
                table: "step_counts");

            migrationBuilder.DropIndex(
                name: "IX_heart_rates_tenant_id",
                table: "heart_rates");

            migrationBuilder.DropIndex(
                name: "IX_body_weights_tenant_id",
                table: "body_weights");

            migrationBuilder.AddColumn<string>(
                name: "data_source",
                table: "step_counts",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "step_counts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "step_counts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "sync_identifier",
                table: "step_counts",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "data_source",
                table: "heart_rates",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "heart_rates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "heart_rates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "sync_identifier",
                table: "heart_rates",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "data_source",
                table: "body_weights",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "body_weights",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "body_weights",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "sync_identifier",
                table: "body_weights",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_step_counts_tenant_source_sync_id",
                table: "step_counts",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_step_counts_tenant_timestamp",
                table: "step_counts",
                columns: new[] { "tenant_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_heart_rates_tenant_source_sync_id",
                table: "heart_rates",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_heart_rates_tenant_timestamp",
                table: "heart_rates",
                columns: new[] { "tenant_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_body_weights_tenant_mills",
                table: "body_weights",
                columns: new[] { "tenant_id", "mills" });

            migrationBuilder.CreateIndex(
                name: "ix_body_weights_tenant_source_sync_id",
                table: "body_weights",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL AND deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_step_counts_tenant_source_sync_id",
                table: "step_counts");

            migrationBuilder.DropIndex(
                name: "ix_step_counts_tenant_timestamp",
                table: "step_counts");

            migrationBuilder.DropIndex(
                name: "ix_heart_rates_tenant_source_sync_id",
                table: "heart_rates");

            migrationBuilder.DropIndex(
                name: "ix_heart_rates_tenant_timestamp",
                table: "heart_rates");

            migrationBuilder.DropIndex(
                name: "ix_body_weights_tenant_mills",
                table: "body_weights");

            migrationBuilder.DropIndex(
                name: "ix_body_weights_tenant_source_sync_id",
                table: "body_weights");

            migrationBuilder.DropColumn(
                name: "data_source",
                table: "step_counts");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "step_counts");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "step_counts");

            migrationBuilder.DropColumn(
                name: "sync_identifier",
                table: "step_counts");

            migrationBuilder.DropColumn(
                name: "data_source",
                table: "heart_rates");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "heart_rates");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "heart_rates");

            migrationBuilder.DropColumn(
                name: "sync_identifier",
                table: "heart_rates");

            migrationBuilder.DropColumn(
                name: "data_source",
                table: "body_weights");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "body_weights");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "body_weights");

            migrationBuilder.DropColumn(
                name: "sync_identifier",
                table: "body_weights");

            migrationBuilder.CreateIndex(
                name: "IX_step_counts_tenant_id",
                table: "step_counts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_heart_rates_tenant_id",
                table: "heart_rates",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_body_weights_tenant_id",
                table: "body_weights",
                column: "tenant_id");
        }
    }
}
