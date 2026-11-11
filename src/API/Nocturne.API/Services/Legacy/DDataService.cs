using System.Reflection;
using System.Text.Json;
using Nocturne.Core.Clinical.Schedules;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;

namespace Nocturne.API.Services.Legacy;

/// <summary>
/// Implements the Nightscout <c>/api/v1/ddata</c> response payload, which bundles recent entries,
/// treatments, profiles, device statuses, food, and activity data into a single response used by
/// legacy Nightscout clients and plugins.
/// </summary>
/// <seealso cref="IDDataService"/>
public class DDataService : IDDataService
{
    private const long DayMills = 24L * 60 * 60 * 1000;
    private const long DeviceStatusWindowMills = DayMills;
    private const long FrameEntriesWindowMills = 2 * DayMills;
    private const long FrameTreatmentsWindowMills = 5 * DayMills / 2;
    private const long LiveFrameWindowMills = 5L * 60 * 1000;

    private readonly IEntryService _entries;
    private readonly ITreatmentService _treatments;
    private readonly IProfileProjectionService _profiles;
    private readonly IDeviceStatusService _deviceStatuses;
    private readonly IFoodRepository _food;
    private readonly IActivityService _activities;
    private readonly IStoreSizeReader _storeSize;
    private readonly TimeProvider _time;
    private readonly ILogger<DDataService> _logger;

    private static readonly (string EventType, long WindowMills)[] FrameLatestTreatments =
    {
        ("Profile Switch", 31 * 12 * DayMills),
        ("Sensor Start", 62 * DayMills),
        ("Sensor Change", 62 * DayMills),
        ("Sensor Stop", 62 * DayMills),
        ("Site Change", 62 * DayMills),
        ("Insulin Change", 62 * DayMills),
        ("Pump Battery Change", 62 * DayMills),
    };

    // Device type fields that should be considered for recent device status
    private static readonly string[] DeviceTypeFields =
    {
        "uploader",
        "pump",
        "openaps",
        "loop",
        "xdripjs",
    };

