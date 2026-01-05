using System.IO;
using System.Linq;
using System.Reflection;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;

namespace Nocturne.API.Services;

/// <summary>
/// Service implementation for status operations with 1:1 Nightscout compatibility
/// </summary>
public class StatusService : IStatusService
{
    private readonly IConfiguration _configuration;
    private readonly ICacheService _cacheService;
    private readonly IDemoModeService _demoModeService;
    private readonly ILogger<StatusService> _logger;

    public StatusService(
        IConfiguration configuration,
        ICacheService cacheService,
        IDemoModeService demoModeService,
        ILogger<StatusService> logger
    )
    {
        _configuration = configuration;
        _cacheService = cacheService;
        _demoModeService = demoModeService;
        _logger = logger;
    }

    /// <summary>
    /// Get the current system status with Nightscout-compatible response
    /// </summary>
    public async Task<StatusResponse> GetSystemStatusAsync()
    {
        // Include demo mode in cache key to ensure correct status is returned
        var demoSuffix = _demoModeService.IsEnabled ? ":demo" : "";
        var cacheKey = "status:system" + demoSuffix;
        var cacheTtl = TimeSpan.FromMinutes(2);

        var cachedStatus = await _cacheService.GetAsync<StatusResponse>(cacheKey);
        if (cachedStatus != null)
        {
            _logger.LogDebug(
                "Cache HIT for system status (demoMode: {DemoMode})",
                _demoModeService.IsEnabled
            );
            return cachedStatus;
        }

        _logger.LogDebug(
            "Cache MISS for system status (demoMode: {DemoMode}), generating response",
            _demoModeService.IsEnabled
        );
        var status = await GenerateSystemStatusAsync();

        if (status != null)
        {
            await _cacheService.SetAsync(cacheKey, status, cacheTtl);
            _logger.LogDebug("Cached system status with {TTL}min TTL", cacheTtl.TotalMinutes);
            return status;
        }

        // Return a default status if generation fails
        return new StatusResponse
        {
            Status = "error",
            Name = "Nocturne",
            Version = "unknown",
            ServerTime = DateTime.UtcNow,
            ApiEnabled = true,
        };
    }

    /// <summary>
    /// Generate the system status response (private method for cache factory)
    /// </summary>
    private async Task<StatusResponse> GenerateSystemStatusAsync()
    {
        _logger.LogDebug("Generating system status response");

        var version = GetVersionString();
        var serverTime = DateTime.UtcNow;
        var settings = await GetPublicSettingsAsync();

        // Add demo mode settings if enabled
        if (_demoModeService.IsEnabled)
        {
            settings["demoMode"] = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["realTimeUpdates"] = true,
                ["showDemoIndicators"] = true,
            };
            settings["runtimeState"] = "demo";
        }

        var response = new StatusResponse
        {
            Status = "ok",
            Name = _configuration[ServiceNames.ConfigKeys.NightscoutSiteName] ?? "Nocturne",
            Version = version,
            ServerTime = serverTime,
            ApiEnabled = true,
            CareportalEnabled = _configuration.GetValue<bool>("Features:CareportalEnabled", true),
            Head = GetGitCommitHash(),
            Settings = settings,
        };

        _logger.LogDebug(
            "Status response generated for site: {SiteName}, version: {Version}, demoMode: {DemoMode}",
            response.Name,
            response.Version,
            _demoModeService.IsEnabled
        );

