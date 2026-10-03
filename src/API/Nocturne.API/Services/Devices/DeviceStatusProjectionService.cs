using System.Globalization;
using System.Text.Json;
using System.Web;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// Reassembles legacy <see cref="DeviceStatus"/> documents from the V4 snapshot tables
/// (APS, pump, uploader, state-span overrides, and extras). This is the read-side complement
/// to <see cref="V4.DeviceStatusDecomposer"/>, which breaks them apart on write.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ApsSnapshot"/> is the primary query anchor because most device status records
/// include APS data. Orphan <see cref="PumpSnapshot"/> and <see cref="UploaderSnapshot"/>
/// records (those without a matching APS correlation) cover the xDrip+ case where only
/// pump/uploader data is submitted.
/// </para>
/// <para>
/// Correlated records are batch-loaded by <see cref="IV4Record.CorrelationId"/> and merged
/// into a single <see cref="DeviceStatus"/> per correlation group.
/// </para>
/// </remarks>
/// <seealso cref="V4.DeviceStatusDecomposer"/>
/// <seealso cref="DeviceStatus"/>
public class DeviceStatusProjectionService
{
    private readonly IApsSnapshotRepository _apsRepo;
    private readonly IPumpSnapshotRepository _pumpRepo;
    private readonly IUploaderSnapshotRepository _uploaderRepo;
    private readonly IStateSpanRepository _stateSpanRepo;
    private readonly IDeviceStatusExtrasRepository _extrasRepo;
    private readonly ILogger<DeviceStatusProjectionService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DeviceStatusProjectionService(
        IApsSnapshotRepository apsRepo,
        IPumpSnapshotRepository pumpRepo,
        IUploaderSnapshotRepository uploaderRepo,
        IStateSpanRepository stateSpanRepo,
        IDeviceStatusExtrasRepository extrasRepo,
        ILogger<DeviceStatusProjectionService> logger)
    {
        _apsRepo = apsRepo;
        _pumpRepo = pumpRepo;
        _uploaderRepo = uploaderRepo;
        _stateSpanRepo = stateSpanRepo;
        _extrasRepo = extrasRepo;
        _logger = logger;
    }