    public DDataService(
        IEntryService entries,
        ITreatmentService treatments,
        IProfileProjectionService profiles,
        IDeviceStatusService deviceStatuses,
        IFoodRepository food,
        IActivityService activities,
        IStoreSizeReader storeSize,
        TimeProvider timeProvider,
        ILogger<DDataService> logger)
    {
        _time = timeProvider;
        _entries = entries;
        _treatments = treatments;
        _profiles = profiles;
        _deviceStatuses = deviceStatuses;
        _food = food;
        _activities = activities;
        _storeSize = storeSize;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<DData> GetDDataAsync(
        long timestamp,
        CancellationToken cancellationToken = default
    ) => GetDDataAsync(timestamp, DDataCollections.All, cancellationToken);

    /// <inheritdoc />
    public Task<DData> GetDDataAsync(
        long timestamp,
        DDataCollections collections,
        CancellationToken cancellationToken = default
    ) => LoadDDataAsync(new DData { LastUpdated = timestamp }, collections, cancellationToken);

    /// <inheritdoc />
    public async Task<DDataFrame> GetDDataFrameAsync(
        long timestamp,
        CancellationToken cancellationToken = default
    )
    {
        var isLive = Math.Abs(_time.GetUtcNow().ToUnixTimeMilliseconds() - timestamp) < LiveFrameWindowMills;
        var frame = await LoadDDataAsync(
            new DDataFrame
            {
                LastUpdated = timestamp,
                Page = isLive ? null : new DDataFramePage { After = timestamp },
            },
            DDataCollections.All,
            cancellationToken);
        frame.Sgvs = frame.Sgvs.OrderBy(e => e.Mills).ToList();
        return frame;
    }

    private async Task<T> LoadDDataAsync<T>(
        T ddata,
        DDataCollections collections,
        CancellationToken cancellationToken
    )
        where T : DData
    {
        var timestamp = ddata.LastUpdated;

        // Sequential: see INocturneDbContextFactory.
        if (collections.HasFlag(DDataCollections.Sgvs))
            await LoadSgvsAsync(ddata, timestamp, cancellationToken);
        if (collections.HasFlag(DDataCollections.Treatments))
            await LoadTreatmentsAsync(ddata, timestamp, cancellationToken);
        if (collections.HasFlag(DDataCollections.Mbgs))
            await LoadMbgsAsync(ddata, timestamp, cancellationToken);
        if (collections.HasFlag(DDataCollections.Cals))
            await LoadCalsAsync(ddata, timestamp, cancellationToken);
        if (collections.HasFlag(DDataCollections.Profiles))
            await LoadProfilesAsync(ddata, cancellationToken);
        if (collections.HasFlag(DDataCollections.DeviceStatus))
            await LoadDeviceStatusAsync(ddata, timestamp, cancellationToken);
        if (collections.HasFlag(DDataCollections.Food))
            await LoadFoodAsync(ddata, cancellationToken);
        if (collections.HasFlag(DDataCollections.Activity))
            await LoadActivityAsync(ddata, timestamp, cancellationToken);
        if (collections.HasFlag(DDataCollections.DbStats))
            await LoadDbStatsAsync(ddata, cancellationToken);

        // Process treatments to create filtered lists
        var processedTreatments = ProcessTreatments(ddata.Treatments, false);

        // Assign the filtered treatment lists to the main ddata object
        ddata.SiteChangeTreatments = processedTreatments.SiteChangeTreatments;
        ddata.InsulinChangeTreatments = processedTreatments.InsulinChangeTreatments;
        ddata.BatteryTreatments = processedTreatments.BatteryTreatments;
        ddata.SensorTreatments = processedTreatments.SensorTreatments;
        ddata.ComboBolusTreatments = processedTreatments.ComboBolusTreatments;
        ddata.ProfileTreatments = processedTreatments.ProfileTreatments;
        ddata.TempBasalTreatments = processedTreatments.TempBasalTreatments;
        ddata.TempTargetTreatments = processedTreatments.TempTargetTreatments;

        // Compute lastProfileFromSwitch: latest zero-duration Profile Switch before request time
        ddata.LastProfileFromSwitch = ddata.ProfileTreatments
            .Where(t => (!t.Duration.HasValue || t.Duration == 0) && t.Mills <= timestamp)
            .OrderByDescending(t => t.Mills)
            .Select(t => t.Profile)
            .FirstOrDefault();

        // Set the most recent calibration
        if (ddata.Cals.Count > 0)
        {
            ddata.Cal = ddata.Cals.OrderByDescending(c => c.Mills).FirstOrDefault();
        }

        return ddata;
    }

    /// <inheritdoc />
    public async Task<DData> GetCurrentDDataAsync(CancellationToken cancellationToken = default)
    {
        var currentTime = _time.GetUtcNow().ToUnixTimeMilliseconds();
        return await GetDDataAsync(currentTime, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DDataResponse> GetDDataWithRecentStatusesAsync(
        long timestamp,
        CancellationToken cancellationToken = default
    )
    {
        var ddata = await GetDDataAsync(timestamp, cancellationToken);

        return new DDataResponse
        {
            DeviceStatus = GetRecentDeviceStatus(ddata.DeviceStatus, timestamp),
            Sgvs = ddata.Sgvs,
            Cals = ddata.Cals,
            Profiles = ddata.Profiles,
            Mbgs = ddata.Mbgs,
            Food = ddata.Food,
            Treatments = ddata.Treatments,
            DbStats = ddata.DbStats,
        };
    }

    /// <inheritdoc />
    public DData ProcessTreatments(
        List<Treatment> treatments,
        bool preserveOriginalTreatments = false
    )
    {
        var ddata = new DData();

        // filter & prepare 'Site Change' events
        ddata.SiteChangeTreatments = treatments
            .Where(t => !string.IsNullOrEmpty(t.EventType) && t.EventType.Contains("Site Change"))
            .OrderBy(t => t.Mills)
            .ToList();

        // filter & prepare 'Insulin Change' events
        ddata.InsulinChangeTreatments = treatments
            .Where(t =>
                !string.IsNullOrEmpty(t.EventType) && t.EventType.Contains("Insulin Change")
            )
            .OrderBy(t => t.Mills)
            .ToList();

        // filter & prepare 'Pump Battery Change' events
        ddata.BatteryTreatments = treatments
            .Where(t =>
                !string.IsNullOrEmpty(t.EventType) && t.EventType.Contains("Pump Battery Change")
            )
            .OrderBy(t => t.Mills)
            .ToList();

        // filter & prepare 'Sensor' events
        ddata.SensorTreatments = treatments
            .Where(t => !string.IsNullOrEmpty(t.EventType) && t.EventType.Contains("Sensor"))
            .OrderBy(t => t.Mills)
            .ToList();

        // filter & prepare 'Combo Bolus' events
        ddata.ComboBolusTreatments = treatments
            .Where(t => !string.IsNullOrEmpty(t.EventType) && t.EventType == "Combo Bolus")
            .OrderBy(t => t.Mills)
            .ToList();

        // filter & prepare 'Profile Switch' events
        var profileTreatments = treatments
            .Where(t => !string.IsNullOrEmpty(t.EventType) && t.EventType == "Profile Switch")
            .OrderBy(t => t.Mills)
            .ToList();
        if (preserveOriginalTreatments)
        {
            profileTreatments = DeepClone(profileTreatments);
        }
        ddata.ProfileTreatments = ProcessDurations(profileTreatments, true);

        // filter & prepare temp basals
        var tempBasalTreatments = treatments
            .Where(t => !string.IsNullOrEmpty(t.EventType) && t.EventType.Contains("Temp Basal"))
            .ToList();
        if (preserveOriginalTreatments)
        {
            tempBasalTreatments = DeepClone(tempBasalTreatments);
        }
        ddata.TempBasalTreatments = ProcessDurations(tempBasalTreatments, false);

        // filter temp target
        var tempTargetTreatments = treatments
            .Where(t =>
                !string.IsNullOrEmpty(t.EventType) && t.EventType.Contains("Temporary Target")
            )
            .ToList();
        if (preserveOriginalTreatments)
        {
            tempTargetTreatments = DeepClone(tempTargetTreatments);
        }
        tempTargetTreatments = ConvertTempTargetUnits(tempTargetTreatments);
        ddata.TempTargetTreatments = ProcessDurations(tempTargetTreatments, false);

        // Compute lastProfileFromSwitch: latest zero-duration Profile Switch
        ddata.LastProfileFromSwitch = ddata.ProfileTreatments
            .Where(t => !t.Duration.HasValue || t.Duration == 0)
            .OrderByDescending(t => t.Mills)
            .Select(t => t.Profile)
            .FirstOrDefault();

        return ddata;
    }

    /// <inheritdoc />
    public List<DeviceStatus> GetRecentDeviceStatus(List<DeviceStatus> deviceStatuses, long time)
    {
        // Get device and types
        var deviceAndTypes = new List<(string Device, string Type)>();

        foreach (var status in deviceStatuses)
        {
            foreach (var field in DeviceTypeFields)
            {
                if (HasProperty(status, field))
                {
                    deviceAndTypes.Add((status.Device ?? "", field));
                }
            }
        }

        // Remove duplicates
        deviceAndTypes = deviceAndTypes
            .GroupBy(x => new { x.Device, x.Type })
            .Select(g => g.First())
            .ToList();

        // Get recent statuses for each device/type combination
        var allRecents = new List<DeviceStatus>();

        foreach (var deviceAndType in deviceAndTypes)
        {
            var recentsForDeviceType = deviceStatuses
                .Where(status =>
                    status.Device == deviceAndType.Device && HasProperty(status, deviceAndType.Type)
                )
                .Where(status => status.Mills <= time)
                .OrderBy(status => status.Mills)
                .TakeLast(10)
                .ToList();
            allRecents.AddRange(recentsForDeviceType);
        }

        // Flatten and deduplicate
        var result = allRecents
            .Where(status => status != null)
            .GroupBy(status => status.Id)
            .Select(g => g.First())
            .OrderBy(status => status.Mills)
            .ToList();

        return result;
    }

    /// <inheritdoc />
    public List<Treatment> ProcessDurations(List<Treatment> treatments, bool keepZeroDuration)
    {
        // Remove duplicates by mills
        var seenMills = new HashSet<long>();
        treatments = treatments.Where(t => seenMills.Add(t.Mills)).ToList();

        // Find end events (treatments without duration)
        var endEvents = treatments.Where(t => !t.Duration.HasValue || t.Duration == 0).ToList();

        // Helper function to cut overlapping durations
        void CutIfInInterval(Treatment baseT, Treatment endT)
        {
            if (
                baseT.Duration.HasValue
                && baseT.Duration > 0
                && baseT.Mills < endT.Mills
                && baseT.Mills + (baseT.Duration * 60000) > endT.Mills
            ) // Duration in minutes, convert to ms
            {
                var newDurationMs = endT.Mills - baseT.Mills;
                var newDurationMins = newDurationMs / 60000.0; // Convert back to minutes
                baseT.Duration = newDurationMins;

                if (!string.IsNullOrEmpty(endT.Profile))
                {
                    baseT.CuttedBy = endT.Profile;
                    endT.Cutting = baseT.Profile;
                }
            }
        }

        // Cut by end events
        foreach (var treatment in treatments)
        {
            if (treatment.Duration.HasValue && treatment.Duration > 0)
            {
                foreach (var endEvent in endEvents)
                {
                    CutIfInInterval(treatment, endEvent);
                }
            }
        }

        // Cut by overlapping events
        foreach (var treatment in treatments)
        {
            if (treatment.Duration.HasValue && treatment.Duration > 0)
            {
                foreach (var otherTreatment in treatments)
                {
                    CutIfInInterval(treatment, otherTreatment);
                }
            }
        }

        // Return filtered results
        if (keepZeroDuration)
        {
            return treatments;
        }
        else
        {
            return treatments.Where(t => t.Duration.HasValue && t.Duration > 0).ToList();
        }
    }

    /// <inheritdoc />    /// <inheritdoc />
    public List<Treatment> ConvertTempTargetUnits(List<Treatment> treatments)
    {
        // Deep clone to avoid modifying originals
        var convertedTreatments = DeepClone(treatments);

        for (int i = 0; i < convertedTreatments.Count; i++)
        {
            var treatment = convertedTreatments[i];
            if (!TemporaryTargetUnits.IsMmol(treatment.TargetTop, treatment.TargetBottom, treatment.Units))
                continue;

            (treatment.TargetTop, treatment.TargetBottom) =
                TemporaryTargetUnits.ToMgdl(treatment.TargetTop, treatment.TargetBottom, treatment.Units);
            treatment.Units = "mg/dl";
        }

        return convertedTreatments;
    }

    /// <inheritdoc />
    public T ProcessRawDataForRuntime<T>(T data)
        where T : class
    {
        // Deep clone the data
        var obj = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(data));

        if (obj == null)
            return data;

        // Process only top-level properties, matching JavaScript implementation
        var type = obj.GetType();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in properties)
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;

            var value = prop.GetValue(obj);
            if (value == null || IsSimpleType(value.GetType()))
                continue;

            // Convert _id to string if present
            var idProp = value.GetType().GetProperty("Id") ?? value.GetType().GetProperty("_id");
            if (idProp != null && idProp.CanRead && idProp.CanWrite)
            {
                var idValue = idProp.GetValue(value);
                if (idValue != null)
                {
                    idProp.SetValue(value, idValue.ToString());
                }
            }

            // Add mills property if created_at exists but mills doesn't
            var millsProp = value.GetType().GetProperty("Mills");
            var createdAtProp =
                value.GetType().GetProperty("CreatedAt")
                ?? value.GetType().GetProperty("created_at");

            if (
                millsProp != null
                && millsProp.CanWrite
                && createdAtProp != null
                && createdAtProp.CanRead
            )
            {
                var millsValue = millsProp.GetValue(value);
                var createdAtValue = createdAtProp.GetValue(value);

                if (
                    (millsValue == null || (millsValue is long mills && mills == 0))
                    && createdAtValue != null
                )
                {
                    long millsToSet = 0;

                    if (createdAtValue is DateTime dateTime)
                    {
                        millsToSet = ((DateTimeOffset)dateTime).ToUnixTimeMilliseconds();
                    }
                    else if (createdAtValue is DateTimeOffset dateTimeOffset)
                    {
                        millsToSet = dateTimeOffset.ToUnixTimeMilliseconds();
                    }
                    else if (createdAtValue is string dateString)
                    {
                        if (UploaderTimestamp.TryParse(dateString, out var parsedDate))
                        {
                            millsToSet = parsedDate.ToUnixTimeMilliseconds();
                        }
                    }

                    if (millsToSet > 0)
                    {
                        millsProp.SetValue(value, millsToSet);
                    }
                }
            }

            // Add mills property if sysTime exists but mills doesn't
            var sysTimeProp =
                value.GetType().GetProperty("SysTime") ?? value.GetType().GetProperty("sysTime");

            if (
                millsProp != null
                && millsProp.CanWrite
                && sysTimeProp != null
                && sysTimeProp.CanRead
            )
            {
                var millsValue = millsProp.GetValue(value);
                var sysTimeValue = sysTimeProp.GetValue(value);

                if (
                    (millsValue == null || (millsValue is long mills && mills == 0))
                    && sysTimeValue != null
                )
                {
                    long millsToSet = 0;

                    if (sysTimeValue is DateTime dateTime)
                    {
                        millsToSet = ((DateTimeOffset)dateTime).ToUnixTimeMilliseconds();
                    }
                    else if (sysTimeValue is DateTimeOffset dateTimeOffset)
                    {
                        millsToSet = dateTimeOffset.ToUnixTimeMilliseconds();
                    }
                    else if (sysTimeValue is string dateString)
                    {
                        if (UploaderTimestamp.TryParse(dateString, out var parsedDate))
                        {
                            millsToSet = parsedDate.ToUnixTimeMilliseconds();
                        }
                    }

                    if (millsToSet > 0)
                    {
                        millsProp.SetValue(value, millsToSet);
                    }
                }
            }
        }

        return obj;
    }