        return response;
    }

    /// <summary>
    /// Get the application version string
    /// </summary>
    private static string GetVersionString()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version?.ToString() ?? "1.0.0";
    }

    /// <summary>
    /// Get the Git commit hash for the head property
    /// </summary>
    private static string GetGitCommitHash()
    {
        var envCommit = Environment.GetEnvironmentVariable("GIT_COMMIT");
        if (!string.IsNullOrWhiteSpace(envCommit))
        {
            return envCommit;
        }

        try
        {
            var gitDirectory = FindGitDirectory(AppContext.BaseDirectory);
            if (gitDirectory == null)
            {
                return "nocturne-dev";
            }

            var commitHash = ReadCommitFromGitDirectory(gitDirectory);
            return string.IsNullOrWhiteSpace(commitHash) ? "nocturne-dev" : commitHash;
        }
        catch (Exception)
        {
            return "nocturne-dev";
        }
    }

    /// <summary>
    /// Locate the nearest .git directory starting from a base path.
    /// </summary>
    private static string? FindGitDirectory(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        var directoryInfo = new DirectoryInfo(startDirectory);

        while (directoryInfo != null)
        {
            var gitPath = Path.Combine(directoryInfo.FullName, ".git");

            if (Directory.Exists(gitPath))
            {
                return gitPath;
            }

            if (File.Exists(gitPath))
            {
                var pointerLine = File.ReadLines(gitPath).FirstOrDefault()?.Trim();
                const string gitDirPrefix = "gitdir:";

                if (
                    !string.IsNullOrWhiteSpace(pointerLine)
                    && pointerLine.StartsWith(gitDirPrefix, StringComparison.OrdinalIgnoreCase)
                )
                {
                    var gitDir = pointerLine.Substring(gitDirPrefix.Length).Trim();
                    var resolvedPath = Path.IsPathRooted(gitDir)
                        ? gitDir
                        : Path.GetFullPath(Path.Combine(directoryInfo.FullName, gitDir));

                    if (Directory.Exists(resolvedPath))
                    {
                        return resolvedPath;
                    }
                }
            }

            directoryInfo = directoryInfo.Parent;
        }

        return null;
    }

    /// <summary>
    /// Read the commit hash referenced by the HEAD file.
    /// </summary>
    private static string? ReadCommitFromGitDirectory(string gitDirectory)
    {
        var headPath = Path.Combine(gitDirectory, "HEAD");
        if (!File.Exists(headPath))
        {
            return null;
        }

        var headContent = File.ReadAllText(headPath).Trim();
        if (string.IsNullOrWhiteSpace(headContent))
        {
            return null;
        }

        if (headContent.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
        {
            var reference = headContent["ref:".Length..].Trim();
            var refPath = Path.Combine(
                gitDirectory,
                reference.Replace('/', Path.DirectorySeparatorChar)
            );

            if (File.Exists(refPath))
            {
                var commitFromRef = File.ReadAllText(refPath).Trim();
                return string.IsNullOrWhiteSpace(commitFromRef) ? null : commitFromRef;
            }

            var packedRefsPath = Path.Combine(gitDirectory, "packed-refs");
            if (File.Exists(packedRefsPath))
            {
                foreach (var line in File.ReadLines(packedRefsPath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    {
                        continue;
                    }

                    var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (
                        parts.Length == 2
                        && string.Equals(parts[1].Trim(), reference, StringComparison.Ordinal)
                    )
                    {
                        return parts[0].Trim();
                    }
                }
            }

            return null;
        }

        return headContent;
    }

    /// <summary>
    /// Get public settings that are safe to expose to clients
    /// </summary>
    private async Task<Dictionary<string, object>> GetPublicSettingsAsync()
    {
        var settings = new Dictionary<string, object>();

        // Core display settings
        settings["units"] = _configuration[ServiceNames.ConfigKeys.DisplayUnits] ?? "mg/dl";
        settings["timeFormat"] = _configuration.GetValue<int>("Display:TimeFormat", 12);
        settings["nightMode"] = _configuration.GetValue<bool>("Display:NightMode", false);
        settings["editMode"] = _configuration.GetValue<bool>("Display:EditMode", true);
        settings["showRawbg"] = _configuration[ServiceNames.ConfigKeys.DisplayShowRawBG] ?? "never";
        settings["customTitle"] = _configuration[ServiceNames.ConfigKeys.DisplayCustomTitle] ?? "";
        settings["theme"] = _configuration[ServiceNames.ConfigKeys.DisplayTheme] ?? "default";

        // Feature enablement
        settings["enable"] = GetEnabledFeatures();
        settings["showPlugins"] = _configuration[ServiceNames.ConfigKeys.DisplayShowPlugins] ?? "";
        settings["showForecast"] =
            _configuration[ServiceNames.ConfigKeys.DisplayShowForecast] ?? "";

        // Alarm settings (public subset)
        settings["alarmUrgentHigh"] = _configuration.GetValue<bool>(
            "Alarms:UrgentHigh:Enabled",
            true
        );
        settings["alarmHigh"] = _configuration.GetValue<bool>("Alarms:High:Enabled", true);
        settings["alarmLow"] = _configuration.GetValue<bool>("Alarms:Low:Enabled", true);
        settings["alarmUrgentLow"] = _configuration.GetValue<bool>(
            "Alarms:UrgentLow:Enabled",
            true
        );
        settings["alarmTimeagoWarn"] = _configuration.GetValue<bool>(
            "Alarms:TimeAgoWarn:Enabled",
            true
        );
        settings["alarmTimeagoUrgent"] = _configuration.GetValue<bool>(
            "Alarms:TimeAgoUrgent:Enabled",
            true
        );

        // Threshold values
        settings["thresholds"] = new Dictionary<string, object>
        {
            ["bgHigh"] = _configuration.GetValue<int>("Thresholds:BgHigh", 260),
            ["bgTargetTop"] = _configuration.GetValue<int>("Thresholds:BgTargetTop", 180),
            ["bgTargetBottom"] = _configuration.GetValue<int>("Thresholds:BgTargetBottom", 80),
            ["bgLow"] = _configuration.GetValue<int>("Thresholds:BgLow", 55),
        };

        // Language and localization
        settings["language"] = _configuration[ServiceNames.ConfigKeys.LocalizationLanguage] ?? "en";
        settings["scaleY"] = _configuration[ServiceNames.ConfigKeys.DisplayScaleY] ?? "log";

        // Default features that are typically enabled
        if (!settings.ContainsKey("enable") || string.IsNullOrEmpty(settings["enable"]?.ToString()))
        {
            settings["enable"] =
                "careportal basal dbsize rawbg iob maker bridge cob bwp cage iage sage boluscalc pushover treatmentnotify mmconnect loop pump profile food openaps bage alexa override cors";
        }

        await Task.CompletedTask; // For future async operations

        return settings;
    }

    /// <summary>
    /// Get the list of enabled features/plugins
    /// </summary>
    private string GetEnabledFeatures()
    {
        var enabledFeatures = _configuration[ServiceNames.ConfigKeys.FeaturesEnable];
        if (!string.IsNullOrEmpty(enabledFeatures))
        {
            return enabledFeatures;
        }

        // Default enabled features to match Nightscout behavior
        return "careportal basal dbsize rawbg iob maker bridge cob bwp cage iage sage boluscalc pushover treatmentnotify mmconnect loop pump profile food openaps bage alexa override cors";
    }

    /// <summary>
    /// Get the current system status with extended V3 information
    /// </summary>
    public async Task<V3StatusResponse> GetV3SystemStatusAsync()
    {
        _logger.LogDebug("Generating V3 system status response");

        var basicStatus = await GetSystemStatusAsync();
        var startTime = Environment.TickCount64;

        var response = new V3StatusResponse
        {
            Status = basicStatus.Status,
            Name = basicStatus.Name ?? "Nocturne",
            Version = basicStatus.Version ?? "unknown",
            ServerTime = basicStatus.ServerTime,
            ApiEnabled = basicStatus.ApiEnabled,
            CareportalEnabled = basicStatus.CareportalEnabled ?? false,
            Head = basicStatus.Head ?? "unknown",
            Settings = basicStatus.Settings ?? new Dictionary<string, object>(),
            Extended = new ExtendedStatusInfo
            {
                Authorization = GetAuthorizationInfo(),
                Permissions = GetApiPermissions(),
                UptimeMs = Environment.TickCount64 - startTime,
                Collections = GetAvailableCollections(),
                ApiVersions = GetSupportedApiVersions(),
            },
        };

        _logger.LogDebug("V3 status response generated with extended information");

        return response;
    }

    /// <summary>
    /// Get last modified timestamps for all collections
    /// </summary>
    public async Task<LastModifiedResponse> GetLastModifiedAsync()
    {
        _logger.LogDebug("Generating last modified timestamps response");

        var serverTime = DateTime.UtcNow;

        var response = new LastModifiedResponse
        {
            ServerTime = serverTime,
            // For now, return current time as placeholder
            // In a real implementation, these would come from database queries
            Entries = serverTime.AddMinutes(-5),
            Treatments = serverTime.AddMinutes(-10),
            Profile = serverTime.AddHours(-1),
            DeviceStatus = serverTime.AddMinutes(-2),
            Food = serverTime.AddDays(-1),
            Settings = serverTime.AddHours(-6),
            Activity = serverTime.AddMinutes(-30),
            Additional = new Dictionary<string, DateTime>
            {
                ["auth"] = serverTime.AddDays(-7),
                ["notifications"] = serverTime.AddMinutes(-15),
            },
        };

        _logger.LogDebug("Last modified response generated");

        await Task.CompletedTask; // For future async database operations

        return response;
    }

    /// <summary>
    /// Get authorization information for the current request
    /// </summary>
    private static AuthorizationInfo GetAuthorizationInfo()
    {
        // For now, return basic authorization info
        // In a real implementation, this would check the current request context
        return new AuthorizationInfo
        {
            IsAuthorized = true, // Default to authorized for now
            Scope = new List<string> { "api:*:read", "api:entries:read", "api:treatments:read" },
            Subject = null, // No authenticated subject for now
            Roles = new List<string> { "readable" },
        };
    }

    /// <summary>
    /// Get API permissions matrix
    /// </summary>
    private static Dictionary<string, bool> GetApiPermissions()
    {
        return new Dictionary<string, bool>
        {
            ["entries:read"] = true,
            ["entries:write"] = false, // Conservative default
            ["treatments:read"] = true,
            ["treatments:write"] = false,
            ["profile:read"] = true,
            ["profile:write"] = false,
            ["devicestatus:read"] = true,
            ["devicestatus:write"] = false,
            ["food:read"] = true,
            ["food:write"] = false,
            ["settings:read"] = true,
            ["settings:write"] = false,
            ["admin"] = false,
        };
    }

    /// <summary>
    /// Get list of available API collections
    /// </summary>
    private static List<string> GetAvailableCollections()
    {
        return new List<string>
        {
            "entries",
            "treatments",
            "profile",
            "devicestatus",
            "food",
            "settings",
            "activity",
        };
    }

    /// <summary>
    /// Get supported API versions matrix
    /// </summary>
    private static Dictionary<string, bool> GetSupportedApiVersions()
    {
        return new Dictionary<string, bool>
        {
            ["v1"] = true,
            ["v2"] = false, // Not implemented yet
            ["v3"] = true, // Partially implemented
        };
    }
}