    /// <summary>
    /// Returns paginated <see cref="DeviceStatus"/> documents projected from V4 snapshots.
    /// APS snapshots are the primary anchor; orphan pump/uploader snapshots are included
    /// for completeness (xDrip+ case).
    /// </summary>
    /// <param name="count">Maximum number of results.</param>
    /// <param name="skip">Number of results to skip for pagination.</param>
    /// <param name="find">MongoDB-style query filter supporting <c>device</c> and <c>created_at</c> date range filters.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IEnumerable<DeviceStatus>> GetAsync(
        int count, int skip, string? find, CancellationToken ct)
    {
        var (device, from, to) = ParseFindQuery(find);

        // 1. Query APS snapshots as primary anchor (newest-first with pagination)
        var apsSnapshots = (await _apsRepo.GetAsync(
            from: from, to: to, device: device, source: null,
            limit: count, offset: skip, descending: true, ct: ct)).ToList();

        // 2. Collect correlation IDs from APS results
        var apsCorrelationIds = apsSnapshots
            .Where(a => a.CorrelationId.HasValue)
            .Select(a => a.CorrelationId!.Value)
            .ToHashSet();

        // 3. Query orphan pump snapshots (those whose CorrelationId is not in the APS set).
        //    Only include orphans on the first page (skip == 0) — on subsequent pages they
        //    would have already appeared, and mixing two offset spaces breaks pagination.
        var orphanPumps = new List<PumpSnapshot>();
        if (skip == 0)
        {
            var allPumpSnapshots = (await _pumpRepo.GetAsync(
                from: from, to: to, device: device, source: null,
                limit: count, offset: 0, descending: true, ct: ct)).ToList();

            orphanPumps = allPumpSnapshots
                .Where(p => !p.CorrelationId.HasValue || !apsCorrelationIds.Contains(p.CorrelationId.Value))
                .ToList();
        }

        // 4. Batch-load correlated records
        var allCorrelationIds = apsCorrelationIds.ToList();

        var correlatedPumps = allCorrelationIds.Count > 0
            ? (await _pumpRepo.GetByCorrelationIdsAsync(allCorrelationIds, ct)).ToList()
            : new List<PumpSnapshot>();

        var correlatedUploaders = allCorrelationIds.Count > 0
            ? (await _uploaderRepo.GetByCorrelationIdsAsync(allCorrelationIds, ct)).ToList()
            : new List<UploaderSnapshot>();

        var extras = allCorrelationIds.Count > 0
            ? (await _extrasRepo.GetByCorrelationIdsAsync(allCorrelationIds, ct)).ToList()
            : new List<DeviceStatusExtras>();

        // Load override state spans that overlap with the time range of results
        var overrides = await LoadOverridesForSnapshots(apsSnapshots, ct);

        // Build lookup dictionaries by CorrelationId
        var pumpByCorrelation = correlatedPumps
            .Where(p => p.CorrelationId.HasValue)
            .ToLookup(p => p.CorrelationId!.Value);
        var uploaderByCorrelation = correlatedUploaders
            .Where(u => u.CorrelationId.HasValue)
            .ToLookup(u => u.CorrelationId!.Value);
        var extrasByCorrelation = extras
            .ToLookup(e => e.CorrelationId);

        // 5. Project each APS snapshot into a DeviceStatus
        var results = new List<DeviceStatus>();

        foreach (var aps in apsSnapshots)
        {
            var correlationId = aps.CorrelationId;
            var pump = correlationId.HasValue ? pumpByCorrelation[correlationId.Value].FirstOrDefault() : null;
            var uploader = correlationId.HasValue ? uploaderByCorrelation[correlationId.Value].FirstOrDefault() : null;
            var extra = correlationId.HasValue ? extrasByCorrelation[correlationId.Value].FirstOrDefault() : null;
            var overrideSpan = FindOverrideForTimestamp(overrides, aps.Timestamp);

            results.Add(ProjectFromSnapshots(aps, pump, uploader, overrideSpan, extra, _logger));
        }

        // 6. Project orphan pump snapshots (no APS correlation)
        foreach (var pump in orphanPumps)
        {
            results.Add(ProjectFromSnapshots(null, pump, null, null, null, _logger));
        }

        return results.OrderByDescending(d => d.Mills).Take(count);
    }

    /// <summary>
    /// Returns a single <see cref="DeviceStatus"/> by ID.
    /// Tries UUID parse for primary key lookup, then falls back to legacy ID lookup.
    /// </summary>
    /// <param name="id">UUID string or legacy MongoDB ObjectId.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DeviceStatus?> GetByIdAsync(string id, CancellationToken ct)
    {
        ApsSnapshot? aps = null;
        PumpSnapshot? pump = null;
        UploaderSnapshot? uploader = null;

        if (Guid.TryParse(id, out var uuid))
        {
            aps = await _apsRepo.GetByIdAsync(uuid, ct);
            if (aps == null)
                pump = await _pumpRepo.GetByIdAsync(uuid, ct);
            if (aps == null && pump == null)
                uploader = await _uploaderRepo.GetByIdAsync(uuid, ct);
        }

        // Fallback to legacy ID
        if (aps == null && pump == null && uploader == null)
        {
            aps = await _apsRepo.GetByLegacyIdAsync(id, ct);
            if (aps == null)
                pump = await _pumpRepo.GetByLegacyIdAsync(id, ct);
            if (aps == null && pump == null)
                uploader = await _uploaderRepo.GetByLegacyIdAsync(id, ct);
        }

        // Fallback to a 24-hex ObjectId derived from the record's UUID (resolved via prefix range).
        if (aps == null && pump == null && uploader == null
            && MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
        {
            aps = await _apsRepo.GetByGuidRangeAsync(low, high, ct);
            if (aps == null)
                pump = await _pumpRepo.GetByGuidRangeAsync(low, high, ct);
            if (aps == null && pump == null)
                uploader = await _uploaderRepo.GetByGuidRangeAsync(low, high, ct);
        }

        if (aps == null && pump == null && uploader == null)
            return null;

        // Load correlated records if we found an anchor
        var correlationId = aps?.CorrelationId ?? pump?.CorrelationId ?? uploader?.CorrelationId;

        if (correlationId.HasValue)
        {
            var correlationIds = new[] { correlationId.Value };

            if (aps == null)
            {
                var apsRecords = await _apsRepo.GetByCorrelationIdsAsync(correlationIds, ct);
                aps = apsRecords.FirstOrDefault();
            }
            if (pump == null)
            {
                var pumpRecords = await _pumpRepo.GetByCorrelationIdsAsync(correlationIds, ct);
                pump = pumpRecords.FirstOrDefault();
            }
            if (uploader == null)
            {
                var uploaderRecords = await _uploaderRepo.GetByCorrelationIdsAsync(correlationIds, ct);
                uploader = uploaderRecords.FirstOrDefault();
            }

            var extras = (await _extrasRepo.GetByCorrelationIdsAsync(correlationIds, ct)).FirstOrDefault();
            var timestamp = aps?.Timestamp ?? pump?.Timestamp ?? uploader?.Timestamp ?? DateTime.UtcNow;
            var overrides = await _stateSpanRepo.GetByCategory(
                StateSpanCategory.Override,
                from: timestamp.AddMinutes(-1),
                to: timestamp.AddMinutes(1),
                cancellationToken: ct);
            var overrideSpan = FindOverrideForTimestamp(overrides.ToList(), timestamp);

            return ProjectFromSnapshots(aps, pump, uploader, overrideSpan, extras, _logger);
        }

        return ProjectFromSnapshots(aps, pump, uploader, null, null, _logger);
    }

    /// <summary>
    /// Returns <see cref="DeviceStatus"/> documents projected from APS snapshots
    /// modified since the given Unix millisecond threshold. Used for AAPS incremental sync.
    /// A deleted snapshot projects with <c>isValid: false</c>.
    /// </summary>
    /// <param name="lastModified">Unix millisecond timestamp threshold.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IEnumerable<DeviceStatus>> GetModifiedSinceAsync(
        long lastModified, int limit, CancellationToken ct)
    {
        var page = await _apsRepo.GetModifiedSinceAsync(lastModified, limit, ct);

        if (page.Count == 0)
            return Enumerable.Empty<DeviceStatus>();

        var apsSnapshots = page.Select(r => r.Record).ToList();

        var correlationIds = apsSnapshots
            .Where(a => a.CorrelationId.HasValue)
            .Select(a => a.CorrelationId!.Value)
            .Distinct()
            .ToList();

        var pumps = correlationIds.Count > 0
            ? (await _pumpRepo.GetByCorrelationIdsAsync(correlationIds, ct)).ToList()
            : new List<PumpSnapshot>();
        var uploaders = correlationIds.Count > 0
            ? (await _uploaderRepo.GetByCorrelationIdsAsync(correlationIds, ct)).ToList()
            : new List<UploaderSnapshot>();
        var extras = correlationIds.Count > 0
            ? (await _extrasRepo.GetByCorrelationIdsAsync(correlationIds, ct)).ToList()
            : new List<DeviceStatusExtras>();

        var pumpByCorrelation = pumps.Where(p => p.CorrelationId.HasValue).ToLookup(p => p.CorrelationId!.Value);
        var uploaderByCorrelation = uploaders.Where(u => u.CorrelationId.HasValue).ToLookup(u => u.CorrelationId!.Value);
        var extrasByCorrelation = extras.ToLookup(e => e.CorrelationId);

        // Load override state spans the same way as GetAsync/GetByIdAsync
        var overrides = await LoadOverridesForSnapshots(apsSnapshots, ct);

        return page.Select(row =>
        {
            var aps = row.Record;
            var cid = aps.CorrelationId;
            var pump = cid.HasValue ? pumpByCorrelation[cid.Value].FirstOrDefault() : null;
            var uploader = cid.HasValue ? uploaderByCorrelation[cid.Value].FirstOrDefault() : null;
            var extra = cid.HasValue ? extrasByCorrelation[cid.Value].FirstOrDefault() : null;
            var overrideSpan = FindOverrideForTimestamp(overrides, aps.Timestamp);
            var status = ProjectFromSnapshots(aps, pump, uploader, overrideSpan, extra, _logger);
            if (row.Deleted)
                status.IsValid = false;
            return status;
        });
    }

    /// <summary>
    /// Returns the number of <see cref="DeviceStatus"/> documents <see cref="GetAsync"/> projects for
    /// <paramref name="find"/> without a page limit: every APS snapshot plus every pump snapshot
    /// no APS snapshot in the same window and device correlates with.
    /// </summary>
    /// <param name="find">MongoDB-style query filter (same format as <see cref="GetAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<long> CountAsync(string? find, CancellationToken ct)
    {
        var (device, from, to) = ParseFindQuery(find);

        var apsCount = await _apsRepo.CountAsync(from, to, device, ct);
        var orphanPumpCount = await _pumpRepo.CountUncorrelatedAsync(from, to, device, ct);

        return apsCount + orphanPumpCount;
    }

    #region Projection Logic

    /// <summary>
    /// Projects V4 snapshots into a legacy <see cref="DeviceStatus"/> document.
    /// At least one of <paramref name="aps"/> or <paramref name="pump"/> must be non-null.
    /// </summary>
    internal static DeviceStatus ProjectFromSnapshots(
        ApsSnapshot? aps,
        PumpSnapshot? pump,
        UploaderSnapshot? uploader,
        StateSpan? overrideSpan,
        DeviceStatusExtras? extras,
        ILogger? logger = null)
    {
        var anchor = (IV4Record?)aps ?? (IV4Record?)pump ?? (IV4Record?)uploader
            ?? throw new ArgumentException("At least one snapshot must be non-null");

        var ds = new DeviceStatus
        {
            Id = anchor.LegacyId ?? anchor.Id.ToString(),
            Mills = anchor.Mills,
            Date = anchor.Mills,
            SrvModified = new DateTimeOffset(anchor.ModifiedAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            SrvCreated = new DateTimeOffset(anchor.CreatedAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            // Nightscout clients place a status on the timeline by created_at, so it is the event
            // time; the server clock is srvCreated.
            CreatedAt = anchor.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            UtcOffset = anchor.UtcOffset,
            Device = anchor.Device ?? string.Empty,
        };

        // Project APS data
        if (aps != null)
        {
            if (aps.AidAlgorithm == AidAlgorithm.Loop)
            {
                ProjectLoopData(ds, aps, logger);
            }
            else
            {
                ProjectOpenApsData(ds, aps, logger);
            }
        }

        // Project pump data
        if (pump != null)
        {
            ProjectPumpData(ds, pump);
        }

        // Project uploader data
        if (uploader != null)
        {
            ProjectUploaderData(ds, uploader);
        }

        // Project override data
        if (overrideSpan != null)
        {
            ProjectOverrideData(ds, overrideSpan);
        }

        // Splat extras onto the document
        if (extras?.Extras is { Count: > 0 })
        {
            SplatExtras(ds, extras.Extras, logger);
        }

        return ds;
    }

    private static void ProjectLoopData(DeviceStatus ds, ApsSnapshot aps, ILogger? logger = null)
    {
        if (!string.IsNullOrEmpty(aps.LoopJson))
        {
            ds.Loop = JsonSerializer.Deserialize<LoopStatus>(aps.LoopJson, JsonOptions);
        }
        else
        {
            // LoopJson should always be present for Loop records; falling back to partial
            // reconstruction from structured fields indicates unexpected data.
            logger?.LogWarning(
                "APS snapshot {Id} has AidAlgorithm=Loop but no LoopJson; reconstructing from structured fields",
                aps.Id);

            ds.Loop = new LoopStatus
            {
                Iob = aps.Iob.HasValue ? new LoopIob { Iob = aps.Iob, BasalIob = aps.BasalIob } : null,
                Cob = aps.Cob.HasValue ? new LoopCob { Cob = aps.Cob } : null,
                RecommendedBolus = aps.RecommendedBolus,
            };
        }
    }

    private static void ProjectOpenApsData(DeviceStatus ds, ApsSnapshot aps, ILogger? logger = null)
    {
        ds.OpenAps = new OpenApsStatus
        {
            Suggested = DeserializeOrNull<OpenApsSuggested>(aps.SuggestedJson, logger),
            Enacted = DeserializeOrNull<OpenApsEnacted>(aps.EnactedJson, logger),
            Version = aps.AidVersion,
        };

        // Reconstruct IOB sub-object from structured fields
        if (aps.Iob.HasValue || aps.BasalIob.HasValue || aps.BolusIob.HasValue)
        {
            ds.OpenAps.Iob = new OpenApsIobData
            {
                Iob = aps.Iob,
                BasalIob = aps.BasalIob,
                BolusIob = aps.BolusIob,
            };
        }

        // COB on the OpenAps status level
        ds.OpenAps.Cob = aps.Cob;
    }

    private static void ProjectPumpData(DeviceStatus ds, PumpSnapshot pump)
    {
        ds.Pump = new PumpStatus
        {
            Manufacturer = pump.Manufacturer,
            Model = pump.Model,
            Reservoir = pump.Reservoir,
            ReservoirDisplayOverride = pump.ReservoirDisplay,
            Clock = pump.Clock,
            Battery = (pump.BatteryPercent.HasValue || pump.BatteryVoltage.HasValue)
                ? new PumpBattery { Percent = pump.BatteryPercent, Voltage = pump.BatteryVoltage }
                : null,
            Status = (pump.PumpStatus != null || pump.Bolusing.HasValue || pump.Suspended.HasValue)
                ? new PumpStatusDetails { Status = pump.PumpStatus, Bolusing = pump.Bolusing, Suspended = pump.Suspended }
                : null,
            Iob = (pump.Iob.HasValue || pump.BolusIob.HasValue)
                ? new PumpIob { Iob = pump.Iob, BolusIob = pump.BolusIob }
                : null,
            Extended = pump.AdditionalProperties?.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value ?? (object)string.Empty),
        };
    }

    private static void ProjectUploaderData(DeviceStatus ds, UploaderSnapshot uploaderSnapshot)
    {
        ds.Uploader = new UploaderStatus
        {
            Battery = uploaderSnapshot.Battery,
            BatteryVoltage = uploaderSnapshot.BatteryVoltage,
            Temperature = uploaderSnapshot.Temperature,
            Name = uploaderSnapshot.Name,
            Type = uploaderSnapshot.Type,
            IsCharging = uploaderSnapshot.IsCharging,
        };

        ds.IsCharging = uploaderSnapshot.IsCharging;
    }

    private static void ProjectOverrideData(DeviceStatus ds, StateSpan overrideSpan)
    {
        ds.Override = new OverrideStatus
        {
            Active = overrideSpan.IsActive,
            Timestamp = overrideSpan.StartTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };

        if (overrideSpan.Metadata != null)
        {
            if (overrideSpan.Metadata.TryGetValue("name", out var name))
                ds.Override.Name = name.ToString();

            if (overrideSpan.Metadata.TryGetValue("multiplier", out var multiplier) &&
                double.TryParse(multiplier.ToString(), out var multiplierValue))
                ds.Override.Multiplier = multiplierValue;

            if (overrideSpan.Metadata.TryGetValue("currentCorrectionRange.minValue", out var minVal) &&
                overrideSpan.Metadata.TryGetValue("currentCorrectionRange.maxValue", out var maxVal))
            {
                if (double.TryParse(minVal.ToString(), out var min) &&
                    double.TryParse(maxVal.ToString(), out var max))
                {
                    ds.Override.CurrentCorrectionRange = new CorrectionRange
                    {
                        MinValue = min,
                        MaxValue = max,
                    };
                }
            }
        }

        if (overrideSpan.EndTimestamp.HasValue)
        {
            ds.Override.Duration = (overrideSpan.EndTimestamp.Value - overrideSpan.StartTimestamp).TotalSeconds;
        }
    }

    private static void SplatExtras(DeviceStatus ds, Dictionary<string, object?> extras, ILogger? logger = null)
    {
        ds.ExtensionData ??= new Dictionary<string, JsonElement>();

        foreach (var (key, value) in extras)
        {
            if (value is null)
                continue;

            // Try to match known typed properties first
            switch (key)
            {
                case "xdripjs":
                    ds.XDripJs = DeserializeValue<XDripJsStatus>(value, logger);
                    break;
                case "radioAdapter":
                    ds.RadioAdapter = DeserializeValue<RadioAdapterStatus>(value, logger);
                    break;
                case "connect":
                    ds.Connect = value;
                    break;
                case "cgm":
                    ds.Cgm = DeserializeValue<CgmStatus>(value, logger);
                    break;
                case "meter":
                    ds.Meter = DeserializeValue<MeterStatus>(value, logger);
                    break;
                case "insulinPen":
                    ds.InsulinPen = DeserializeValue<InsulinPenStatus>(value, logger);
                    break;
                case "mmtune":
                    ds.MmTune = DeserializeValue<OpenApsMmTune>(value, logger);
                    break;
                // The record reports the server clock (see ProjectFromSnapshots); a client-supplied
                // value must neither override it nor re-emit as an extra.
                case "srvCreated":
                case "srvModified":
                    break;
                // An NS v3 uploader sends its own identifier and it is stored verbatim.
                // DeviceStatus has no member to absorb it, so re-emitting it would put a
                // client-supplied value where every reader takes the record's identity from —
                // including the id of its delete event (see StorageDeleteEvent).
                case "identifier":
                    break;
                default:
                    // Unknown keys go into ExtensionData
                    var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
                    var element = JsonSerializer.Deserialize<JsonElement>(jsonBytes, JsonOptions);
                    ds.ExtensionData[key] = element;
                    break;
            }
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Parses a MongoDB-style find query string into typed filter values.
    /// Supports two formats:
    /// <list type="bullet">
    /// <item>URL query string: <c>find[device]=openaps&amp;find[created_at][$gte]=2024-01-01</c></item>
    /// <item>JSON object: <c>{"device":"openaps","created_at":{"$gte":"2024-01-01"}}</c></item>
    /// </list>
    /// Only <c>device</c> (exact match) and <c>created_at</c> with <c>$gte/$gt/$lte/$lt</c> operators are supported.
    /// </summary>
    internal static (string? Device, DateTime? From, DateTime? To) ParseFindQuery(string? find)
    {
        var (device, from, to, _) = ParseFindQueryReportingUnhonoured(find);
        return (device, from, to);
    }

    /// <summary>
    /// The filters of <paramref name="find"/> when it names at least one and every <c>find</c> key
    /// in it is one <see cref="ParseFindQuery"/> honours, else null. A delete must refuse a find it
    /// cannot apply, since the lenient read would drop that key and select everything.
    /// </summary>
    internal static (string? Device, DateTime? From, DateTime? To)? ParseDeleteFind(string? find)
    {
        var (device, from, to, unhonoured) = ParseFindQueryReportingUnhonoured(find);
        return unhonoured || (device is null && from is null && to is null) ? null : (device, from, to);
    }

    private static (string? Device, DateTime? From, DateTime? To, bool Unhonoured) ParseFindQueryReportingUnhonoured(
        string? find)
    {
        if (string.IsNullOrWhiteSpace(find))
            return (null, null, null, false);

        // Try JSON format first
        if (find.TrimStart().StartsWith('{'))
        {
            return ParseFindQueryFromJson(find);
        }

        // URL query string format: find[device]=value&find[created_at][$gte]=date
        return ParseFindQueryFromQueryString(find);
    }

    private static (string? Device, DateTime? From, DateTime? To, bool Unhonoured) ParseFindQueryFromJson(string json)
    {
        string? device = null;
        DateTime? from = null;
        DateTime? to = null;
        var unhonoured = false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null, null, true);

            foreach (var field in root.EnumerateObject())
            {
                if (field.Name == "device" && field.Value.ValueKind == JsonValueKind.String)
                {
                    device = field.Value.GetString();
                }
                else if (field.Name == "created_at" && field.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in field.Value.EnumerateObject())
                    {
                        var dateStr = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                        if (dateStr == null || !TryParseDateTime(dateStr, out var dt))
                        {
                            unhonoured = true;
                            continue;
                        }

                        switch (prop.Name)
                        {
                            case "$gte":
                            case "$gt":
                                from = dt;
                                break;
                            case "$lte":
                            case "$lt":
                                to = dt;
                                break;
                            default:
                                unhonoured = true;
                                break;
                        }
                    }
                }
                else
                {
                    unhonoured = true;
                }
            }
        }
        catch (JsonException)
        {
            return (null, null, null, true);
        }

        return (device, from, to, unhonoured);
    }

    private static (string? Device, DateTime? From, DateTime? To, bool Unhonoured) ParseFindQueryFromQueryString(
        string queryString)
    {
        string? device = null;
        DateTime? from = null;
        DateTime? to = null;
        var unhonoured = false;

        var parsed = HttpUtility.ParseQueryString(queryString);

        foreach (string? key in parsed)
        {
            if (key == null) continue;
            var isFind = key.StartsWith("find", StringComparison.OrdinalIgnoreCase);
            var value = parsed[key];
            if (string.IsNullOrEmpty(value))
            {
                unhonoured |= isFind;
                continue;
            }

            if (key.Equals("find[device]", StringComparison.OrdinalIgnoreCase))
            {
                device = value;
            }
            else if (key.Equals("find[created_at][$gte]", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("find[created_at][$gt]", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseDateTime(value, out var dt))
                    from = dt;
                else
                    unhonoured = true;
            }
            else if (key.Equals("find[created_at][$lte]", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("find[created_at][$lt]", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseDateTime(value, out var dt))
                    to = dt;
                else
                    unhonoured = true;
            }
            else
            {
                unhonoured |= isFind;
            }
        }

        return (device, from, to, unhonoured);
    }

    private static bool TryParseDateTime(string value, out DateTime result)
    {
        // Try ISO 8601 first, then fall back to general DateTime parsing
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out result))
        {
            return true;
        }

        // Try Unix milliseconds
        if (long.TryParse(value, out var mills))
        {
            result = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;
            return true;
        }

        result = default;
        return false;
    }

    private async Task<List<StateSpan>> LoadOverridesForSnapshots(
        List<ApsSnapshot> snapshots, CancellationToken ct)
    {
        if (snapshots.Count == 0)
            return new List<StateSpan>();

        var earliest = snapshots.Min(a => a.Timestamp);
        var latest = snapshots.Max(a => a.Timestamp);

        var overrides = await _stateSpanRepo.GetByCategory(
            StateSpanCategory.Override,
            from: earliest.AddMinutes(-1),
            to: latest.AddMinutes(1),
            cancellationToken: ct);

        return overrides.ToList();
    }

    private static StateSpan? FindOverrideForTimestamp(List<StateSpan> overrides, DateTime timestamp)
    {
        return overrides.FirstOrDefault(o =>
            o.StartTimestamp <= timestamp &&
            (!o.EndTimestamp.HasValue || o.EndTimestamp.Value >= timestamp));
    }

    private static T? DeserializeOrNull<T>(string? json, ILogger? logger = null) where T : class
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Failed to deserialize {Type} from JSON", typeof(T).Name);
            return null;
        }
    }

    private static T? DeserializeValue<T>(object value, ILogger? logger = null) where T : class
    {
        try
        {
            var json = value is JsonElement element
                ? element.GetRawText()
                : JsonSerializer.Serialize(value, JsonOptions);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Failed to deserialize {Type} from value", typeof(T).Name);
            return null;
        }
    }

    #endregion
}
