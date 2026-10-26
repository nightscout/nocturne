using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.Core.Clinical.Schedules;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Battery;
using Nocturne.Core.Models.V4;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Treatments;

namespace Nocturne.API.Services.Profiles;

/// <summary>
/// Computes the Nightscout <c>/api/v1/properties</c> response, aggregating current glucose,
/// IOB, COB, AR2 forecast, delta, direction, and plugin data into a single structured payload
/// with 1:1 compatibility with the legacy <c>properties.js</c>.
/// </summary>
/// <seealso cref="IPropertiesService"/>
public class PropertiesService : IPropertiesService
{
    private readonly IDDataService _ddataService;
    private readonly ILogger<PropertiesService> _logger;
    private readonly IIobCobReader _iobCobReader;
    private readonly IAr2Service _ar2Service;
    private readonly IDeviceAgeService _deviceAgeService;
    private readonly ITherapySettingsResolver _therapySettings;
    private readonly TimeProvider _time;
    private readonly DbSizeOptions _dbSize;

    // Properties served by IDeviceAgeService, each costing its own query.
    private static readonly HashSet<string> DeviceAgeProperties =
        new(StringComparer.OrdinalIgnoreCase) { "cage", "sage", "iage", "bage" };

    // Properties that should be filtered out for security
    private static readonly string[] SecureProperties =
    {
        "apnsKey",
        "apnsKeyId",
        "developerTeamId",
        "userName",
        "password",
        "obscured",
        "obscureDeviceProvenance",
    };

