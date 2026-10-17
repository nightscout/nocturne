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
/// Which rows make up one status, and which of them places it, is
/// <see cref="IDeviceStatusGroupRepository"/>'s rule; every list, count and history read here goes
/// through it.
/// </remarks>
/// <seealso cref="V4.DeviceStatusDecomposer"/>
/// <seealso cref="DeviceStatus"/>
public class DeviceStatusProjectionService
{
    private readonly IDeviceStatusGroupRepository _groupRepo;
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
        IDeviceStatusGroupRepository groupRepo,
        IApsSnapshotRepository apsRepo,
        IPumpSnapshotRepository pumpRepo,
        IUploaderSnapshotRepository uploaderRepo,
        IStateSpanRepository stateSpanRepo,
        IDeviceStatusExtrasRepository extrasRepo,
        ILogger<DeviceStatusProjectionService> logger)
    {
        _groupRepo = groupRepo;
        _apsRepo = apsRepo;
        _pumpRepo = pumpRepo;
        _uploaderRepo = uploaderRepo;
        _stateSpanRepo = stateSpanRepo;
        _extrasRepo = extrasRepo;
        _logger = logger;
    }

    /// <summary>
    /// Returns paginated <see cref="DeviceStatus"/> documents projected from V4 snapshots, newest first.
    /// </summary>
    /// <param name="count">Maximum number of results.</param>
    /// <param name="skip">Number of results to skip for pagination.</param>
    /// <param name="find">MongoDB-style query filter supporting <c>device</c> and <c>created_at</c> date range filters.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IEnumerable<DeviceStatus>> GetAsync(
        int count, int skip, string? find, CancellationToken ct)
    {
        var (device, from, to) = ParseFindQuery(find);
        var groups = await _groupRepo.GetAsync(from, to, device, count, skip, ct);
        return await ProjectAsync(groups, ct);
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
        DeviceStatusExtras? extras = null;

        if (Guid.TryParse(id, out var uuid))
        {
            aps = await _apsRepo.GetByIdAsync(uuid, ct);
            if (aps == null)
                pump = await _pumpRepo.GetByIdAsync(uuid, ct);
            if (aps == null && pump == null)
                uploader = await _uploaderRepo.GetByIdAsync(uuid, ct);
            if (aps == null && pump == null && uploader == null)
                extras = await _extrasRepo.GetByIdAsync(uuid, ct);
        }

        // Fallback to legacy ID
        if (aps == null && pump == null && uploader == null && extras == null)
        {
            aps = await _apsRepo.GetByLegacyIdAsync(id, ct);
            if (aps == null)
                pump = await _pumpRepo.GetByLegacyIdAsync(id, ct);
            if (aps == null && pump == null)
                uploader = await _uploaderRepo.GetByLegacyIdAsync(id, ct);
        }

        // Fallback to a 24-hex ObjectId derived from the record's UUID (resolved via prefix range).
        if (aps == null && pump == null && uploader == null && extras == null
            && MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
        {
            aps = await _apsRepo.GetByGuidRangeAsync(low, high, ct);
            if (aps == null)
                pump = await _pumpRepo.GetByGuidRangeAsync(low, high, ct);
            if (aps == null && pump == null)
                uploader = await _uploaderRepo.GetByGuidRangeAsync(low, high, ct);
            if (aps == null && pump == null && uploader == null)
                extras = await _extrasRepo.GetByGuidRangeAsync(low, high, ct);
        }

        if (aps == null && pump == null && uploader == null && extras == null)
            return null;

        // Load correlated records if we found an anchor
        var correlationId = aps?.CorrelationId ?? pump?.CorrelationId ?? uploader?.CorrelationId ?? extras?.CorrelationId;

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

            extras ??= (await _extrasRepo.GetByCorrelationIdsAsync(correlationIds, ct)).FirstOrDefault();
            var timestamp = aps?.Timestamp ?? pump?.Timestamp ?? uploader?.Timestamp ?? extras?.Timestamp ?? DateTime.UtcNow;
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
    /// Returns <see cref="DeviceStatus"/> documents modified since the given Unix millisecond
    /// threshold. Used for AAPS incremental sync. A deleted status projects with <c>isValid: false</c>.
    /// </summary>
    /// <param name="lastModified">Unix millisecond timestamp threshold.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IEnumerable<DeviceStatus>> GetModifiedSinceAsync(
        long lastModified, int limit, CancellationToken ct)
    {
        var page = await _groupRepo.GetModifiedSinceAsync(lastModified, limit, ct);
        var statuses = await ProjectAsync(page.Select(r => r.Record).ToList(), ct);

        for (var i = 0; i < page.Count; i++)
        {
            if (page[i].Deleted)
                statuses[i].IsValid = false;
        }

        return statuses;
    }

    /// <summary>
    /// Returns the number of <see cref="DeviceStatus"/> documents <see cref="GetAsync"/> projects for
    /// <paramref name="find"/> without a page limit.
    /// </summary>
    /// <param name="find">MongoDB-style query filter (same format as <see cref="GetAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<long> CountAsync(string? find, CancellationToken ct)
    {
        var (device, from, to) = ParseFindQuery(find);
        return await _groupRepo.CountAsync(from, to, device, ct);
    }

    /// <summary>
    /// Projects each group, giving a status with an APS snapshot the override active at its time.
    /// <c>srvModified</c> is the group's <see cref="DeviceStatusGroup.ModifiedAt"/>, the stamp the
    /// history pages on, so a client's cursor never passes a status it has not seen.
    /// </summary>
    private async Task<List<DeviceStatus>> ProjectAsync(IReadOnlyList<DeviceStatusGroup> groups, CancellationToken ct)
    {
        var overrides = await LoadOverridesForSnapshots(groups.Select(g => g.Aps).OfType<ApsSnapshot>().ToList(), ct);

        return groups
            .Select(g =>
            {
                var status = ProjectFromSnapshots(
                    g.Aps,
                    g.Pump,
                    g.Uploader,
                    g.Aps is { } aps ? FindOverrideForTimestamp(overrides, aps.Timestamp) : null,
                    g.Extras,
                    _logger);
                status.SrvModified = new DateTimeOffset(g.ModifiedAt, TimeSpan.Zero).ToUnixTimeMilliseconds();
                return status;
            })
            .ToList();
    }

    #region Projection Logic

    /// <summary>
    /// Projects V4 snapshots into a legacy <see cref="DeviceStatus"/> document, identified by the first
    /// of <paramref name="aps"/>, <paramref name="pump"/>, <paramref name="uploader"/> and
    /// <paramref name="extras"/> present. At least one must be.
    /// </summary>
    internal static DeviceStatus ProjectFromSnapshots(
        ApsSnapshot? aps,
        PumpSnapshot? pump,
        UploaderSnapshot? uploader,
        StateSpan? overrideSpan,
        DeviceStatusExtras? extras,
        ILogger? logger = null)
    {
        var anchor = (IV4Record?)aps ?? (IV4Record?)pump ?? uploader;
        var ds = anchor is not null
            ? NewStatus(anchor.LegacyId ?? anchor.Id.ToString(), anchor.Timestamp, anchor.CreatedAt,
                anchor.ModifiedAt, anchor.UtcOffset, anchor.Device)
            : extras is not null
                ? NewStatus(extras.Id.ToString(), extras.Timestamp, extras.CreatedAt, extras.ModifiedAt,
                    utcOffset: null, device: null)
                : throw new ArgumentException("At least one snapshot must be non-null");

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

    private static DeviceStatus NewStatus(
        string id, DateTime timestamp, DateTime createdAt, DateTime modifiedAt, int? utcOffset, string? device)
    {
        var mills = new DateTimeOffset(timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return new DeviceStatus
        {
            Id = id,
            Mills = mills,
            Date = mills,
            SrvModified = new DateTimeOffset(modifiedAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            SrvCreated = new DateTimeOffset(createdAt, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            // Nightscout clients place a status on the timeline by created_at, so it is the event
            // time; the server clock is srvCreated.
            CreatedAt = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            UtcOffset = utcOffset,
            Device = device ?? string.Empty,
        };
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
    /// Honours <c>device</c> as a string or <c>$eq</c>, and <c>created_at</c> with <c>$gte/$gt/$lte/$lt</c>,
    /// plus <c>$eq</c> or a bare instant in JSON.
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
                if (field.Name == "device")
                {
                    var operators = field.Value.ValueKind == JsonValueKind.Object
                        ? field.Value.EnumerateObject().Select(prop => (prop.Name, prop.Value)).ToList()
                        : [("$eq", field.Value)];

                    if (operators.Count == 0)
                        unhonoured = true;

                    foreach (var (op, operand) in operators)
                    {
                        if (op == "$eq" && operand.ValueKind == JsonValueKind.String)
                            device = operand.GetString();
                        else
                            unhonoured = true;
                    }
                }
                else if (field.Name == "created_at")
                {
                    var operators = field.Value.ValueKind == JsonValueKind.Object
                        ? field.Value.EnumerateObject().Select(prop => (prop.Name, prop.Value)).ToList()
                        : [("$eq", field.Value)];

                    foreach (var (op, operand) in operators)
                    {
                        if (!TryParseDateTime(operand, out var dt)
                            || !TryApplyCreatedAtCondition(op, dt, ref from, ref to))
                            unhonoured = true;
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

            var createdAtOp = key.ToLowerInvariant() switch
            {
                "find[created_at][$gte]" => "$gte",
                "find[created_at][$gt]" => "$gt",
                "find[created_at][$lte]" => "$lte",
                "find[created_at][$lt]" => "$lt",
                _ => null,
            };

            if (key.Equals("find[device]", StringComparison.OrdinalIgnoreCase))
            {
                device = value;
            }
            else if (createdAtOp is not null)
            {
                if (!TryParseDateTime(value, out var dt)
                    || !TryApplyCreatedAtCondition(createdAtOp, dt, ref from, ref to))
                    unhonoured = true;
            }
            else
            {
                unhonoured |= isFind;
            }
        }

        return (device, from, to, unhonoured);
    }

    /// <summary>
    /// Intersects one <c>created_at</c> condition into the window. The repositories are inclusive at
    /// both ends, so a strict bound tightens by 1ms, as <c>FindQuery</c> does for its time bounds. A
    /// strict bound with no representable instant beyond it returns false.
    /// </summary>
    private static bool TryApplyCreatedAtCondition(
        string op, DateTime instant, ref DateTime? from, ref DateTime? to)
    {
        switch (op)
        {
            case "$gt" when instant > DateTime.MaxValue.AddMilliseconds(-1):
            case "$lt" when instant < DateTime.MinValue.AddMilliseconds(1):
                return false;
            case "$gt":
                instant = instant.AddMilliseconds(1);
                goto case "$gte";
            case "$gte":
                from = from > instant ? from : instant;
                return true;
            case "$lt":
                instant = instant.AddMilliseconds(-1);
                goto case "$lte";
            case "$lte":
                to = to < instant ? to : instant;
                return true;
            case "$eq":
                from = from > instant ? from : instant;
                to = to < instant ? to : instant;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseDateTime(JsonElement value, out DateTime result)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            result = default;
            return value.TryGetInt64(out var mills) && TryFromMills(mills, out result);
        }

        if (value.ValueKind == JsonValueKind.String)
            return TryParseDateTime(value.GetString()!, out result);

        result = default;
        return false;
    }

    private static bool TryParseDateTime(string value, out DateTime result)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out result))
        {
            return true;
        }

        result = default;
        return long.TryParse(value, out var mills) && TryFromMills(mills, out result);
    }

    private static bool TryFromMills(long mills, out DateTime result)
    {
        if (mills < DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
            || mills > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            result = default;
            return false;
        }

        result = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;
        return true;
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
