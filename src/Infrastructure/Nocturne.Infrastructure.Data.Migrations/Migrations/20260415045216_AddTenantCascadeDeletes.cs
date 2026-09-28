using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantCascadeDeletes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // -- Idempotent indexes (CREATE INDEX IF NOT EXISTS) --

            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_uploader_snapshots_tenant_id" ON uploader_snapshots (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_treatments_tenant_id" ON treatments (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_treatment_foods_tenant_id" ON treatment_foods (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_tracker_presets_tenant_id" ON tracker_presets (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_tracker_notification_thresholds_tenant_id" ON tracker_notification_thresholds (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_tracker_instances_tenant_id" ON tracker_instances (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_tracker_definitions_tenant_id" ON tracker_definitions (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_system_events_tenant_id" ON system_events (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_step_counts_tenant_id" ON step_counts (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_state_spans_tenant_id" ON state_spans (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_pump_snapshots_tenant_id" ON pump_snapshots (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_profiles_tenant_id" ON profiles (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_oauth_device_codes_tenant_id" ON oauth_device_codes (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_oauth_authorization_codes_tenant_id" ON oauth_authorization_codes (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_meter_glucose_tenant_id" ON meter_glucose (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_in_app_notifications_tenant_id" ON in_app_notifications (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_heart_rates_tenant_id" ON heart_rates (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_entries_tenant_id" ON entries (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_discrepancy_details_tenant_id" ON discrepancy_details (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_discrepancy_analyses_tenant_id" ON discrepancy_analyses (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_devicestatus_tenant_id" ON devicestatus (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_devices_tenant_id" ON devices (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_connector_configurations_tenant_id" ON connector_configurations (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_compression_low_suggestions_tenant_id" ON compression_low_suggestions (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_clock_faces_tenant_id" ON clock_faces (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_calibrations_tenant_id" ON calibrations (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_body_weights_tenant_id" ON body_weights (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_aps_snapshots_tenant_id" ON aps_snapshots (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_alert_tracker_state_tenant_id" ON alert_tracker_state (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_alert_step_channels_tenant_id" ON alert_step_channels (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_alert_schedules_tenant_id" ON alert_schedules (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_alert_rules_tenant_id" ON alert_rules (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_alert_invites_tenant_id" ON alert_invites (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_alert_instances_tenant_id" ON alert_instances (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_alert_escalation_steps_tenant_id" ON alert_escalation_steps (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_alert_deliveries_tenant_id" ON alert_deliveries (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_alert_custom_sounds_tenant_id" ON alert_custom_sounds (tenant_id);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_activities_tenant_id" ON activities (tenant_id);""");

            // -- Idempotent foreign keys (check pg_constraint before adding) --

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_activities_tenants_tenant_id') THEN
                        ALTER TABLE activities ADD CONSTRAINT "FK_activities_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_alert_custom_sounds_tenants_tenant_id') THEN
                        ALTER TABLE alert_custom_sounds ADD CONSTRAINT "FK_alert_custom_sounds_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_alert_deliveries_tenants_tenant_id') THEN
                        ALTER TABLE alert_deliveries ADD CONSTRAINT "FK_alert_deliveries_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_alert_escalation_steps_tenants_tenant_id') THEN
                        ALTER TABLE alert_escalation_steps ADD CONSTRAINT "FK_alert_escalation_steps_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_alert_excursions_tenants_tenant_id') THEN
                        ALTER TABLE alert_excursions ADD CONSTRAINT "FK_alert_excursions_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_alert_instances_tenants_tenant_id') THEN
                        ALTER TABLE alert_instances ADD CONSTRAINT "FK_alert_instances_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_alert_invites_tenants_tenant_id') THEN
                        ALTER TABLE alert_invites ADD CONSTRAINT "FK_alert_invites_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_alert_rules_tenants_tenant_id') THEN
                        ALTER TABLE alert_rules ADD CONSTRAINT "FK_alert_rules_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_alert_schedules_tenants_tenant_id') THEN
                        ALTER TABLE alert_schedules ADD CONSTRAINT "FK_alert_schedules_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_alert_step_channels_tenants_tenant_id') THEN
                        ALTER TABLE alert_step_channels ADD CONSTRAINT "FK_alert_step_channels_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_alert_tracker_state_tenants_tenant_id') THEN
                        ALTER TABLE alert_tracker_state ADD CONSTRAINT "FK_alert_tracker_state_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_aps_snapshots_tenants_tenant_id') THEN
                        ALTER TABLE aps_snapshots ADD CONSTRAINT "FK_aps_snapshots_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_basal_schedules_tenants_tenant_id') THEN
                        ALTER TABLE basal_schedules ADD CONSTRAINT "FK_basal_schedules_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_bg_checks_tenants_tenant_id') THEN
                        ALTER TABLE bg_checks ADD CONSTRAINT "FK_bg_checks_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_body_weights_tenants_tenant_id') THEN
                        ALTER TABLE body_weights ADD CONSTRAINT "FK_body_weights_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_bolus_calculations_tenants_tenant_id') THEN
                        ALTER TABLE bolus_calculations ADD CONSTRAINT "FK_bolus_calculations_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_boluses_tenants_tenant_id') THEN
                        ALTER TABLE boluses ADD CONSTRAINT "FK_boluses_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_calibrations_tenants_tenant_id') THEN
                        ALTER TABLE calibrations ADD CONSTRAINT "FK_calibrations_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_carb_intakes_tenants_tenant_id') THEN
                        ALTER TABLE carb_intakes ADD CONSTRAINT "FK_carb_intakes_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_carb_ratio_schedules_tenants_tenant_id') THEN
                        ALTER TABLE carb_ratio_schedules ADD CONSTRAINT "FK_carb_ratio_schedules_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_clock_faces_tenants_tenant_id') THEN
                        ALTER TABLE clock_faces ADD CONSTRAINT "FK_clock_faces_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_compression_low_suggestions_tenants_tenant_id') THEN
                        ALTER TABLE compression_low_suggestions ADD CONSTRAINT "FK_compression_low_suggestions_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_connector_configurations_tenants_tenant_id') THEN
                        ALTER TABLE connector_configurations ADD CONSTRAINT "FK_connector_configurations_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_connector_food_entries_tenants_tenant_id') THEN
                        ALTER TABLE connector_food_entries ADD CONSTRAINT "FK_connector_food_entries_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_data_source_metadata_tenants_tenant_id') THEN
                        ALTER TABLE data_source_metadata ADD CONSTRAINT "FK_data_source_metadata_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_device_events_tenants_tenant_id') THEN
                        ALTER TABLE device_events ADD CONSTRAINT "FK_device_events_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_devices_tenants_tenant_id') THEN
                        ALTER TABLE devices ADD CONSTRAINT "FK_devices_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_devicestatus_tenants_tenant_id') THEN
                        ALTER TABLE devicestatus ADD CONSTRAINT "FK_devicestatus_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_discrepancy_analyses_tenants_tenant_id') THEN
                        ALTER TABLE discrepancy_analyses ADD CONSTRAINT "FK_discrepancy_analyses_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_discrepancy_details_tenants_tenant_id') THEN
                        ALTER TABLE discrepancy_details ADD CONSTRAINT "FK_discrepancy_details_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_entries_tenants_tenant_id') THEN
                        ALTER TABLE entries ADD CONSTRAINT "FK_entries_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_foods_tenants_tenant_id') THEN
                        ALTER TABLE foods ADD CONSTRAINT "FK_foods_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_heart_rates_tenants_tenant_id') THEN
                        ALTER TABLE heart_rates ADD CONSTRAINT "FK_heart_rates_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_in_app_notifications_tenants_tenant_id') THEN
                        ALTER TABLE in_app_notifications ADD CONSTRAINT "FK_in_app_notifications_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_linked_records_tenants_tenant_id') THEN
                        ALTER TABLE linked_records ADD CONSTRAINT "FK_linked_records_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_meter_glucose_tenants_tenant_id') THEN
                        ALTER TABLE meter_glucose ADD CONSTRAINT "FK_meter_glucose_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_notes_tenants_tenant_id') THEN
                        ALTER TABLE notes ADD CONSTRAINT "FK_notes_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_oauth_authorization_codes_tenants_tenant_id') THEN
                        ALTER TABLE oauth_authorization_codes ADD CONSTRAINT "FK_oauth_authorization_codes_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_oauth_clients_tenants_tenant_id') THEN
                        ALTER TABLE oauth_clients ADD CONSTRAINT "FK_oauth_clients_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_oauth_device_codes_tenants_tenant_id') THEN
                        ALTER TABLE oauth_device_codes ADD CONSTRAINT "FK_oauth_device_codes_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_oauth_grants_tenants_tenant_id') THEN
                        ALTER TABLE oauth_grants ADD CONSTRAINT "FK_oauth_grants_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_patient_devices_tenants_tenant_id') THEN
                        ALTER TABLE patient_devices ADD CONSTRAINT "FK_patient_devices_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_patient_insulins_tenants_tenant_id') THEN
                        ALTER TABLE patient_insulins ADD CONSTRAINT "FK_patient_insulins_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_patient_records_tenants_tenant_id') THEN
                        ALTER TABLE patient_records ADD CONSTRAINT "FK_patient_records_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_profiles_tenants_tenant_id') THEN
                        ALTER TABLE profiles ADD CONSTRAINT "FK_profiles_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_pump_snapshots_tenants_tenant_id') THEN
                        ALTER TABLE pump_snapshots ADD CONSTRAINT "FK_pump_snapshots_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_sensitivity_schedules_tenants_tenant_id') THEN
                        ALTER TABLE sensitivity_schedules ADD CONSTRAINT "FK_sensitivity_schedules_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_sensor_glucose_tenants_tenant_id') THEN
                        ALTER TABLE sensor_glucose ADD CONSTRAINT "FK_sensor_glucose_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_settings_tenants_tenant_id') THEN
                        ALTER TABLE settings ADD CONSTRAINT "FK_settings_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_state_spans_tenants_tenant_id') THEN
                        ALTER TABLE state_spans ADD CONSTRAINT "FK_state_spans_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_step_counts_tenants_tenant_id') THEN
                        ALTER TABLE step_counts ADD CONSTRAINT "FK_step_counts_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_system_events_tenants_tenant_id') THEN
                        ALTER TABLE system_events ADD CONSTRAINT "FK_system_events_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_target_range_schedules_tenants_tenant_id') THEN
                        ALTER TABLE target_range_schedules ADD CONSTRAINT "FK_target_range_schedules_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_temp_basals_tenants_tenant_id') THEN
                        ALTER TABLE temp_basals ADD CONSTRAINT "FK_temp_basals_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_therapy_settings_tenants_tenant_id') THEN
                        ALTER TABLE therapy_settings ADD CONSTRAINT "FK_therapy_settings_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_tracker_definitions_tenants_tenant_id') THEN
                        ALTER TABLE tracker_definitions ADD CONSTRAINT "FK_tracker_definitions_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_tracker_instances_tenants_tenant_id') THEN
                        ALTER TABLE tracker_instances ADD CONSTRAINT "FK_tracker_instances_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_tracker_notification_thresholds_tenants_tenant_id') THEN
                        ALTER TABLE tracker_notification_thresholds ADD CONSTRAINT "FK_tracker_notification_thresholds_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_tracker_presets_tenants_tenant_id') THEN
                        ALTER TABLE tracker_presets ADD CONSTRAINT "FK_tracker_presets_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_treatment_foods_tenants_tenant_id') THEN
                        ALTER TABLE treatment_foods ADD CONSTRAINT "FK_treatment_foods_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_treatments_tenants_tenant_id') THEN
                        ALTER TABLE treatments ADD CONSTRAINT "FK_treatments_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_uploader_snapshots_tenants_tenant_id') THEN
                        ALTER TABLE uploader_snapshots ADD CONSTRAINT "FK_uploader_snapshots_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_user_food_favorites_tenants_tenant_id') THEN
                        ALTER TABLE user_food_favorites ADD CONSTRAINT "FK_user_food_favorites_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_activities_tenants_tenant_id",
                table: "activities");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_custom_sounds_tenants_tenant_id",
                table: "alert_custom_sounds");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_deliveries_tenants_tenant_id",
                table: "alert_deliveries");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_escalation_steps_tenants_tenant_id",
                table: "alert_escalation_steps");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_excursions_tenants_tenant_id",
                table: "alert_excursions");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_instances_tenants_tenant_id",
                table: "alert_instances");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_invites_tenants_tenant_id",
                table: "alert_invites");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_rules_tenants_tenant_id",
                table: "alert_rules");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_schedules_tenants_tenant_id",
                table: "alert_schedules");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_step_channels_tenants_tenant_id",
                table: "alert_step_channels");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_tracker_state_tenants_tenant_id",
                table: "alert_tracker_state");

            migrationBuilder.DropForeignKey(
                name: "FK_aps_snapshots_tenants_tenant_id",
                table: "aps_snapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_basal_schedules_tenants_tenant_id",
                table: "basal_schedules");

            migrationBuilder.DropForeignKey(
                name: "FK_bg_checks_tenants_tenant_id",
                table: "bg_checks");

            migrationBuilder.DropForeignKey(
                name: "FK_body_weights_tenants_tenant_id",
                table: "body_weights");

            migrationBuilder.DropForeignKey(
                name: "FK_bolus_calculations_tenants_tenant_id",
                table: "bolus_calculations");

            migrationBuilder.DropForeignKey(
                name: "FK_boluses_tenants_tenant_id",
                table: "boluses");

            migrationBuilder.DropForeignKey(
                name: "FK_calibrations_tenants_tenant_id",
                table: "calibrations");

            migrationBuilder.DropForeignKey(
                name: "FK_carb_intakes_tenants_tenant_id",
                table: "carb_intakes");

            migrationBuilder.DropForeignKey(
                name: "FK_carb_ratio_schedules_tenants_tenant_id",
                table: "carb_ratio_schedules");

            migrationBuilder.DropForeignKey(
                name: "FK_clock_faces_tenants_tenant_id",
                table: "clock_faces");

            migrationBuilder.DropForeignKey(
                name: "FK_compression_low_suggestions_tenants_tenant_id",
                table: "compression_low_suggestions");

            migrationBuilder.DropForeignKey(
                name: "FK_connector_configurations_tenants_tenant_id",
                table: "connector_configurations");

            migrationBuilder.DropForeignKey(
                name: "FK_connector_food_entries_tenants_tenant_id",
                table: "connector_food_entries");

            migrationBuilder.DropForeignKey(
                name: "FK_data_source_metadata_tenants_tenant_id",
                table: "data_source_metadata");

            migrationBuilder.DropForeignKey(
                name: "FK_device_events_tenants_tenant_id",
                table: "device_events");

            migrationBuilder.DropForeignKey(
                name: "FK_devices_tenants_tenant_id",
                table: "devices");

            migrationBuilder.DropForeignKey(
                name: "FK_devicestatus_tenants_tenant_id",
                table: "devicestatus");

            migrationBuilder.DropForeignKey(
                name: "FK_discrepancy_analyses_tenants_tenant_id",
                table: "discrepancy_analyses");

            migrationBuilder.DropForeignKey(
                name: "FK_discrepancy_details_tenants_tenant_id",
                table: "discrepancy_details");

            migrationBuilder.DropForeignKey(
                name: "FK_entries_tenants_tenant_id",
                table: "entries");

            migrationBuilder.DropForeignKey(
                name: "FK_foods_tenants_tenant_id",
                table: "foods");

            migrationBuilder.DropForeignKey(
                name: "FK_heart_rates_tenants_tenant_id",
                table: "heart_rates");

            migrationBuilder.DropForeignKey(
                name: "FK_in_app_notifications_tenants_tenant_id",
                table: "in_app_notifications");

            migrationBuilder.DropForeignKey(
                name: "FK_linked_records_tenants_tenant_id",
                table: "linked_records");

            migrationBuilder.DropForeignKey(
                name: "FK_meter_glucose_tenants_tenant_id",
                table: "meter_glucose");

            migrationBuilder.DropForeignKey(
                name: "FK_notes_tenants_tenant_id",
                table: "notes");

            migrationBuilder.DropForeignKey(
                name: "FK_oauth_authorization_codes_tenants_tenant_id",
                table: "oauth_authorization_codes");

            migrationBuilder.DropForeignKey(
                name: "FK_oauth_clients_tenants_tenant_id",
                table: "oauth_clients");

            migrationBuilder.DropForeignKey(
                name: "FK_oauth_device_codes_tenants_tenant_id",
                table: "oauth_device_codes");

            migrationBuilder.DropForeignKey(
                name: "FK_oauth_grants_tenants_tenant_id",
                table: "oauth_grants");

            migrationBuilder.DropForeignKey(
                name: "FK_patient_devices_tenants_tenant_id",
                table: "patient_devices");

            migrationBuilder.DropForeignKey(
                name: "FK_patient_insulins_tenants_tenant_id",
                table: "patient_insulins");

            migrationBuilder.DropForeignKey(
                name: "FK_patient_records_tenants_tenant_id",
                table: "patient_records");

            migrationBuilder.DropForeignKey(
                name: "FK_profiles_tenants_tenant_id",
                table: "profiles");

            migrationBuilder.DropForeignKey(
                name: "FK_pump_snapshots_tenants_tenant_id",
                table: "pump_snapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_sensitivity_schedules_tenants_tenant_id",
                table: "sensitivity_schedules");

            migrationBuilder.DropForeignKey(
                name: "FK_sensor_glucose_tenants_tenant_id",
                table: "sensor_glucose");

            migrationBuilder.DropForeignKey(
                name: "FK_settings_tenants_tenant_id",
                table: "settings");

            migrationBuilder.DropForeignKey(
                name: "FK_state_spans_tenants_tenant_id",
                table: "state_spans");

            migrationBuilder.DropForeignKey(
                name: "FK_step_counts_tenants_tenant_id",
                table: "step_counts");

            migrationBuilder.DropForeignKey(
                name: "FK_system_events_tenants_tenant_id",
                table: "system_events");

            migrationBuilder.DropForeignKey(
                name: "FK_target_range_schedules_tenants_tenant_id",
                table: "target_range_schedules");

            migrationBuilder.DropForeignKey(
                name: "FK_temp_basals_tenants_tenant_id",
                table: "temp_basals");

            migrationBuilder.DropForeignKey(
                name: "FK_therapy_settings_tenants_tenant_id",
                table: "therapy_settings");

            migrationBuilder.DropForeignKey(
                name: "FK_tracker_definitions_tenants_tenant_id",
                table: "tracker_definitions");

            migrationBuilder.DropForeignKey(
                name: "FK_tracker_instances_tenants_tenant_id",
                table: "tracker_instances");

            migrationBuilder.DropForeignKey(
                name: "FK_tracker_notification_thresholds_tenants_tenant_id",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropForeignKey(
                name: "FK_tracker_presets_tenants_tenant_id",
                table: "tracker_presets");

            migrationBuilder.DropForeignKey(
                name: "FK_treatment_foods_tenants_tenant_id",
                table: "treatment_foods");

            migrationBuilder.DropForeignKey(
                name: "FK_treatments_tenants_tenant_id",
                table: "treatments");

            migrationBuilder.DropForeignKey(
                name: "FK_uploader_snapshots_tenants_tenant_id",
                table: "uploader_snapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_user_food_favorites_tenants_tenant_id",
                table: "user_food_favorites");

            migrationBuilder.DropIndex(
                name: "IX_uploader_snapshots_tenant_id",
                table: "uploader_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_treatments_tenant_id",
                table: "treatments");

            migrationBuilder.DropIndex(
                name: "IX_treatment_foods_tenant_id",
                table: "treatment_foods");

            migrationBuilder.DropIndex(
                name: "IX_tracker_presets_tenant_id",
                table: "tracker_presets");

            migrationBuilder.DropIndex(
                name: "IX_tracker_notification_thresholds_tenant_id",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropIndex(
                name: "IX_tracker_instances_tenant_id",
                table: "tracker_instances");

            migrationBuilder.DropIndex(
                name: "IX_tracker_definitions_tenant_id",
                table: "tracker_definitions");

            migrationBuilder.DropIndex(
                name: "IX_system_events_tenant_id",
                table: "system_events");

            migrationBuilder.DropIndex(
                name: "IX_step_counts_tenant_id",
                table: "step_counts");

            migrationBuilder.DropIndex(
                name: "IX_state_spans_tenant_id",
                table: "state_spans");

            migrationBuilder.DropIndex(
                name: "IX_pump_snapshots_tenant_id",
                table: "pump_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_profiles_tenant_id",
                table: "profiles");

            migrationBuilder.DropIndex(
                name: "IX_oauth_device_codes_tenant_id",
                table: "oauth_device_codes");

            migrationBuilder.DropIndex(
                name: "IX_oauth_authorization_codes_tenant_id",
                table: "oauth_authorization_codes");

            migrationBuilder.DropIndex(
                name: "IX_meter_glucose_tenant_id",
                table: "meter_glucose");

            migrationBuilder.DropIndex(
                name: "IX_in_app_notifications_tenant_id",
                table: "in_app_notifications");

            migrationBuilder.DropIndex(
                name: "IX_heart_rates_tenant_id",
                table: "heart_rates");

            migrationBuilder.DropIndex(
                name: "IX_entries_tenant_id",
                table: "entries");

            migrationBuilder.DropIndex(
                name: "IX_discrepancy_details_tenant_id",
                table: "discrepancy_details");

            migrationBuilder.DropIndex(
                name: "IX_discrepancy_analyses_tenant_id",
                table: "discrepancy_analyses");

            migrationBuilder.DropIndex(
                name: "IX_devicestatus_tenant_id",
                table: "devicestatus");

            migrationBuilder.DropIndex(
                name: "IX_devices_tenant_id",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "IX_connector_configurations_tenant_id",
                table: "connector_configurations");

            migrationBuilder.DropIndex(
                name: "IX_compression_low_suggestions_tenant_id",
                table: "compression_low_suggestions");

            migrationBuilder.DropIndex(
                name: "IX_clock_faces_tenant_id",
                table: "clock_faces");

            migrationBuilder.DropIndex(
                name: "IX_calibrations_tenant_id",
                table: "calibrations");

            migrationBuilder.DropIndex(
                name: "IX_body_weights_tenant_id",
                table: "body_weights");

            migrationBuilder.DropIndex(
                name: "IX_aps_snapshots_tenant_id",
                table: "aps_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_alert_tracker_state_tenant_id",
                table: "alert_tracker_state");

            migrationBuilder.DropIndex(
                name: "IX_alert_step_channels_tenant_id",
                table: "alert_step_channels");

            migrationBuilder.DropIndex(
                name: "IX_alert_schedules_tenant_id",
                table: "alert_schedules");

            migrationBuilder.DropIndex(
                name: "IX_alert_rules_tenant_id",
                table: "alert_rules");

            migrationBuilder.DropIndex(
                name: "IX_alert_invites_tenant_id",
                table: "alert_invites");

            migrationBuilder.DropIndex(
                name: "IX_alert_instances_tenant_id",
                table: "alert_instances");

            migrationBuilder.DropIndex(
                name: "IX_alert_escalation_steps_tenant_id",
                table: "alert_escalation_steps");

            migrationBuilder.DropIndex(
                name: "IX_alert_deliveries_tenant_id",
                table: "alert_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_alert_custom_sounds_tenant_id",
                table: "alert_custom_sounds");

            migrationBuilder.DropIndex(
                name: "IX_activities_tenant_id",
                table: "activities");
        }
    }
}
