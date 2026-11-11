using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// Pins the set of tables <see cref="SoftDeleteCleanupJob"/> purges.
/// </summary>
[Trait("Category", "Unit")]
public class SoftDeleteCleanupTablesTests
{
    private static readonly string[] Expected =
    [
        "aps_snapshots", "basal_injections", "basal_schedules", "bg_checks", "body_weights",
        "bolus_calculations", "boluses", "calibrations", "carb_intakes", "carb_ratio_schedules",
        "device_events", "device_status_extras", "devices", "foods", "heart_rates", "lab_hba1c_results",
        "meter_glucose", "notes", "patient_devices", "patient_insulins", "patient_records",
        "profile_activations", "pump_snapshots", "sensitivity_schedules", "sensor_glucose", "sleep_sessions", "state_spans", "step_counts",
        "target_range_schedules", "temp_basals", "therapy_settings", "uploader_snapshots"
    ];

    [Fact]
    public void SoftDeletableTables_CoversEveryTenantScopedSoftDeletableTable()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        using var context = new NocturneDbContext(options);

        var tables = SoftDeleteCleanupJob.SoftDeletableTables(context.Model);

        tables.Select(t => t.Table).Should().Equal(Expected);
    }

    [Fact]
    public void SoftDeletableTables_NamesTheLinkedRecordTypeOfEveryDeduplicatedTable()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        using var context = new NocturneDbContext(options);

        var linked = SoftDeleteCleanupJob.SoftDeletableTables(context.Model)
            .Where(t => t.LinkedRecordType is not null)
            .ToDictionary(t => t.Table, t => t.LinkedRecordType);

        linked.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["bg_checks"] = "bgcheck",
            ["bolus_calculations"] = "boluscalculation",
            ["boluses"] = "bolus",
            ["carb_intakes"] = "carbintake",
            ["device_events"] = "deviceevent",
            ["notes"] = "note",
            ["sensor_glucose"] = "sensorglucose",
            ["state_spans"] = "statespan",
            ["temp_basals"] = "tempbasal",
        }, "a deduplicated table purged without its key would leave orphaned links behind");
    }
}