    public PropertiesService(
        IDDataService ddataService,
        ILogger<PropertiesService> logger,
        IIobCobReader iobCobReader,
        IAr2Service ar2Service,
        IDeviceAgeService deviceAgeService,
        ITherapySettingsResolver therapySettings,
        TimeProvider time,
        IOptions<DbSizeOptions> dbSize
    )
    {
        _ddataService = ddataService;
        _logger = logger;
        _iobCobReader = iobCobReader;
        _ar2Service = ar2Service;
        _deviceAgeService = deviceAgeService;
        _therapySettings = therapySettings;
        _time = time;
        _dbSize = dbSize.Value;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>> GetAllPropertiesAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var properties = await BuildSandboxPropertiesAsync(cancellationToken);
            return ApplySecurityFiltering(properties);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all properties");
            return new Dictionary<string, object>();
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>> GetPropertiesAsync(
        IEnumerable<string> propertyNames,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var requested = propertyNames as IReadOnlyCollection<string> ?? propertyNames.ToList();
            var allProperties = await BuildSandboxPropertiesAsync(cancellationToken, requested);
            var filteredProperties = new Dictionary<string, object>();

            foreach (var propertyName in requested)
            {
                if (allProperties.TryGetValue(propertyName, out var value))
                {
                    filteredProperties[propertyName] = value;
                }
            }

            return ApplySecurityFiltering(filteredProperties);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error getting specific properties: {PropertyNames}",
                string.Join(",", propertyNames)
            );
            return new Dictionary<string, object>();
        }
    }

    /// <inheritdoc />
    public Dictionary<string, object> ApplySecurityFiltering(Dictionary<string, object> properties)
    {
        var filtered = new Dictionary<string, object>(properties);

        foreach (var secureProperty in SecureProperties)
        {
            filtered.Remove(secureProperty);
        }

        // Recursively filter nested objects
        foreach (var kvp in filtered.ToList())
        {
            if (kvp.Value is Dictionary<string, object> nestedDict)
            {
                filtered[kvp.Key] = ApplySecurityFiltering(nestedDict);
            }
            else if (
                kvp.Value is JsonElement jsonElement
                && jsonElement.ValueKind == JsonValueKind.Object
            )
            {
                var nestedProperties =
                    JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText())
                    ?? new Dictionary<string, object>();
                filtered[kvp.Key] = ApplySecurityFiltering(nestedProperties);
            }
        }

        return filtered;
    }

    /// <summary>
    /// Build sandbox properties similar to the legacy JavaScript implementation
    /// This simulates the plugin system that sets properties on the sandbox
    /// </summary>
    /// <remarks>
    /// When <paramref name="requested"/> is set, only those property names were asked for, and
    /// properties whose sources cost extra queries are skipped; null builds everything.
    /// </remarks>
    private async Task<Dictionary<string, object>> BuildSandboxPropertiesAsync(
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? requested = null
    )
    {
        var properties = new Dictionary<string, object>();
        var timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds();

        try
        {
            // Get DData which contains the current state
            var ddata = await _ddataService.GetDDataAsync(timestamp, cancellationToken);

            // Build properties that would be set by plugins

            // BGNow properties (most recent glucose entry)
            SetBgNowPropertiesAsync(properties, ddata);

            // Delta properties (change in glucose)
            SetDeltaProperties(properties, ddata);

            // Direction properties
            SetDirectionProperties(properties, ddata);

            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var iobCob = await ReadIobCobAsync(now);
            if (iobCob is not null)
            {
                SetIobProperties(properties, iobCob.Iob, now);
                if (iobCob.Cob is not null)
                    SetCobProperties(properties, iobCob.Cob, now);
            }

            // Device status properties
            SetDeviceStatusProperties(properties, ddata);

            // AR2 properties (prediction algorithm)
            await SetAr2PropertiesAsync(properties, ddata);

            // Raw BG properties
            SetRawBgProperties(properties, ddata);

            // Battery properties
            SetBatteryProperties(properties, ddata);

            // Device age properties (cannula / sensor / insulin / battery age). Each is a
            // separate query, so skip them when the caller asked for other properties —
            // clients poll single properties like /api/v2/properties/iob frequently.
            if (requested == null || requested.Any(DeviceAgeProperties.Contains))
            {
                await SetDeviceAgePropertiesAsync(properties, cancellationToken);
            }

            // Profile properties
            SetProfileProperties(properties, ddata);

            // Basal properties
            if (requested == null || requested.Contains("basal"))
            {
                await SetBasalPropertiesAsync(properties, ddata, cancellationToken);
            }

            // DB Size properties
            SetDbSizeProperties(properties, ddata);

            // Runtime state
            SetRuntimeStateProperties(properties);

            _logger.LogDebug("Built {PropertyCount} sandbox properties", properties.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building sandbox properties");
        }

        return properties;
    }

    /// <summary>
    /// Sets <c>bgnow</c> to the newest 5 minute bucket, as <c>bgnow.js</c> does (see
    /// <see cref="DirectionService.BuildBuckets"/>). A bucket holding only error codes has no
    /// <c>mean</c>, <c>last</c> or <c>mills</c>, and so no delta or forecast follows it.
    /// </summary>
    private void SetBgNowPropertiesAsync(Dictionary<string, object> properties, DData ddata)
    {
        var currentSgv = ddata.Sgvs?.OrderByDescending(s => s.Mills).FirstOrDefault();
        if (currentSgv == null)
            return;

        var mgdlValue = currentSgv.GlucoseMgdl();
        var bgnow = BucketProperties(DirectionService.BuildBuckets(ddata.Sgvs!)[0]);
        bgnow["mgdl"] = mgdlValue;
        bgnow["scaled"] = currentSgv.Scaled ?? mgdlValue;
        bgnow["displayLine"] = $"BG Now: {currentSgv.Scaled ?? mgdlValue} mg/dl";

        properties["bgnow"] = bgnow;
    }

    private static Dictionary<string, object> BucketProperties(BgNowBucket bucket)
    {
        var result = new Dictionary<string, object> { ["sgvs"] = bucket.Entries };

        if (bucket.Last != null)
        {
            result["mean"] = bucket.Mean!.Value;
            result["last"] = bucket.Last.GlucoseMgdl();
            result["mills"] = bucket.Last.Mills;
        }

        if (bucket.Errors.Count > 0)
            result["errors"] = bucket.Errors;

        return result;
    }

    /// <summary>
    /// Set delta properties showing glucose change between the two most recent 5 minute buckets
    /// </summary>
    private void SetDeltaProperties(Dictionary<string, object> properties, DData ddata)
    {
        if (ddata.Sgvs == null)
            return;

        var bucketDelta = DirectionService.CalculateBucketDelta(
            DirectionService.BuildBuckets(ddata.Sgvs),
            "mg/dl"
        );
        if (bucketDelta == null)
            return;

        var deltaInfo = bucketDelta.Info;
        properties["delta"] = new Dictionary<string, object>
        {
            ["display"] = deltaInfo.Display ?? "",
            ["mgdl"] = deltaInfo.Mgdl ?? 0,
            ["scaled"] = deltaInfo.Scaled ?? 0,
            ["interpolated"] = deltaInfo.Interpolated,
            ["absolute"] = deltaInfo.Absolute,
            ["elapsedMins"] = deltaInfo.ElapsedMins ?? 0,
            ["mean5MinsAgo"] = deltaInfo.Mean5MinsAgo ?? 0,
            ["previous"] = BucketProperties(bucketDelta.Previous),
            ["times"] = deltaInfo.Times,
        };
    }

    /// <summary>
    /// Set direction properties showing trend - exact legacy algorithm
    /// </summary>
    private void SetDirectionProperties(Dictionary<string, object> properties, DData ddata)
    {
        var currentEntry = ddata.Sgvs?.OrderByDescending(s => s.Mills).FirstOrDefault();
        if (currentEntry == null)
            return;

        // Use the DirectionService for exact legacy direction calculation
        var directionInfo = DirectionService.GetDirectionInfo(currentEntry);

        properties["direction"] = new Dictionary<string, object>
        {
            ["value"] = currentEntry.Direction ?? "NONE",
            ["label"] = directionInfo.Label,
            ["entity"] = directionInfo.Entity,
        };
    }

    private async Task<IobCobReading?> ReadIobCobAsync(long now)
    {
        try
        {
            return await _iobCobReader.ReadTotalAsync(now, live: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading IOB and COB; leaving both properties out");
            return null;
        }
    }

    /// <summary>
    /// Set IOB (Insulin on Board) properties. Emitted even with no insulin on board: legacy
    /// clients treat a missing <c>iob</c> as "server doesn't support it" rather than "zero".
    /// </summary>
    private static void SetIobProperties(Dictionary<string, object> properties, IobResult iobResult, long now)
    {
        var iob = Math.Round(iobResult.Iob, 2);
        var display = IobResult.FormatDisplay(iob);

        // NIGHTSCOUT-COMPAT: Nightscout derives iob and activity from boluses alone. Here both
        // include the net basal IOB of temp basals, as the chart does, so they can go negative.
        properties["iob"] = new Dictionary<string, object?>
        {
            ["iob"] = iob,
            ["display"] = display,
            ["displayLine"] = iobResult.DisplayLine ?? $"IOB: {display}U",
            ["timestamp"] = now,
            ["source"] = iobResult.Source ?? "Care Portal",
            // Legacy calcTotal reports zeros rather than nulls for the numeric parts.
            ["activity"] = iobResult.Activity ?? 0,
            ["lastBolus"] = iobResult.LastBolus?.Mills,
            ["basalIob"] = iobResult.BasalIob ?? 0,
            ["treatmentIob"] = iobResult.TreatmentIob ?? 0,
        };
    }

    /// <summary>
    /// Set COB (Carbs on Board) properties, emitted even with no carbs on board, as IOB is.
    /// </summary>
    private static void SetCobProperties(Dictionary<string, object> properties, CobResult cobResult, long now)
    {
        var cob = Math.Round(cobResult.Cob);
        var display = cobResult.Display ?? cob.ToString(CultureInfo.InvariantCulture);

        properties["cob"] = new Dictionary<string, object?>
        {
            ["cob"] = cob,
            ["display"] = display,
            ["displayLine"] = cobResult.DisplayLine ?? $"COB: {display}g",
            ["timestamp"] = now,
            ["source"] = cobResult.Source ?? "Care Portal",
            ["activity"] = cobResult.Activity ?? 0,
        };
    }

    /// <summary>
    /// Set device status properties
    /// </summary>
    private void SetDeviceStatusProperties(Dictionary<string, object> properties, DData ddata)
    {
        var latestDeviceStatus = ddata
            .DeviceStatus?.OrderByDescending(d => d.Mills)
            .FirstOrDefault();
        if (latestDeviceStatus == null)
            return;

        properties["devicestatus"] = new Dictionary<string, object?>
        {
            ["device"] = latestDeviceStatus.Device ?? "unknown",
            ["mills"] = latestDeviceStatus.Mills,
            ["uploaderBattery"] = latestDeviceStatus.Uploader?.Battery,
            ["created_at"] = latestDeviceStatus.CreatedAt,
        };
    }

    /// <summary>
    /// Set AR2 prediction properties with full 1:1 legacy compatibility
    /// </summary>
    private async Task SetAr2PropertiesAsync(Dictionary<string, object> properties, DData ddata)
    {
        try
        {
            // Get BGNow and Delta properties that AR2 depends on
            if (
                !properties.TryGetValue("bgnow", out var bgNowObj)
                || bgNowObj is not Dictionary<string, object> bgNowProperties
            )
                return;

            if (
                !properties.TryGetValue("delta", out var deltaObj)
                || deltaObj is not Dictionary<string, object> deltaProperties
            )
                return;

            // Get settings for thresholds (use defaults if not available)
            var settings = new Dictionary<string, object>
            {
                ["bgTargetTop"] = ApplicationConstants.Web.Thresholds.BgTargetTop,
                ["bgTargetBottom"] = ApplicationConstants.Web.Thresholds.BgTargetBottom,
                ["alarmHigh"] = true,
                ["alarmLow"] = true,
            };

            // Calculate AR2 forecast using the full algorithm
            var ar2Properties = await _ar2Service.CalculateForecastAsync(
                ddata,
                bgNowProperties,
                deltaProperties,
                settings
            );

            // Convert to legacy-compatible format
            var ar2Dict = new Dictionary<string, object>();

            if (ar2Properties.Forecast != null)
            {
                ar2Dict["forecast"] = new Dictionary<string, object>
                {
                    ["predicted"] = ar2Properties
                        .Forecast.Predicted.Select(p => new
                        {
                            mills = p.Mills,
                            mgdl = p.Mgdl,
                            color = p.Color,
                        })
                        .ToArray(),
                    ["avgLoss"] = ar2Properties.Forecast.AvgLoss,
                };
            }

            if (!string.IsNullOrEmpty(ar2Properties.Level))
                ar2Dict["level"] = ar2Properties.Level;

            if (!string.IsNullOrEmpty(ar2Properties.EventName))
                ar2Dict["eventName"] = ar2Properties.EventName;

            if (!string.IsNullOrEmpty(ar2Properties.DisplayLine))
                ar2Dict["displayLine"] = ar2Properties.DisplayLine;

            properties["ar2"] = ar2Dict;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to calculate AR2 properties");
            // Don't add AR2 properties if calculation fails
        }
    }

    /// <summary>
    /// Set raw BG properties
    /// </summary>
    private void SetRawBgProperties(Dictionary<string, object> properties, DData ddata)
    {
        // Look for raw glucose data in entries
        var rawEntry = ddata
            .Sgvs?.OrderByDescending(s => s.Mills)
            .FirstOrDefault(s => s.Noise.HasValue);
        if (rawEntry == null)
            return;

        var mgdlValue = rawEntry.GlucoseMgdl();

        properties["rawbg"] = new Dictionary<string, object?>
        {
            ["mgdl"] = mgdlValue,
            ["noise"] = rawEntry.Noise,
            ["displayLine"] = $"Raw BG: {mgdlValue} mg/dl",
        };
    }

    private void SetBatteryProperties(Dictionary<string, object> properties, DData ddata)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();

        // Only statuses that actually carry an uploader battery: AID systems interleave
        // pump-only device statuses, and the newest record is often one of those.
        var batteryReports = (ddata.DeviceStatus ?? [])
            .Where(d => d.Uploader?.Battery != null)
            .ToList();

        var weakest = UploaderBatterySelection.Select(
            batteryReports.Select(ToBatteryReading),
            now);
        if (weakest == null)
            return;

        // The most recent report from each uploader, weakest battery first. Legacy
        // clients read this list to show more than one phone.
        var devices = batteryReports
            .GroupBy(DeviceKey)
            .Select(g => g.OrderByDescending(d => d.Mills).First())
            .OrderBy(d => d.Uploader!.Battery)
            .Select(d => new Dictionary<string, object?>
            {
                ["name"] = d.Uploader!.Name ?? DeviceKey(d),
                ["device"] = DeviceKey(d),
                ["value"] = d.Uploader.Battery,
                ["voltage"] = d.Uploader.BatteryVoltage,
                ["isCharging"] = d.Uploader.IsCharging,
                ["temperature"] = d.Uploader.Temperature,
                ["mills"] = d.Mills,
            })
            .ToList();

        var upbat = new Dictionary<string, object> { ["devices"] = devices };

        if (UpbatLevel(weakest) is { } level)
        {
            upbat["level"] = level;
            upbat["display"] = $"{weakest.Battery}%";
            upbat["displayLine"] = $"Battery: {weakest.Battery}%";
        }

        properties["upbat"] = upbat;
    }

    // Nightscout sets nothing for 0% without a voltage, and falls through to 25 for 0% with one.
    private static int? UpbatLevel(BatteryReading reading) =>
        reading.Battery != 0 ? reading.Level
        : reading.Voltage is > 0 ? 25
        : null;

    private static string DeviceKey(DeviceStatus status) =>
        string.IsNullOrEmpty(status.Device) ? "unknown" : status.Device;

    private static BatteryReading ToBatteryReading(DeviceStatus status) =>
        new()
        {
            Device = DeviceKey(status),
            Battery = status.Uploader!.Battery,
            Voltage = status.Uploader.BatteryVoltage,
            IsCharging = status.Uploader.IsCharging ?? false,
            Temperature = status.Uploader.Temperature,
            Mills = status.Mills,
        };

    /// <summary>
    /// Set the legacy device-age plugin properties: cannula (cage), sensor (sage),
    /// insulin (iage) and pump battery (bage) age.
    /// </summary>
    private async Task SetDeviceAgePropertiesAsync(
        Dictionary<string, object> properties,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Thresholds/display are per-plugin settings in legacy Nightscout; the
            // properties endpoint reports the service defaults.
            var prefs = new DeviceAgePreferences();

            // The sage plugin always shows days and hours.
            var sensor = await _deviceAgeService.GetSensorAgeAsync(
                new DeviceAgePreferences { Display = "days" },
                ct: cancellationToken);

            properties["cage"] = ToAgeDictionary(
                await _deviceAgeService.GetCannulaAgeAsync(prefs, ct: cancellationToken));
            properties["sage"] = new Dictionary<string, object?>
            {
                ["Sensor Start"] = ToSensorDictionary(sensor.SensorStart, sensor.Min == "Sensor Start"),
                ["Sensor Change"] = ToSensorDictionary(sensor.SensorChange, sensor.Min == "Sensor Change"),
                ["min"] = sensor.Min,
            };
            properties["iage"] = ToAgeDictionary(
                await _deviceAgeService.GetInsulinAgeAsync(prefs, ct: cancellationToken));
            properties["bage"] = ToAgeDictionary(
                await _deviceAgeService.GetBatteryAgeAsync(prefs, ct: cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting device age properties");
            // Don't throw, just skip setting device age properties
        }
    }

    /// <summary>
    /// Projects a <see cref="DeviceAgeInfo"/> into a dictionary so every field reaches the
    /// wire. The Nightscout response filter serializes with
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault"/>,
    /// which drops zero/false properties off typed objects — that would hide
    /// <c>age</c>/<c>days</c>/<c>level</c> exactly when a site was just changed, and never
    /// emit <c>found: false</c>. Dictionary values are exempt from that condition.
    /// </summary>
    private static Dictionary<string, object?> ToAgeDictionary(DeviceAgeInfo age)
    {
        if (!age.Found)
        {
            return new()
            {
                ["found"] = false,
                ["age"] = 0,
                ["treatmentDate"] = null,
                ["checkForAlert"] = false,
                ["level"] = age.Level,
                // NIGHTSCOUT-COMPAT: Nightscout's trailing space.
                ["display"] = "n/a ",
            };
        }

        var result = FoundAgeFields(age);
        result["checkForAlert"] = false;
        result["level"] = age.Level;
        result["display"] = age.Display;
        if (age.Notification is not null)
            result["notification"] = age.Notification;
        return result;
    }

    /// <summary>
    /// Projects one sage entry. Nightscout sets <c>level</c> and <c>notification</c> only on the
    /// entry named by <c>min</c>, and gives an entry with nothing found no other field.
    /// </summary>
    private static Dictionary<string, object?> ToSensorDictionary(DeviceAgeInfo age, bool isMin)
    {
        var result = age.Found ? FoundAgeFields(age) : new Dictionary<string, object?> { ["found"] = false };
        if (age.Found)
        {
            result["display"] = age.Display;
            result["displayLong"] = age.Age >= 24
                ? $"{age.Days} days {age.Hours} hours"
                : $"{age.Hours} hours";
        }

        if (!isMin)
            return result;

        result["level"] = age.Level;
        if (age.Notification is not null)
            result["notification"] = age.Notification;
        return result;
    }

    private static Dictionary<string, object?> FoundAgeFields(DeviceAgeInfo age)
    {
        var fields = new Dictionary<string, object?>
        {
            ["found"] = true,
            ["treatmentDate"] = age.TreatmentDate,
            ["age"] = age.Age,
            ["days"] = age.Days,
            ["hours"] = age.Hours,
            ["minFractions"] = age.MinFractions,
        };
        if (age.Notes is not null)
            fields["notes"] = age.Notes;
        return fields;
    }

    /// <summary>
    /// Set profile properties
    /// </summary>
    private void SetProfileProperties(Dictionary<string, object> properties, DData ddata)
    {
        var currentProfile = ddata.Profiles?.OrderByDescending(p => p.Mills).FirstOrDefault();
        if (currentProfile == null)
            return;

        properties["profile"] = new Dictionary<string, object?>
        {
            ["name"] = currentProfile.DefaultProfile ?? "Default",
            ["mills"] = currentProfile.Mills,
            ["created_at"] = currentProfile.CreatedAt,
        };
    }

    /// <summary>
    /// Set basal properties - exact 1:1 match with legacy basalprofile.js
    /// Provides current basal rate information including temp basal and combo bolus treatments
    /// </summary>
    private async Task SetBasalPropertiesAsync(
        Dictionary<string, object> properties,
        DData ddata,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("SetBasalProperties: Starting");
            var profile = ddata.Profiles?.OrderByDescending(p => p.Mills).FirstOrDefault();
            _logger.LogDebug("SetBasalProperties: Profile found: {ProfileFound}", profile != null);
            if (profile?.Store == null)
            {
                _logger.LogDebug("SetBasalProperties: No profile or store, returning");
                return;
            }

            var defaultProfile = profile.DefaultProfile ?? "Default";
            _logger.LogDebug("SetBasalProperties: Default profile: {DefaultProfile}", defaultProfile);
            if (
                !profile.Store.TryGetValue(defaultProfile, out var profileData)
                || profileData?.Basal == null
            )
            {
                _logger.LogDebug("SetBasalProperties: No profile data or basal, returning");
                return;
            }

            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var treatments = ddata.Treatments ?? new List<Treatment>();
            _logger.LogDebug("SetBasalProperties: Now: {Now}, Treatments count: {TreatmentCount}", now, treatments.Count);

            var zone = TimeZoneHelper.GetTimeZoneInfoFromId(
                await _therapySettings.GetTimezoneAsync(ct: cancellationToken));
            var tempBasalResult = CalculateTempBasal(now, profileData, treatments, zone);
            _logger.LogDebug("SetBasalProperties: TempBasal result: Basal={Basal}, TotalBasal={TotalBasal}", tempBasalResult.Basal, tempBasalResult.TotalBasal);

            // Build temp markers like legacy: T for temp basal, C for combo bolus
            var tempMark = "";
            tempMark += tempBasalResult.Treatment != null ? "T" : "";
            tempMark += tempBasalResult.ComboBolusTreatment != null ? "C" : "";
            tempMark += !string.IsNullOrEmpty(tempMark) ? ": " : "";

            var displayValue = $"{tempMark}{tempBasalResult.TotalBasal:F3}U";
            _logger.LogDebug("SetBasalProperties: Display value: {DisplayValue}", displayValue);

            properties["basal"] = new Dictionary<string, object>
            {
                ["display"] = displayValue,
                ["current"] = new Dictionary<string, object>
                {
                    ["basal"] = tempBasalResult.Basal,
                    ["treatment"] = tempBasalResult.Treatment ?? (object)null!,
                    ["combobolustreatment"] = tempBasalResult.ComboBolusTreatment ?? (object)null!,
                    ["totalbasal"] = tempBasalResult.TotalBasal,
                },
            };
            _logger.LogDebug("SetBasalProperties: Basal property set successfully");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "SetBasalProperties failed");
            // Don't throw, just skip setting basal properties
        }
    }

    /// <summary>
    /// Calculate temp basal result - matches legacy profile.getTempBasal() logic exactly
    /// </summary>
    private TempBasalResult CalculateTempBasal(
        long time,
        ProfileData profileData,
        List<Treatment> treatments,
        TimeZoneInfo zone
    )
    {
        var result = new TempBasalResult();

        // Get base basal rate from profile for the current time
        result.Basal = GetBasalRateAtTime(time, profileData.Basal, zone);
        result.TotalBasal = result.Basal;

        // Find active temp basal treatment - exact legacy search logic
        var tempBasalTreatment = treatments
            .Where(t =>
                !string.IsNullOrEmpty(t.EventType)
                && (
                    t.EventType.Equals("Temp Basal", StringComparison.OrdinalIgnoreCase)
                    || t.EventType.Equals("temp basal", StringComparison.OrdinalIgnoreCase)
                )
                && t.Mills <= time
                && t.Mills + (t.Duration ?? 0) * 60000 > time
            ) // Duration in minutes, convert to ms
            .OrderByDescending(t => t.Mills)
            .FirstOrDefault();

        if (tempBasalTreatment != null)
        {
            result.Treatment = tempBasalTreatment;

            if (tempBasalTreatment.Absolute is { } absolute)
                result.TempBasal = absolute;
            else if (tempBasalTreatment.Percent is { } percent and not 0)
                result.TempBasal = Nocturne.Core.Models.V4.TempBasal.RateRelativeTo(result.Basal, percent);
            else
                result.TempBasal = result.Basal;

            result.TotalBasal = result.TempBasal;
        }

        // Find active combo bolus treatment - exact legacy search logic
        var comboBolusTreatment = treatments
            .Where(t =>
                !string.IsNullOrEmpty(t.EventType)
                && (
                    t.EventType.Equals("Combo Bolus", StringComparison.OrdinalIgnoreCase)
                    || t.EventType.Equals("combo bolus", StringComparison.OrdinalIgnoreCase)
                )
                && t.Mills <= time
                && t.Mills + (t.Duration ?? 0) * 60000 > time
            ) // Duration in minutes, convert to ms
            .OrderByDescending(t => t.Mills)
            .FirstOrDefault();

        if (comboBolusTreatment != null)
        {
            result.ComboBolusTreatment = comboBolusTreatment;

            // Add combo bolus basal component - exact legacy logic
            if (comboBolusTreatment.Relative != null)
            {
                result.ComboBolusBasal = comboBolusTreatment.Relative.Value;
                result.TotalBasal += result.ComboBolusBasal;
            }
        }

        return result;
    }

    private static double GetBasalRateAtTime(long time, List<TimeValue> basalSchedule, TimeZoneInfo zone)
    {
        var entries = basalSchedule
            .Select(b => new ScheduleEntry { Time = b.Time, Value = b.Value, TimeAsSeconds = b.TimeAsSeconds })
            .ToList();
        return ScheduleResolution.FindValueAtTime(entries, ScheduleEntryTime.SecondsOfDay(time, zone))
            ?? 0.0;
    }

    /// <summary>
    /// Set database size properties
    /// </summary>
    private void SetDbSizeProperties(Dictionary<string, object> properties, DData ddata)
    {
        var dbStats = ddata.DbStats;
        if (dbStats == null)
            return;

        var totalSize = dbStats.DataSize + dbStats.IndexSize;
        var percentage = totalSize > 0 ? (int)(totalSize * 100.0 / _dbSize.MaxBytes) : 0;

        properties["dbsize"] = new Dictionary<string, object>
        {
            ["size"] = totalSize,
            ["percentage"] = percentage,
            ["display"] = $"{percentage}%",
            // NIGHTSCOUT-COMPAT: Nightscout's dbsize plugin warns from 60% and is urgent from 75% of the floored percentage.
            ["status"] =
                percentage >= 75 ? "urgent"
                : percentage >= 60 ? "warn"
                : "ok",
        };
    }

    /// <summary>
    /// Set runtime state properties
    /// </summary>
    private void SetRuntimeStateProperties(Dictionary<string, object> properties)
    {
        properties["runtimestate"] = new Dictionary<string, object> { ["state"] = "ok" };
    }
}
