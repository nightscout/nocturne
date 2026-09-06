using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Projects V4 granular records back into the legacy <see cref="Entry"/> and <see cref="Treatment"/>
/// shapes for v1/v2/v3 API compatibility.
/// </summary>
/// <remarks>
/// This service is the read side of the dual-path architecture.
/// The write side (legacy record → V4 typed record) is handled by <see cref="DecompositionPipeline"/>.
/// Projection covers: <see cref="SensorGlucose"/> → <see cref="Entry"/>,
/// <see cref="Bolus"/> and <see cref="CarbIntake"/> → <see cref="Treatment"/>,
/// <see cref="DeviceEvent"/> → <see cref="Treatment"/> using the legacy event-type map.
/// <para>
/// Entries are projected V4-native-only, supplementing the rows the legacy entries table still
/// holds. Treatments have no legacy table left to supplement, so every treatment read projects
/// records of both provenances — see <see cref="LegacyTreatmentRange.NativeOnly"/>.
/// </para>
/// </remarks>
/// <seealso cref="IV4ToLegacyProjectionService"/>
/// <seealso cref="LegacyTreatmentTables"/>
/// <seealso cref="DecompositionPipeline"/>
public class V4ToLegacyProjectionService : IV4ToLegacyProjectionService
{
    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly LegacyTreatmentRepositories _repositories;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly NocturneDbContext _dbContext;
    private readonly ILogger<V4ToLegacyProjectionService> _logger;

