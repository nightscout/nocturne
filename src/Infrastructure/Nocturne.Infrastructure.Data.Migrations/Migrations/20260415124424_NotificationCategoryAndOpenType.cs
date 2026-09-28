using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationCategoryAndOpenType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_in_app_notifications_user_type_archived",
                table: "in_app_notifications");

            migrationBuilder.AlterColumn<string>(
                name: "type",
                table: "in_app_notifications",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "in_app_notifications",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Informational");

            migrationBuilder.AddColumn<string>(
                name: "icon",
                table: "in_app_notifications",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "in_app_notifications",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_in_app_notifications_user_type_source_archived",
                table: "in_app_notifications",
                columns: new[] { "user_id", "type", "source_id", "is_archived" });

            // Data migration: convert old enum values to new type strings + categories
            // Must set tenant context per-tenant because FORCE RLS is enabled
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE in_app_notifications SET
                            category = CASE type
                                WHEN 'TrackerAlert' THEN 'Alert'
                                WHEN 'PredictedLow' THEN 'Alert'
                                WHEN 'UnconfiguredTracker' THEN 'ActionRequired'
                                WHEN 'AnonymousLoginRequest' THEN 'ActionRequired'
                                WHEN 'SuggestedMealMatch' THEN 'ActionRequired'
                                WHEN 'SuggestedTrackerMatch' THEN 'ActionRequired'
                                WHEN 'CompressionLowReview' THEN 'ActionRequired'
                                WHEN 'StatisticsSummary' THEN 'Informational'
                                WHEN 'HelpResponse' THEN 'Informational'
                                ELSE 'Informational'
                            END,
                            type = CASE type
                                WHEN 'UnconfiguredTracker' THEN 'tracker.unconfigured'
                                WHEN 'TrackerAlert' THEN 'tracker.alert'
                                WHEN 'StatisticsSummary' THEN 'system.statistics_summary'
                                WHEN 'HelpResponse' THEN 'system.help_response'
                                WHEN 'AnonymousLoginRequest' THEN 'passkey.anonymous_login_request'
                                WHEN 'PredictedLow' THEN 'glucose.predicted_low'
                                WHEN 'SuggestedMealMatch' THEN 'meal_matching.suggested_match'
                                WHEN 'SuggestedTrackerMatch' THEN 'tracker.suggested_match'
                                WHEN 'CompressionLowReview' THEN 'glucose.compression_low_review'
                                ELSE type
                            END;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_in_app_notifications_user_type_source_archived",
                table: "in_app_notifications");

            migrationBuilder.DropColumn(
                name: "category",
                table: "in_app_notifications");

            migrationBuilder.DropColumn(
                name: "icon",
                table: "in_app_notifications");

            migrationBuilder.DropColumn(
                name: "source",
                table: "in_app_notifications");

            migrationBuilder.AlterColumn<string>(
                name: "type",
                table: "in_app_notifications",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.CreateIndex(
                name: "ix_in_app_notifications_user_type_archived",
                table: "in_app_notifications",
                columns: new[] { "user_id", "type", "is_archived" });
        }
    }
}
