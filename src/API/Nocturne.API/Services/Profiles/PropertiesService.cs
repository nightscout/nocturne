using System.Text.Json;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
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
    private readonly IIobService _iobService;
    private readonly ICobService _cobService;
    private readonly IAr2Service _ar2Service;

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
        IIobService iobService,
        ICobService cobService,
        IAr2Service ar2Service
    )
    {
        _ddataService = ddataService;
        _logger = logger;
        _iobService = iobService;
        _cobService = cobService;
        _ar2Service = ar2Service;
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
            var allProperties = await BuildSandboxPropertiesAsync(cancellationToken);
            var filteredProperties = new Dictionary<string, object>();

            foreach (var propertyName in propertyNames)
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
    private async Task<Dictionary<string, object>> BuildSandboxPropertiesAsync(
        CancellationToken cancellationToken
    )
    {
        var properties = new Dictionary<string, object>();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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

            // IOB properties (Insulin on Board) - requires treatments and profile data
            await SetIobPropertiesAsync(properties, ddata);

            // COB properties (Carbs on Board) - requires treatments and profile data
            await SetCobPropertiesAsync(properties, ddata);

            // Device status properties
            SetDeviceStatusProperties(properties, ddata);

            // AR2 properties (prediction algorithm)
            await SetAr2PropertiesAsync(properties, ddata);

            // Raw BG properties
            SetRawBgProperties(properties, ddata);

            // Battery properties
            SetBatteryProperties(properties, ddata);

            // Profile properties
            SetProfileProperties(properties, ddata);

            // Basal properties
            SetBasalProperties(properties, ddata);

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
    /// Set BGNow properties from the most recent glucose entry
    /// </summary>
    private void SetBgNowPropertiesAsync(Dictionary<string, object> properties, DData ddata)
    {
        var sgvs = ddata.Sgvs?.OrderByDescending(s => s.Mills).ToList();
        if (sgvs?.Any() != true)
            return;

        var currentSgv = sgvs.First();
        var mgdlValue = currentSgv.Mgdl != 0 ? currentSgv.Mgdl : currentSgv.Sgv ?? 0;

        properties["bgnow"] = new Dictionary<string, object>
        {
            ["sgvs"] = new[] { currentSgv },
            ["mgdl"] = mgdlValue,
            ["scaled"] = currentSgv.Scaled ?? mgdlValue,
            ["mills"] = currentSgv.Mills,
            ["displayLine"] = $"BG Now: {currentSgv.Scaled ?? mgdlValue} mg/dl",
        };
    }

    /// <summary>
    /// Set delta properties showing glucose change - exact legacy algorithm
    /// </summary>
    private void SetDeltaProperties(Dictionary<string, object> properties, DData ddata)
    {
        var entries = ddata.Sgvs?.OrderByDescending(s => s.Mills).Take(10).ToList();
        if (entries?.Count < 2)
            return;

        // Use the DirectionService for exact legacy delta calculation
        var deltaInfo = DirectionService.CalculateDelta(entries!, "mg/dl");
        if (deltaInfo == null)
            return;

        properties["delta"] = new Dictionary<string, object>
        {
            ["display"] = deltaInfo.Display ?? "",
            ["mgdl"] = deltaInfo.Mgdl ?? 0,
            ["scaled"] = deltaInfo.Scaled ?? 0,
            ["interpolated"] = deltaInfo.Interpolated,
            ["absolute"] = deltaInfo.Absolute,
            ["elapsedMins"] = deltaInfo.ElapsedMins ?? 0,
            ["mean5MinsAgo"] = deltaInfo.Mean5MinsAgo ?? 0,
            ["previous"] = deltaInfo.Previous ?? (object)null!,
            ["current"] = deltaInfo.Current ?? (object)null!,
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

    /// <summary>
    /// Set IOB (Insulin on Board) properties using full legacy algorithm
    /// </summary>
    private async Task SetIobPropertiesAsync(Dictionary<string, object> properties, DData ddata)
    {
        try
        {
            var treatments = ddata.Treatments?.ToList() ?? new List<Treatment>();

            if (!treatments.Any())
                return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var iobResult = await _iobService.CalculateTotalAsync(
                treatments,
                now
            );

            if (iobResult == null)
                return;

            properties["iob"] = new Dictionary<string, object?>
            {
                ["iob"] = Math.Round(iobResult.Iob, 2),
                ["displayLine"] = iobResult.DisplayLine ?? $"IOB: {Math.Round(iobResult.Iob, 2)}U",
                ["timestamp"] = now,
                ["source"] = iobResult.Source ?? "Care Portal",
                ["activity"] = iobResult.Activity,
                ["lastBolus"] = iobResult.LastBolus?.Mills,
                ["basalIob"] = iobResult.BasalIob,
                ["treatmentIob"] = iobResult.TreatmentIob,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting IOB properties");
            // Don't throw, just skip setting IOB properties
        }
    }

    /// <summary>
    /// Set COB (Carbs on Board) properties using full legacy algorithm
    /// </summary>
    private async Task SetCobPropertiesAsync(Dictionary<string, object> properties, DData ddata)
    {
        try
        {
            _logger.LogDebug("SetCobProperties: Starting");
            var treatments = ddata.Treatments?.ToList() ?? new List<Treatment>();

            if (!treatments.Any())
            {
                _logger.LogDebug("SetCobProperties: No treatments, returning");
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Use full COB calculation service - NO SIMPLIFICATIONS
            _logger.LogDebug("SetCobProperties: Calling COB service");
            var cobResult = await _cobService.CobTotalAsync(treatments, now);
            _logger.LogDebug("SetCobProperties: COB service returned result, is null: {IsNull}", cobResult == null);

            if (cobResult == null)
            {
                _logger.LogDebug("SetCobProperties: COB result is null, returning");
                return;
            }

            properties["cob"] = new Dictionary<string, object?>
            {
                ["cob"] = Math.Round(cobResult.Cob),
                ["displayLine"] = cobResult.DisplayLine ?? $"COB: {Math.Round(cobResult.Cob)}g",
                ["timestamp"] = now,
                ["source"] = cobResult.Source ?? "Care Portal",
                ["activity"] = cobResult.Activity,
                ["treatments"] = cobResult
                    .Treatments?.Select(t => new { t.Mills, t.Carbs })
                    .ToList(),
            };
            _logger.LogDebug("SetCobProperties: COB property set successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SetCobProperties failed");
            // Don't throw, just skip setting COB properties
        }
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
                ["bgTargetTop"] = 180,
                ["bgTargetBottom"] = 80,
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

        var mgdlValue = rawEntry.Mgdl != 0 ? rawEntry.Mgdl : rawEntry.Sgv ?? 0;

        properties["rawbg"] = new Dictionary<string, object?>
        {
            ["mgdl"] = mgdlValue,
            ["noise"] = rawEntry.Noise,
            ["displayLine"] = $"Raw BG: {mgdlValue} mg/dl",
        };
    }

    /// <summary>
    /// Set battery level properties
    /// </summary>
    private void SetBatteryProperties(Dictionary<string, object> properties, DData ddata)
    {
        var deviceStatus = ddata.DeviceStatus?.OrderByDescending(d => d.Mills).FirstOrDefault();
        var batteryLevel = deviceStatus?.Uploader?.Battery;

        if (!batteryLevel.HasValue)
            return;

        properties["upbat"] = new Dictionary<string, object>
        {
            ["level"] = batteryLevel.Value,
            ["displayLine"] = $"Battery: {batteryLevel}%",
        };
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
    private void SetBasalProperties(Dictionary<string, object> properties, DData ddata)
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

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var treatments = ddata.Treatments ?? new List<Treatment>();
            _logger.LogDebug("SetBasalProperties: Now: {Now}, Treatments count: {TreatmentCount}", now, treatments.Count);

            // Calculate current basal rate with temp treatments - exact legacy algorithm
            var tempBasalResult = CalculateTempBasal(now, profileData, treatments);
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
        catch (Exception ex)
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
        List<Treatment> treatments
    )
    {
        var result = new TempBasalResult();

        // Get base basal rate from profile for the current time
        result.Basal = GetBasalRateAtTime(time, profileData.Basal);
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

            // Calculate temp basal rate - exact legacy logic
            if (tempBasalTreatment.Percent != null)
            {
                // Percentage-based temp basal: percent field contains the target percentage (e.g., 150 for 150%)
                result.TempBasal = result.Basal * tempBasalTreatment.Percent.Value / 100;
            }
            else if (tempBasalTreatment.Absolute != null)
            {
                // Absolute temp basal rate
                result.TempBasal = tempBasalTreatment.Absolute.Value;
            }
            else
            {
                result.TempBasal = result.Basal;
            }

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

    /// <summary>
    /// Get basal rate at specific time from profile - exact legacy algorithm
    /// </summary>
    private double GetBasalRateAtTime(long time, List<TimeValue>? basalSchedule)
    {
        if (basalSchedule == null || !basalSchedule.Any())
            return 0.0;

        // Convert timestamp to time of day in seconds
        var dateTime = DateTimeOffset.FromUnixTimeMilliseconds(time);
        var timeOfDaySeconds = (int)dateTime.TimeOfDay.TotalSeconds;

        // Find the appropriate basal rate - exact legacy lookup logic
        var applicableRate = basalSchedule
            .Where(b => b.TimeAsSeconds.HasValue && b.TimeAsSeconds.Value <= timeOfDaySeconds)
            .OrderByDescending(b => b.TimeAsSeconds)
            .FirstOrDefault();

        return applicableRate?.Value ?? basalSchedule.FirstOrDefault()?.Value ?? 0.0;
    }

    /// <summary>
    /// Set database size properties
    /// </summary>
    private void SetDbSizeProperties(Dictionary<string, object> properties, DData ddata)
    {
        var dbStats = ddata.DbStats;
        if (dbStats == null)
            return;

        // Simplified database size calculation
        var totalSize = dbStats.DataSize; // Only dataSize, no indexSize in our model
        var maxSize = 500 * 1024 * 1024; // 500MB default max
        var percentage = totalSize > 0 ? (int)((totalSize / (double)maxSize) * 100) : 0;

        properties["dbsize"] = new Dictionary<string, object>
        {
            ["size"] = totalSize,
            ["percentage"] = percentage,
            ["display"] = $"{percentage}%",
            ["status"] =
                percentage > 80 ? "urgent"
                : percentage > 60 ? "warn"
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
