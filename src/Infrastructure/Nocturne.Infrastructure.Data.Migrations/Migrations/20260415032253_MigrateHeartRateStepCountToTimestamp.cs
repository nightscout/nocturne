using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MigrateHeartRateStepCountToTimestamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- heart_rates ---

            // 1. Add nullable timestamp column
            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "heart_rates",
                type: "timestamp with time zone",
                nullable: true);

            // 2. Populate timestamp from mills
            migrationBuilder.Sql(
                "UPDATE heart_rates SET \"timestamp\" = to_timestamp(mills::double precision / 1000.0) AT TIME ZONE 'UTC'");

            // 3. Make timestamp NOT NULL
            migrationBuilder.AlterColumn<DateTime>(
                name: "timestamp",
                table: "heart_rates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // 4. Drop old index, columns
            migrationBuilder.DropIndex(
                name: "ix_heart_rates_mills",
                table: "heart_rates");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "heart_rates");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "heart_rates");

            // 5. Create new index on timestamp
            migrationBuilder.CreateIndex(
                name: "ix_heart_rates_timestamp",
                table: "heart_rates",
                column: "timestamp",
                descending: new bool[0]);

            // --- step_counts ---

            // 1. Add nullable timestamp column
            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "step_counts",
                type: "timestamp with time zone",
                nullable: true);

            // 2. Populate timestamp from mills
            migrationBuilder.Sql(
                "UPDATE step_counts SET \"timestamp\" = to_timestamp(mills::double precision / 1000.0) AT TIME ZONE 'UTC'");

            // 3. Make timestamp NOT NULL
            migrationBuilder.AlterColumn<DateTime>(
                name: "timestamp",
                table: "step_counts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // 4. Drop old index, columns
            migrationBuilder.DropIndex(
                name: "ix_step_counts_mills",
                table: "step_counts");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "step_counts");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "step_counts");

            // 5. Create new index on timestamp
            migrationBuilder.CreateIndex(
                name: "ix_step_counts_timestamp",
                table: "step_counts",
                column: "timestamp",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // --- heart_rates ---

            migrationBuilder.DropIndex(
                name: "ix_heart_rates_timestamp",
                table: "heart_rates");

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "heart_rates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql(
                "UPDATE heart_rates SET mills = (EXTRACT(EPOCH FROM \"timestamp\") * 1000)::bigint");

            migrationBuilder.AddColumn<string>(
                name: "created_at",
                table: "heart_rates",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "heart_rates");

            migrationBuilder.CreateIndex(
                name: "ix_heart_rates_mills",
                table: "heart_rates",
                column: "mills",
                descending: new bool[0]);

            // --- step_counts ---

            migrationBuilder.DropIndex(
                name: "ix_step_counts_timestamp",
                table: "step_counts");

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "step_counts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql(
                "UPDATE step_counts SET mills = (EXTRACT(EPOCH FROM \"timestamp\") * 1000)::bigint");

            migrationBuilder.AddColumn<string>(
                name: "created_at",
                table: "step_counts",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "step_counts");

            migrationBuilder.CreateIndex(
                name: "ix_step_counts_mills",
                table: "step_counts",
                column: "mills",
                descending: new bool[0]);
        }
    }
}