    public V4ToLegacyProjectionService(
        ISensorGlucoseRepository sensorGlucoseRepository,
        IBolusRepository bolusRepository,
        ICarbIntakeRepository carbIntakeRepository,
        IBGCheckRepository bgCheckRepository,
        INoteRepository noteRepository,
        IDeviceEventRepository deviceEventRepository,
        ITempBasalRepository tempBasalRepository,
        IBolusCalculationRepository bolusCalculationRepository,
        ITreatmentFoodService treatmentFoodService,
        NocturneDbContext dbContext,
        ILogger<V4ToLegacyProjectionService> logger
    )
    {
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _repositories = new LegacyTreatmentRepositories(
            bolusRepository,
            carbIntakeRepository,
            bgCheckRepository,
            noteRepository,
            deviceEventRepository,
            tempBasalRepository,
            bolusCalculationRepository
        );
        _treatmentFoodService = treatmentFoodService;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <summary>
    /// Converts nullable unix milliseconds to nullable DateTime.
    /// </summary>
    private static DateTime? MillsToDateTime(long? mills) =>
        mills.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(mills.Value).UtcDateTime : null;

    public async Task<IEnumerable<Entry>> GetProjectedEntriesAsync(
        long? fromMills,
        long? toMills,
        int limit,
        int offset,
        bool descending,
        CancellationToken ct = default
    )
    {
        IEnumerable<SensorGlucose> records;
        try
        {
            records = await _sensorGlucoseRepository.GetAsync(
                from: MillsToDateTime(fromMills),
                to: MillsToDateTime(toMills),
                device: null,
                source: null,
                limit: limit,
                offset: offset,
                descending: descending,
                nativeOnly: true,
                ct: ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch V4 SensorGlucose records for projection");
            return Enumerable.Empty<Entry>();
        }

        return records.Select(ProjectSensorGlucoseToEntry);
    }

    /// <inheritdoc />
    public async Task<Entry?> GetLatestProjectedEntryAsync(CancellationToken ct = default)
    {
        IEnumerable<SensorGlucose> records;
        try
        {
            records = await _sensorGlucoseRepository.GetAsync(
                from: null,
                to: null,
                device: null,
                source: null,
                limit: 1,
                offset: 0,
                descending: true,
                nativeOnly: true,
                ct: ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch V4 SensorGlucose records for latest projection");
            return null;
        }

        var latest = records.FirstOrDefault();
        return latest == null ? null : ProjectSensorGlucoseToEntry(latest);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetProjectedTreatmentsAsync(
        long? fromMills,
        long? toMills,
        int limit,
        bool nativeOnly = true,
        CancellationToken ct = default
    )
    {
        var range = new LegacyTreatmentRange(
            MillsToDateTime(fromMills), MillsToDateTime(toMills), limit, nativeOnly);

        var rows = await FetchAsync(table => table.InRangeAsync(_repositories, range, ct));
        var treatments = await AssembleAsync(rows, ct);

        return treatments.OrderByDescending(t => t.Mills).Take(limit);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetProjectedTreatmentsModifiedSinceAsync(
        long lastModifiedMills, int limit, CancellationToken ct = default)
    {
        // Strictly-greater (not >=) so the cursor record AAPS already holds is not
        // re-returned; an inclusive bound makes AAPS re-request the same page in a loop.
        var threshold = DateTimeOffset.FromUnixTimeMilliseconds(lastModifiedMills).UtcDateTime;

        var rows = await FetchAsync(
            table => table.ModifiedSinceAsync(_dbContext, threshold, limit, ct));

        // The page is cut on raw row stamps and only then paired. Each type contributes its own
        // oldest `limit` rows, so this merge holds the globally-oldest `limit`, and every row left
        // behind is strictly newer than every row returned. Pairing before the cut breaks that: a
        // meal carries its newer constituent's stamp, so it can sort past the cut while the cursor
        // — max(srvModified) over the page — still advances beyond the older constituent's own row,
        // which is then never fetched again. Pairing after the cut can only merge rows that were
        // both delivered, so a pair split by the cut simply arrives as two treatments.
        var page = rows.OrderBy(r => r.Modified).Take(limit).ToList();
        var treatments = await AssembleAsync(page, ct);

        return treatments.OrderBy(t => t.SrvModified ?? t.Mills);
    }

    /// <summary>
    /// Reads every record type through <paramref name="fetch"/>. Types are read sequentially: they
    /// share a scoped DbContext, which is not thread-safe, so they cannot be run concurrently via
    /// <see cref="Task.WhenAll(Task[])"/>. A type whose read fails contributes nothing rather than
    /// failing the whole page.
    /// </summary>
    private async Task<List<FetchedRecord>> FetchAsync(
        Func<ILegacyTreatmentTable, Task<IReadOnlyList<FetchedRecord>>> fetch
    )
    {
        var rows = new List<FetchedRecord>();
        foreach (var table in LegacyTreatmentTables.All)
            rows.AddRange(await FetchSafe(table, fetch));

        return rows;
    }

    private async Task<List<Treatment>> AssembleAsync(
        IReadOnlyList<FetchedRecord> rows, CancellationToken ct
    ) => LegacyTreatmentTables.Assemble(rows, await LoadCarbFoodsAsync(rows, ct));

    private async Task<CarbFoodIndex> LoadCarbFoodsAsync(
        IReadOnlyList<FetchedRecord> rows,
        CancellationToken ct
    )
    {
        var carbIntakeIds = rows
            .Select(r => r.Record)
            .OfType<CarbIntake>()
            .Select(c => c.Id)
            .ToList();

        return carbIntakeIds.Count == 0
            ? CarbFoodIndex.Empty
            : new CarbFoodIndex(await _treatmentFoodService.GetByCarbIntakeIdsAsync(carbIntakeIds, ct));
    }

    private async Task<IReadOnlyList<FetchedRecord>> FetchSafe(
        ILegacyTreatmentTable table,
        Func<ILegacyTreatmentTable, Task<IReadOnlyList<FetchedRecord>>> fetch
    )
    {
        try
        {
            return await fetch(table);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to fetch V4 records of type {Type} for legacy projection",
                table.RecordType);
            return [];
        }
    }

    private static Entry ProjectSensorGlucoseToEntry(SensorGlucose sg) =>
        SensorGlucoseToEntryMapper.ToEntry(sg);
}
