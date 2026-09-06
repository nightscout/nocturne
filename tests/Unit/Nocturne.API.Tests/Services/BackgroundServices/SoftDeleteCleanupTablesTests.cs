using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// Pins the set of tables <see cref="SoftDeleteCleanupService"/> purges.
/// </summary>
[Trait("Category", "Unit")]
public class SoftDeleteCleanupTablesTests
{
    private static readonly string[] Expected =
    [
        "aps_snapshots", "basal_injections", "basal_schedules", "bg_checks", "body_weights",
        "bolus_calculations", "boluses", "calibrations", "carb_intakes", "carb_ratio_schedules",
        "device_events", "device_status_extras", "devices", "heart_rates", "meter_glucose",
        "notes", "patient_devices", "patient_insulins", "patient_records", "pump_snapshots",
        "sensitivity_schedules", "sensor_glucose", "state_spans", "step_counts",
        "target_range_schedules", "temp_basals", "therapy_settings", "uploader_snapshots"
    ];

    [Fact]
    public void SoftDeletableTables_CoversEveryTenantScopedSoftDeletableTable()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        using var context = new NocturneDbContext(options);

        var tables = SoftDeleteCleanupService.SoftDeletableTables(context.Model);

        tables.Should().Equal(Expected);
    }
}
