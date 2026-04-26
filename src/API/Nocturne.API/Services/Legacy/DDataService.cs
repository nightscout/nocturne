using System.Reflection;
using System.Text.Json;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Legacy;

/// <summary>
/// Implements the Nightscout <c>/api/v1/ddata</c> response payload, which bundles recent entries,
/// treatments, profiles, device statuses, food, and activity data into a single response used by
/// legacy Nightscout clients and plugins.
/// </summary>
/// <seealso cref="IDDataService"/>
public class DDataService : IDDataService
{
    private readonly IEntryStore _store;
    private readonly ITreatmentService _treatments;
    private readonly IProfileProjectionService _profiles;
    private readonly DeviceStatusProjectionService _projectionService;
    private readonly IFoodRepository _food;
    private readonly IActivityService _activities;
    private readonly ILogger<DDataService> _logger;

    // Device type fields that should be considered for recent device status
    private static readonly string[] DeviceTypeFields =
    {
        "uploader",
        "pump",
        "openaps",
        "loop",
        "xdripjs",
    };

    // Constant for mmol/L to mg/dL conversion
    private const double MMOL_TO_MGDL = 18.0182;

    public DDataService(
        IEntryStore store,
        ITreatmentService treatments,
        IProfileProjectionService profiles,
        DeviceStatusProjectionService projectionService,
        IFoodRepository food,
        IActivityService activities,
        ILogger<DDataService> logger)
    {
        _store = store;
        _treatments = treatments;
        _profiles = profiles;
        _projectionService = projectionService;
        _food = food;
        _activities = activities;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DData> GetDDataAsync(
        long timestamp,
        CancellationToken cancellationToken = default
    )
    {
        var ddata = new DData { LastUpdated = timestamp };

        // Load data from various collections based on timestamp.
        // Fetched sequentially — the underlying service shares a scoped DbContext
        // which is not thread-safe for concurrent access.
        await LoadSgvsAsync(ddata, timestamp, cancellationToken);
        await LoadTreatmentsAsync(ddata, timestamp, cancellationToken);
        await LoadMbgsAsync(ddata, timestamp, cancellationToken);
        await LoadCalsAsync(ddata, timestamp, cancellationToken);
        await LoadProfilesAsync(ddata, cancellationToken);
        await LoadDeviceStatusAsync(ddata, timestamp, cancellationToken);
        await LoadFoodAsync(ddata, cancellationToken);
        await LoadActivityAsync(ddata, timestamp, cancellationToken);
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
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return await GetDDataAsync(currentTime, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DDataResponse> GetDDataWithRecentStatusesAsync(
        long timestamp,
        CancellationToken cancellationToken = default
    )
    {
        var ddata = await GetDDataAsync(timestamp, cancellationToken);

        // Clone profiles and filter out temporary profile switches (containing "@@@@@")
        var profiles =
            JsonSerializer.Deserialize<List<Profile>>(JsonSerializer.Serialize(ddata.Profiles))
            ?? new();
        if (profiles.Count > 0 && profiles[0].Store != null)
        {
            var keysToRemove = profiles[0].Store.Keys.Where(k => k.Contains("@@@@@")).ToList();
            foreach (var key in keysToRemove)
            {
                profiles[0].Store.Remove(key);
            }
        }

        return new DDataResponse
        {
            DeviceStatus = GetRecentDeviceStatus(ddata.DeviceStatus, timestamp),
            Sgvs = ddata.Sgvs,
            Cals = ddata.Cals,
            Profiles = profiles,
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
            profileTreatments =
                JsonSerializer.Deserialize<List<Treatment>>(
                    JsonSerializer.Serialize(profileTreatments)
                ) ?? new();
        }
        ddata.ProfileTreatments = ProcessDurations(profileTreatments, true);

        // filter & prepare temp basals
        var tempBasalTreatments = treatments
            .Where(t => !string.IsNullOrEmpty(t.EventType) && t.EventType.Contains("Temp Basal"))
            .ToList();
        if (preserveOriginalTreatments)
        {
            tempBasalTreatments =
                JsonSerializer.Deserialize<List<Treatment>>(
                    JsonSerializer.Serialize(tempBasalTreatments)
                ) ?? new();
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
            tempTargetTreatments =
                JsonSerializer.Deserialize<List<Treatment>>(
                    JsonSerializer.Serialize(tempTargetTreatments)
                ) ?? new();
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
        var convertedTreatments =
            JsonSerializer.Deserialize<List<Treatment>>(JsonSerializer.Serialize(treatments))
            ?? new();

        for (int i = 0; i < convertedTreatments.Count; i++)
        {
            var treatment = convertedTreatments[i];
            bool converted = false;

            // If treatment is in mmol, convert to mg/dl
            if (!string.IsNullOrEmpty(treatment.Units) && treatment.Units == "mmol")
            {
                if (treatment.TargetTop.HasValue)
                    treatment.TargetTop = treatment.TargetTop * MMOL_TO_MGDL;
                if (treatment.TargetBottom.HasValue)
                    treatment.TargetBottom = treatment.TargetBottom * MMOL_TO_MGDL;
                treatment.Units = "mg/dl";
                converted = true;
            }

            // If we have a temp target that's below 20, assume it's mmol and convert to mg/dl for safety
            if (
                !converted
                && (
                    (treatment.TargetTop.HasValue && treatment.TargetTop < 20)
                    || (treatment.TargetBottom.HasValue && treatment.TargetBottom < 20)
                )
            )
            {
                if (treatment.TargetTop.HasValue)
                    treatment.TargetTop = treatment.TargetTop * MMOL_TO_MGDL;
                if (treatment.TargetBottom.HasValue)
                    treatment.TargetBottom = treatment.TargetBottom * MMOL_TO_MGDL;
                treatment.Units = "mg/dl";
            }
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
                        if (DateTime.TryParse(dateString, out var parsedDate))
                        {
                            millsToSet = ((DateTimeOffset)parsedDate).ToUnixTimeMilliseconds();
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
                        if (DateTime.TryParse(dateString, out var parsedDate))
                        {
                            millsToSet = ((DateTimeOffset)parsedDate).ToUnixTimeMilliseconds();
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
        var merged =
            JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(newData)) ?? new List<T>();

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

    private async Task LoadSgvsAsync(
        DData ddata,
        long timestamp,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Load SGV entries with type filter - reduced count to prevent memory issues
            var sgvs = await _store.QueryAsync(
                new EntryQuery { Type = "sgv", Count = 1000 },
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
                count: 1000,
                skip: 0,
                cancellationToken: cancellationToken
            );
            ddata.Treatments = treatments.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading treatments");
            ddata.Treatments = new List<Treatment>();
        }
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
            var mbgs = await _store.QueryAsync(
                new EntryQuery { Type = "mbg", Count = 1000 },
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
            var cals = await _store.QueryAsync(
                new EntryQuery { Type = "cal", Count = 1000 },
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
            var deviceStatuses = await _projectionService.GetAsync(
                count: 1000,
                skip: 0,
                find: null,
                ct: cancellationToken
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

    private Task LoadDbStatsAsync(DData ddata, CancellationToken cancellationToken)
    {
        try
        {
            // For now, return empty stats - would need to implement database statistics collection
            ddata.DbStats = new DbStats();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading database stats");
            ddata.DbStats = new DbStats();
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public DData Clone(DData ddata)
    {
        // Create a deep clone using JSON serialization
        return JsonSerializer.Deserialize<DData>(JsonSerializer.Serialize(ddata)) ?? new DData();
    }
}
