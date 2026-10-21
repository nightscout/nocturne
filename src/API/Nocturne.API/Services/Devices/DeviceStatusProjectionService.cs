using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
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
    /// The time fields a projected status carries for its anchor's time, the one the store filters by.
    /// </summary>
    private static readonly IReadOnlySet<string> AnchorTimeFields =
        new[] { "created_at", "date", "mills" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Most statuses projected into memory for one find whose conditions only the projected document
    /// can answer: the merged-read budget <see cref="LegacyReadLimits.MaxMergedCount"/> documents.
    /// </summary>
    internal int MaxFilterFetch { get; set; } = LegacyReadLimits.MaxMergedCount;

    /// <summary>
    /// Most ids an <c>_id</c> find resolves one by one. Each lookup costs several queries, so a longer
    /// list reads through the budgeted scan instead.
    /// </summary>
    private const int MaxIdLookups = 100;

    /// <summary>
    /// Returns paginated <see cref="DeviceStatus"/> documents projected from V4 snapshots, newest first
    /// or, when <paramref name="descending"/> is <c>false</c>, the oldest first.
    /// </summary>
    /// <param name="count">Maximum number of results.</param>
    /// <param name="skip">Number of results to skip for pagination.</param>
    /// <param name="find">JSON find honouring <c>device</c> and <c>created_at</c> only (see <see cref="ParseFindQuery"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="descending">Newest first when <c>true</c>, else the oldest first.</param>
    /// <param name="sort">
    /// <see cref="ReadSortField.SrvModified"/> orders by modification stamp when the find is empty or
    /// only bounds <c>srvModified</c> from below, which is served by the modified-since pages; any
    /// other field orders by time.
    /// </param>
    public async Task<IEnumerable<DeviceStatus>> GetAsync(
        int count, int skip, string? find, CancellationToken ct,
        bool descending = true, ReadSortField sort = ReadSortField.Time)
    {
        if (ModifiedSearch.CursorFor(FindQuery.Parse(find), sort, descending) is { } after)
            return await GetModifiedAfterAsync(count, skip, after, sort, descending, ct);

        var (device, from, to) = ParseFindQuery(find);
        var groups = await _groupRepo.GetAsync(from, to, device, count, skip, descending, ct);
        return await ProjectAsync(groups, ct);
    }

    private async Task<IEnumerable<DeviceStatus>> GetModifiedAfterAsync(
        int count, int skip, long after, ReadSortField sort, bool descending, CancellationToken ct)
    {
        var live = await ModifiedSearch.CollectAsync(
            async (cursor, limit, token) =>
            {
                var page = (await GetModifiedSinceAsync(cursor, limit, token)).ToList();
                return new ModifiedSearch.Page<DeviceStatus>(page, page.Count == 0 ? null : page[^1].SrvModified, page.Count < limit);
            },
            status => status.IsValid == false,
            after,
            ModifiedSearch.ArrivesInOrder(sort, descending) ? (long)count + skip : null,
            ModifiedSearch.MaxRead,
            ct);

        Func<DeviceStatus, long> key = sort == ReadSortField.SrvModified
            ? status => status.SrvModified ?? status.Mills
            : status => status.Mills;

        return (descending ? live.OrderByDescending(key) : live.OrderBy(key)).Skip(skip).Take(count).ToList();
    }

    /// <summary>Parses a v1 find, in either wire form, for <see cref="GetMatchingAsync"/>.</summary>
    public static FindQuery ParseFind(string? find) =>
        FindQuery.Parse(find, bareRegexFields: null, AnchorTimeFields);

    /// <summary>
    /// A page of the statuses <paramref name="find"/> matches, newest first, evaluated as legacy
    /// Nightscout evaluates it. The device equality and time bounds filter the store; every
    /// condition is tested against the projected document.
    /// </summary>
    public async Task<IReadOnlyList<DeviceStatus>> GetMatchingAsync(
        FindQuery find, int count, int skip, CancellationToken ct)
    {
        if (!TryGetStoreWindow(find, out var from, out var to))
            return [];

        if (find.GetEqualityValues("_id") is { Count: <= MaxIdLookups } ids)
            return (await ResolveByIdAsync(find, ids, ct)).Skip(skip).Take(count).ToList();

        var device = StoreDevice(find);
        if (!HasResidual(find, device))
        {
            var page = await _groupRepo.GetAsync(from, to, device, count, skip, descending: true, ct);
            return (await ProjectAsync(page, ct)).Where(find.Matches).ToList();
        }

        var (matching, truncated) = await FindFilteredFetch.MatchAsync<DeviceStatus>(
            async (limit, offset) => await ProjectAsync(await _groupRepo.GetAsync(from, to, device, limit, offset, descending: true, ct), ct),
            find.Matches, status => status.Id, (long)count + skip, MaxFilterFetch);

        if (truncated)
            _logger.LogWarning(
                "Find-filtered device status query hit the {MaxFetch}-row window; older matches are not returned",
                MaxFilterFetch);

        if (find.HasAbandonedRegex)
            _logger.LogWarning("A find $regex ran out of its time budget; device status matches may be incomplete");

        return matching.Skip(skip).Take(count).ToList();
    }

    /// <summary>The number of statuses <see cref="GetMatchingAsync"/> pages through.</summary>
    public async Task<long> CountMatchingAsync(FindQuery find, CancellationToken ct)
    {
        if (!TryGetStoreWindow(find, out var from, out var to))
            return 0;

        var device = StoreDevice(find);
        if (find.GetEqualityValues("_id") is { Count: <= MaxIdLookups } || HasResidual(find, device))
            return (await GetMatchingAsync(find, MaxFilterFetch, 0, ct)).Count;

        return await _groupRepo.CountAsync(from, to, device, ct);
    }

    /// <summary>
    /// The statuses an <c>_id</c> find names, each resolved as <see cref="GetByIdAsync"/> resolves an
    /// id, newest first. A status is named by its stored id as well as by the ObjectId its wire form
    /// carries, so an uploader's own id finds its status even when it is not an ObjectId.
    /// </summary>
    private async Task<List<DeviceStatus>> ResolveByIdAsync(
        FindQuery find, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var found = new Dictionary<string, DeviceStatus>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (await GetByIdAsync(id, ct) is not { Id: { } storedId } status || found.ContainsKey(storedId))
                continue;

            if (storedId == id ? find.MatchesAs(status, "_id", id) : find.Matches(status))
                found[storedId] = status;
        }

        return found.Values.OrderByDescending(status => status.Mills).ToList();
    }

    /// <summary>
    /// The device equality the store applies. Only a condition named <c>device</c> exactly is one, as
    /// MongoDB field names are case-sensitive. An empty one is left to the projected document, where a
    /// status stored without a device reads as an empty one.
    /// </summary>
    private static string? StoreDevice(FindQuery find) =>
        find.GetEqualityValue("device", StringComparison.Ordinal) is { Length: > 0 } device ? device : null;

    private static bool HasResidual(FindQuery find, string? storeDevice) =>
        find.HasFieldFiltersExcept(storeDevice is null ? null : "device", StringComparison.Ordinal);

    /// <summary>
    /// The store's inclusive window for the find's bounds, or false when no instant falls in it. A
    /// bound is a whole millisecond and a stored time can carry more, so the upper end runs to the
    /// last microsecond, the store's precision, of its millisecond.
    /// </summary>
    private static bool TryGetStoreWindow(FindQuery find, out DateTime? from, out DateTime? to)
    {
        from = null;
        to = null;
        var earliest = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
        var latest = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
        if (find.FromMills > latest || find.ToMills < earliest || find.FromMills > find.ToMills)
            return false;

        if (find.FromMills > earliest)
            from = DateTimeOffset.FromUnixTimeMilliseconds(find.FromMills.Value).UtcDateTime;
        if (find.ToMills < latest)
            to = DateTimeOffset.FromUnixTimeMilliseconds(find.ToMills.Value).UtcDateTime
                .AddTicks(TimeSpan.TicksPerMillisecond - TimeSpan.TicksPerMicrosecond);
        return true;
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
            ds.Loop = ApsSnapshotCurves.RestoreLoop(
                JsonSerializer.Deserialize<LoopStatus>(aps.LoopJson, JsonOptions), aps);
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
            Suggested = DeserializeOrNull<OpenApsSuggested>(ApsSnapshotCurves.RestoreCommand(aps.SuggestedJson, aps), logger),
            Enacted = DeserializeOrNull<OpenApsEnacted>(ApsSnapshotCurves.RestoreCommand(aps.EnactedJson, aps), logger),
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
            // The span contains the status time. IsActive would say whether it has ended since.
            Active = true,
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
    /// Parses a JSON find, <c>{"device":"openaps","created_at":{"$gte":"2024-01-01"}}</c>, into the
    /// filters <see cref="GetAsync"/> applies. Honours <c>device</c> as a string or <c>$eq</c>, and
    /// <c>created_at</c> with <c>$gte/$gt/$lte/$lt/$eq</c> or a bare instant; drops everything else.
    /// </summary>
    internal static (string? Device, DateTime? From, DateTime? To) ParseFindQuery(string? find)
    {
        if (string.IsNullOrWhiteSpace(find) || !find.TrimStart().StartsWith('{'))
            return (null, null, null);

        string? device = null;
        DateTime? from = null;
        DateTime? to = null;

        try
        {
            using var doc = JsonDocument.Parse(find);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null, null);

            foreach (var field in root.EnumerateObject())
            {
                if (field.Name is not ("device" or "created_at"))
                    continue;

                var operators = field.Value.ValueKind == JsonValueKind.Object
                    ? field.Value.EnumerateObject().Select(prop => (prop.Name, prop.Value)).ToList()
                    : [("$eq", field.Value)];

                foreach (var (op, operand) in operators)
                {
                    if (field.Name == "device")
                    {
                        if (op == "$eq" && operand.ValueKind == JsonValueKind.String)
                            device = operand.GetString();
                    }
                    else if (TryParseDateTime(operand, out var dt))
                    {
                        TryApplyCreatedAtCondition(op, dt, ref from, ref to);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return (null, null, null);
        }

        return (device, from, to);
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
            (!o.EndTimestamp.HasValue || o.EndTimestamp.Value > timestamp));
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
