using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Rewrites the patient device categories the sample data seeder stored as <c>pump</c> and
    /// <c>meter</c> to the <c>DeviceCategory</c> names they stand for. Neither parses as a member, so
    /// the mapper read both as a CGM. <c>Down</c> leaves the names in place: the old values were
    /// never valid.
    /// </summary>
    public partial class NormalisePatientDeviceCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    r RECORD;
                BEGIN
                    FOR r IN SELECT id FROM tenants
                    LOOP
                        PERFORM set_config('app.current_tenant_id', r.id::text, true);
                        UPDATE patient_devices SET device_category = 'InsulinPump'
                        WHERE tenant_id = r.id AND lower(device_category) = 'pump';
                        UPDATE patient_devices SET device_category = 'GlucoseMeter'
                        WHERE tenant_id = r.id AND lower(device_category) = 'meter';
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
