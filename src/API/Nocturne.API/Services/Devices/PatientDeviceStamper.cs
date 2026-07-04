using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Timezones;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// Default <see cref="IPatientDeviceStamper"/>: matches records against the patient devices whose
/// usage window covers the record's timestamp, strongest signal first.
/// </summary>
/// <remarks>
/// Matching ladder per record: (1) unique serial-number match of a candidate's
/// <see cref="PatientDevice.SerialNumber"/> inside the record's device string; (2) the single
/// candidate whose manufacturer is compatible with the record's data source. A source that maps to
/// a specific manufacturer (e.g. "dexcom-connector") excludes candidates of other manufacturers;
/// aggregator and uploader sources (Glooko, Nightscout, xDrip) constrain nothing. Records that
/// remain ambiguous — e.g. two same-manufacturer CGMs on one connector with no serial in the data —
/// are left unstamped rather than guessed.
/// </remarks>
/// <seealso cref="DeviceService"/>
internal sealed class PatientDeviceStamper : IPatientDeviceStamper
{
    private readonly IPatientDeviceRepository _patientDeviceRepository;
    private readonly ITimezoneTimelineService _timezoneTimelineService;
    private readonly ILogger<PatientDeviceStamper> _logger;

    /// <summary>
    /// Connector data sources that identify a specific device manufacturer, keyed by the
    /// manufacturer name as it appears in <see cref="DeviceCatalog"/> / <see cref="PatientDevice.Manufacturer"/>.
    /// Aggregators (Glooko, Nightscout, Tidepool) and uploader apps are intentionally absent —
    /// they carry data from any manufacturer.
    /// </summary>
    private static readonly Dictionary<string, string[]> ManufacturerConnectorSources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dexcom"] = [DataSources.DexcomConnector],
        ["abbott"] = [DataSources.LibreConnector],
        ["medtronic"] = [DataSources.MiniMedConnector, DataSources.CareLinkConnector],
        ["tandem"] = [DataSources.TConnectSyncConnector],
        ["ypsomed"] = [DataSources.MyLifeConnector],
        ["senseonics"] = [DataSources.EversenseConnector],
    };

    private static readonly HashSet<string> ManufacturerSpecificSources = new(
        ManufacturerConnectorSources.Values.SelectMany(s => s),
        StringComparer.OrdinalIgnoreCase);

    public PatientDeviceStamper(
        IPatientDeviceRepository patientDeviceRepository,
        ITimezoneTimelineService timezoneTimelineService,
        ILogger<PatientDeviceStamper> logger)
    {
        _patientDeviceRepository = patientDeviceRepository ?? throw new ArgumentNullException(nameof(patientDeviceRepository));
        _timezoneTimelineService = timezoneTimelineService ?? throw new ArgumentNullException(nameof(timezoneTimelineService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StampAsync(
        IReadOnlyList<IDeviceAttributed> records,
        IReadOnlyList<DeviceCategory> categories,
        string? batchSource,
        CancellationToken ct = default)
    {
        try
        {
            await StampCoreAsync(records, categories, batchSource, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve PatientDeviceId for batch from {Source}", batchSource);
        }
    }

    private async Task StampCoreAsync(
        IReadOnlyList<IDeviceAttributed> records,
        IReadOnlyList<DeviceCategory> categories,
        string? batchSource,
        CancellationToken ct)
    {
        var unstamped = records.Where(r => !r.PatientDeviceId.HasValue).ToList();
        if (unstamped.Count == 0) return;

        // Device usage windows are user-entered local dates, so records match on the wearer's local
        // day. Pad the candidate fetch by a day in each direction: a record's local date can differ
        // from its UTC date by up to a day either way.
        var min = unstamped.Min(r => r.Timestamp).AddDays(-1);
        var max = unstamped.Max(r => r.Timestamp).AddDays(1);
        var candidates = (await _patientDeviceRepository.GetByDateRangeAsync(min, max, ct))
            .Where(d => categories.Contains(d.DeviceCategory))
            .ToList();
        if (candidates.Count == 0) return;

        // Records missing a UtcOffset fall back to the tenant's timezone timeline; an empty
        // timeline resolves to UTC unchanged.
        TimezoneTimeline? timeline = null;
        if (unstamped.Any(r => r.UtcOffset is null))
            timeline = await _timezoneTimelineService.GetResolverAsync(fallbackOffsetHours: null, ct);

        var unresolved = 0;
        foreach (var record in unstamped)
        {
            var local = record.UtcOffset is { } offsetMinutes
                ? record.Timestamp.AddMinutes(offsetMinutes)
                : timeline!.ToLocal(record.Timestamp);
            var date = DateOnly.FromDateTime(local);
            var active = candidates
                .Where(d =>
                    (d.StartDate is null || d.StartDate <= date) &&
                    (d.EndDate is null || d.EndDate >= date))
                .ToList();
            if (active.Count == 0) continue;

            var match = MatchBySerial(record, active) ?? MatchBySource(record, active, batchSource);
            if (match is not null)
                record.PatientDeviceId = match.Id;
            else
                unresolved++;
        }

        if (unresolved > 0)
            _logger.LogDebug(
                "Left {Count} record(s) from {Source} unattributed: multiple {Categories} devices match and no serial disambiguates",
                unresolved, batchSource, string.Join("/", categories));
    }

    /// <summary>
    /// Serials shorter than this are too likely to occur incidentally inside device strings
    /// ("G6" in "xDrip-DexcomG6") to be trusted as a substring match.
    /// </summary>
    private const int MinSerialMatchLength = 5;

    /// <summary>
    /// Matches a candidate whose serial number appears in the record's device string. When several
    /// serials match (one a prefix of another), the longest wins; two distinct devices tying on the
    /// same matched serial is ambiguous and matches nothing.
    /// </summary>
    private static PatientDevice? MatchBySerial(IDeviceAttributed record, List<PatientDevice> active)
    {
        if (string.IsNullOrEmpty(record.Device)) return null;

        var matches = active
            .Where(d => d.SerialNumber is { Length: >= MinSerialMatchLength }
                && record.Device.Contains(d.SerialNumber, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.SerialNumber!.Length)
            .ToList();

        if (matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0];

        return matches[0].SerialNumber!.Length > matches[1].SerialNumber!.Length ? matches[0] : null;
    }

    /// <summary>
    /// Matches the single candidate compatible with the record's data source. Compatibility only
    /// constrains manufacturer-specific connector sources; any other source (aggregator, uploader
    /// app, null) is compatible with every candidate, so a lone active device still matches.
    /// </summary>
    private static PatientDevice? MatchBySource(IDeviceAttributed record, List<PatientDevice> active, string? batchSource)
    {
        var source = record.DataSource ?? batchSource;
        var compatible = active.Where(d => IsCompatible(d, source)).ToList();
        return compatible.Count == 1 ? compatible[0] : null;
    }

    private static bool IsCompatible(PatientDevice device, string? source)
    {
        if (string.IsNullOrEmpty(source) || !ManufacturerSpecificSources.Contains(source))
            return true;

        var manufacturer = ManufacturerOf(device);
        return manufacturer is not null
            && ManufacturerConnectorSources.TryGetValue(manufacturer, out var sources)
            && sources.Contains(source, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Catalog manufacturer when the device is catalog-linked, else its own Manufacturer field.</summary>
    private static string? ManufacturerOf(PatientDevice device)
        => device.CatalogId is not null
            ? DeviceCatalog.GetById(device.CatalogId)?.Manufacturer ?? device.Manufacturer
            : device.Manufacturer;
}