    private static bool IsSimpleType(Type type)
    {
        return type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan)
            || type == typeof(Guid)
            || (
                type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(Nullable<>)
                && IsSimpleType(type.GetGenericArguments()[0])
            );
    }

    /// <inheritdoc />
    public List<T> IdMergePreferNew<T>(List<T> oldData, List<T> newData)
        where T : class
    {
        if (newData == null && oldData != null)
            return oldData;
        if (oldData == null && newData != null)
            return newData;
        if (oldData == null && newData == null)
            return new List<T>();

        // Start with a deep clone of newData
        var merged = DeepClone(newData!);

        // Iterate through oldData and add items not found in newData
        for (int i = 0; i < oldData!.Count; i++)
        {
            var oldElement = oldData[i];
            var oldId = GetId(oldElement);
            bool found = false;

            // Check if this element exists in newData
            for (int j = 0; j < newData!.Count; j++)
            {
                var newId = GetId(newData[j]);
                if (oldId != null && newId != null && oldId == newId)
                {
                    found = true;
                    break;
                }
            }

            // If not found in new data, add the old element
            if (!found)
            {
                merged.Add(oldElement);
            }
        }

        return merged;
    }

    /// <summary>
    /// A JSON deep clone that keeps each document's <see cref="IProcessableDocument.Id"/>, which the
    /// serialized form carries only in its lossy wire spelling (<see cref="Nocturne.Core.Models.Serializers.ObjectIdJsonConverter"/>).
    /// </summary>
    private static List<T> DeepClone<T>(List<T> source)
    {
        var clone = JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(source)) ?? [];
        for (var i = 0; i < clone.Count; i++)
        {
            if (clone[i] is IProcessableDocument copy && source[i] is IProcessableDocument original)
                copy.Id = original.Id;
        }

        return clone;
    }

    private static string? GetId(object item)
    {
        // Check for Id property first, then _id
        var idProperty = item.GetType().GetProperty("Id") ?? item.GetType().GetProperty("_id");
        return idProperty?.GetValue(item)?.ToString();
    }

    private static bool HasProperty(object obj, string propertyName)
    {
        return obj.GetType()
                .GetProperty(
                    propertyName,
                    BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance
                ) != null;
    }

    private static EntryQuery FrameEntryQuery(DData ddata, string type, long timestamp) =>
        new()
        {
            Type = type,
            Count = 1000,
            FromMills = ddata is DDataFrame ? timestamp - FrameEntriesWindowMills : null,
            ToMills = ddata is DDataFrame ? timestamp : null,
        };

    private static string CreatedAtWindow(long fromMills, long toMills) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["created_at"] = new Dictionary<string, string>
            {
                ["$gte"] = DateTimeOffset.FromUnixTimeMilliseconds(fromMills).UtcDateTime.ToString("o"),
                ["$lte"] = DateTimeOffset.FromUnixTimeMilliseconds(toMills).UtcDateTime.ToString("o"),
            },
        });

    private async Task LoadSgvsAsync(
        DData ddata,
        long timestamp,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Load SGV entries with type filter - reduced count to prevent memory issues
            var sgvs = await _entries.QueryEntriesAsync(
                FrameEntryQuery(ddata, "sgv", timestamp),
                cancellationToken);
            ddata.Sgvs = sgvs.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading SGVs");
            ddata.Sgvs = new List<Entry>();
        }
    }

    private async Task LoadTreatmentsAsync(
        DData ddata,
        long timestamp,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var treatments = await _treatments.GetTreatmentsAsync(
                find: ddata is DDataFrame
                    ? CreatedAtWindow(timestamp - FrameTreatmentsWindowMills, timestamp)
                    : null,
                count: 1000,
                skip: 0,
                cancellationToken: cancellationToken
            );
            ddata.Treatments = treatments.ToList();
            if (ddata is DDataFrame)
            {
                ddata.Treatments = await MergeLatestFrameTreatmentsAsync(
                    ddata.Treatments, timestamp, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading treatments");
            ddata.Treatments = new List<Treatment>();
        }
    }

    private async Task<List<Treatment>> MergeLatestFrameTreatmentsAsync(
        List<Treatment> windowed,
        long timestamp,
        CancellationToken cancellationToken)
    {
        var latest = new List<Treatment>();
        foreach (var (eventType, windowMills) in FrameLatestTreatments)
        {
            try
            {
                var treatment = await _treatments.GetLatestTreatmentOfTypeAsync(
                    eventType, timestamp - windowMills, timestamp, cancellationToken);
                if (treatment is not null)
                    latest.Add(treatment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading the latest {EventType} treatment", eventType);
            }
        }

        return windowed
            .Concat(latest)
            .DistinctBy(t => t.Id)
            .OrderByDescending(t => t.Mills)
            .ToList();
    }

    private async Task LoadMbgsAsync(
        DData ddata,
        long timestamp,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Load MBG entries with type filter - reduced count to prevent memory issues
            var mbgs = await _entries.QueryEntriesAsync(
                FrameEntryQuery(ddata, "mbg", timestamp),
                cancellationToken);
            ddata.Mbgs = mbgs.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading MBGs");
            ddata.Mbgs = new List<Entry>();
        }
    }

    private async Task LoadCalsAsync(
        DData ddata,
        long timestamp,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Load calibration entries with type filter - reduced count to prevent memory issues
            var cals = await _entries.QueryEntriesAsync(
                FrameEntryQuery(ddata, "cal", timestamp),
                cancellationToken);
            ddata.Cals = cals.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading calibrations");
            ddata.Cals = new List<Entry>();
        }
    }

    private async Task LoadProfilesAsync(DData ddata, CancellationToken cancellationToken)
    {
        try
        {
            var profiles = await _profiles.GetProfilesAsync(
                count: 10,
                skip: 0,
                ct: cancellationToken
            );
            ddata.Profiles = profiles.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading profiles");
            ddata.Profiles = new List<Profile>();
        }
    }

    private async Task LoadDeviceStatusAsync(
        DData ddata,
        long timestamp,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var window = new Dictionary<string, object>
            {
                ["created_at"] = new Dictionary<string, string>
                {
                    ["$gte"] = DateTimeOffset.FromUnixTimeMilliseconds(timestamp - DeviceStatusWindowMills).UtcDateTime.ToString("o"),
                    ["$lte"] = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime.ToString("o"),
                },
            };
            var deviceStatuses = await _deviceStatuses.GetDeviceStatusesAsync(
                FindQuery.ParseDeviceStatus(JsonSerializer.Serialize(window)),
                count: 1000,
                skip: 0,
                cancellationToken: cancellationToken
            );
            ddata.DeviceStatus = deviceStatuses.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading device status");
            ddata.DeviceStatus = new List<DeviceStatus>();
        }
    }

    private async Task LoadFoodAsync(DData ddata, CancellationToken cancellationToken)
    {
        try
        {
            var food = await _food.GetFoodAsync(cancellationToken: cancellationToken);
            ddata.Food = food.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading food");
            ddata.Food = new List<Food>();
        }
    }

    private async Task LoadActivityAsync(
        DData ddata,
        long timestamp,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var activities = await _activities.GetActivitiesAsync(
                find: null,
                count: 1000,
                skip: 0,
                cancellationToken: cancellationToken
            );
            ddata.Activity = activities.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading activity");
            ddata.Activity = new List<Activity>();
        }
    }

    private async Task LoadDbStatsAsync(DData ddata, CancellationToken cancellationToken) =>
        ddata.DbStats = await _storeSize.ReadAsync(cancellationToken);

    /// <inheritdoc />
    public DData Clone(DData ddata)
    {
        // Create a deep clone using JSON serialization
        return JsonSerializer.Deserialize<DData>(JsonSerializer.Serialize(ddata)) ?? new DData();
    }
}
