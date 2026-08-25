using Nocturne.Core.Constants;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Demo;

/// <summary>
/// The one owner of "everything the demo service wrote": the table list and the delete that empties
/// it, for every caller that clears demo data. Takes the context so a caller may bring either
/// tenant-scoping pattern — an injected scoped context, or a factory context with
/// <see cref="NocturneDbContext.TenantId"/> pinned from the demo tenant lookup.
/// </summary>
/// <remarks>
/// Hard delete (<see cref="PurgeExtensions.PurgeAsync{TEntity}"/>), unlike the audited soft-delete
/// every other source's delete routes through: demo data is regenerated wholesale, and the primary
/// path that does so — <see cref="DemoTenantService.ResetAsync"/> — drops the tenant row and lets the
/// database cascade clear every tenant-scoped table before the seeder refills it. A
/// <c>deleted_by_user</c> stamp would only block that regeneration, and the audit trail it would be
/// written for does not survive the reset either.
/// </remarks>
internal static class DemoDataPurge
{
    /// <summary>Glucose the demo service wrote: sensor, meter and calibration readings.</summary>
    public static async Task<long> PurgeEntriesAsync(NocturneDbContext db, CancellationToken ct)
    {
        long deleted = await db.SensorGlucose.PurgeAsync(SourceFilter.For<SensorGlucoseEntity>(DataSources.DemoService), ct);
        deleted += await db.MeterGlucose.PurgeAsync(SourceFilter.For<MeterGlucoseEntity>(DataSources.DemoService), ct);
        deleted += await db.Calibrations.PurgeAsync(SourceFilter.For<CalibrationEntity>(DataSources.DemoService), ct);
        return deleted;
    }

    /// <summary>
    /// Treatments the demo service wrote: boluses, carbs, BG checks, notes, device events, bolus
    /// calculations, temp basals and state spans.
    /// </summary>
    public static async Task<long> PurgeTreatmentsAsync(NocturneDbContext db, CancellationToken ct)
    {
        long deleted = await db.Boluses.PurgeAsync(SourceFilter.For<BolusEntity>(DataSources.DemoService), ct);
        deleted += await db.CarbIntakes.PurgeAsync(SourceFilter.For<CarbIntakeEntity>(DataSources.DemoService), ct);
        deleted += await db.BGChecks.PurgeAsync(SourceFilter.For<BGCheckEntity>(DataSources.DemoService), ct);
        deleted += await db.Notes.PurgeAsync(SourceFilter.For<NoteEntity>(DataSources.DemoService), ct);
        deleted += await db.DeviceEvents.PurgeAsync(SourceFilter.For<DeviceEventEntity>(DataSources.DemoService), ct);
        deleted += await db.BolusCalculations.PurgeAsync(SourceFilter.For<BolusCalculationEntity>(DataSources.DemoService), ct);
        deleted += await db.TempBasals.PurgeAsync(SourceFilter.For<TempBasalEntity>(DataSources.DemoService), ct);
        deleted += await db.StateSpans.PurgeAsync(s => s.Source == DataSources.DemoService, ct);
        return deleted;
    }

    /// <summary>APS snapshots the demo service wrote.</summary>
    public static async Task<long> PurgeDeviceStatusAsync(NocturneDbContext db, CancellationToken ct) =>
        await db.ApsSnapshots.PurgeAsync(SourceFilter.For<ApsSnapshotEntity>(DataSources.DemoService), ct);
}
