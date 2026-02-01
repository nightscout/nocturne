using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Extensions;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services;

/// <summary>
/// Service implementation for status operations with 1:1 Nightscout compatibility
/// </summary>
public class StatusService : IStatusService
{
    private readonly IConfiguration _configuration;
    private readonly ICacheService _cacheService;
    private readonly IDemoModeService _demoModeService;
    private readonly NocturneDbContext _dbContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<StatusService> _logger;

    public StatusService(
        IConfiguration configuration,
        ICacheService cacheService,
        IDemoModeService demoModeService,
        NocturneDbContext dbContext,
        IHttpContextAccessor httpContextAccessor,
        ILogger<StatusService> logger
    )
    {
        _configuration = configuration;
        _cacheService = cacheService;
        _demoModeService = demoModeService;
        _dbContext = dbContext;
        _httpContextAccessor = httpContextAccessor;
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
            Name = _configuration[ServiceNames.ConfigKeys.NightscoutSiteName] ?? "nightscout",
            Version = version,
            ServerTime = serverTime,
            ApiEnabled = true,
            CareportalEnabled = _configuration.GetValue<bool>("Features:CareportalEnabled", true),
            BoluscalcEnabled = _configuration.GetValue<bool>("Features:BoluscalcEnabled", false),
            Head = GetGitCommitHash(),
            Settings = settings,
            ExtendedSettings = GetExtendedSettings(),
            Authorized = null, // Nightscout returns null for unauthenticated requests
            RuntimeState = _demoModeService.IsEnabled ? "demo" : "loaded",
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
        settings["dayStart"] = _configuration.GetValue<int>("Display:DayStart", 7);
        settings["dayEnd"] = _configuration.GetValue<int>("Display:DayEnd", 21);
        settings["nightMode"] = _configuration.GetValue<bool>("Display:NightMode", false);
        settings["editMode"] = _configuration.GetValue<bool>("Display:EditMode", true);
        settings["showRawbg"] = _configuration[ServiceNames.ConfigKeys.DisplayShowRawBG] ?? "never";
        settings["customTitle"] = _configuration[ServiceNames.ConfigKeys.DisplayCustomTitle] ?? "Nightscout";
        settings["theme"] = _configuration[ServiceNames.ConfigKeys.DisplayTheme] ?? "default";

        // Alarm boolean settings
        settings["alarmUrgentHigh"] = _configuration.GetValue<bool>("Alarms:UrgentHigh:Enabled", true);
        settings["alarmHigh"] = _configuration.GetValue<bool>("Alarms:High:Enabled", true);
        settings["alarmLow"] = _configuration.GetValue<bool>("Alarms:Low:Enabled", true);
        settings["alarmUrgentLow"] = _configuration.GetValue<bool>("Alarms:UrgentLow:Enabled", true);
        settings["alarmTimeagoWarn"] = _configuration.GetValue<bool>("Alarms:TimeAgoWarn:Enabled", true);
        settings["alarmTimeagoUrgent"] = _configuration.GetValue<bool>("Alarms:TimeAgoUrgent:Enabled", true);
        settings["alarmPumpBatteryLow"] = _configuration.GetValue<bool>("Alarms:PumpBatteryLow", false);

        // Alarm minute arrays - Nightscout defaults
        settings["alarmUrgentHighMins"] = _configuration.GetSection("Alarms:UrgentHighMins").Get<int[]>() ?? new[] { 30, 60, 90, 120 };
        settings["alarmHighMins"] = _configuration.GetSection("Alarms:HighMins").Get<int[]>() ?? new[] { 30, 60, 90, 120 };
        settings["alarmLowMins"] = _configuration.GetSection("Alarms:LowMins").Get<int[]>() ?? new[] { 15, 30, 45, 60 };
        settings["alarmUrgentLowMins"] = _configuration.GetSection("Alarms:UrgentLowMins").Get<int[]>() ?? new[] { 15, 30, 45 };
        settings["alarmUrgentMins"] = _configuration.GetSection("Alarms:UrgentMins").Get<int[]>() ?? new[] { 30, 60, 90, 120 };
        settings["alarmWarnMins"] = _configuration.GetSection("Alarms:WarnMins").Get<int[]>() ?? new[] { 30, 60, 90, 120 };
        settings["alarmTimeagoWarnMins"] = _configuration.GetValue<int>("Alarms:TimeAgoWarnMins", 15);
        settings["alarmTimeagoUrgentMins"] = _configuration.GetValue<int>("Alarms:TimeAgoUrgentMins", 30);

        // Language and display settings
        settings["language"] = _configuration[ServiceNames.ConfigKeys.LocalizationLanguage] ?? "en";
        settings["scaleY"] = _configuration[ServiceNames.ConfigKeys.DisplayScaleY] ?? "log";
        settings["showPlugins"] = _configuration[ServiceNames.ConfigKeys.DisplayShowPlugins] ?? "dbsize delta direction upbat";
        settings["showForecast"] = _configuration[ServiceNames.ConfigKeys.DisplayShowForecast] ?? "ar2";
        settings["focusHours"] = _configuration.GetValue<int>("Display:FocusHours", 3);
        settings["heartbeat"] = _configuration.GetValue<int>("Server:Heartbeat", 60);
        settings["baseURL"] = _configuration["Server:BaseURL"] ?? "";

        // Auth settings
        settings["authDefaultRoles"] = _configuration["Auth:DefaultRoles"] ?? "readable";

        // Threshold values
        settings["thresholds"] = new Dictionary<string, object>
        {
            ["bgHigh"] = _configuration.GetValue<int>("Thresholds:BgHigh", 260),
            ["bgTargetTop"] = _configuration.GetValue<int>("Thresholds:BgTargetTop", 180),
            ["bgTargetBottom"] = _configuration.GetValue<int>("Thresholds:BgTargetBottom", 80),
            ["bgLow"] = _configuration.GetValue<int>("Thresholds:BgLow", 55),
        };

        // Security settings
        settings["insecureUseHttp"] = _configuration.GetValue<bool>("Security:InsecureUseHttp", true);
        settings["secureHstsHeader"] = _configuration.GetValue<bool>("Security:SecureHstsHeader", false);
        settings["secureHstsHeaderIncludeSubdomains"] = _configuration.GetValue<bool>("Security:SecureHstsHeaderIncludeSubdomains", false);
        settings["secureHstsHeaderPreload"] = _configuration.GetValue<bool>("Security:SecureHstsHeaderPreload", false);
        settings["secureCsp"] = _configuration.GetValue<bool>("Security:SecureCsp", false);

        // Misc settings
        settings["deNormalizeDates"] = _configuration.GetValue<bool>("Display:DeNormalizeDates", false);
        settings["showClockDelta"] = _configuration.GetValue<bool>("Display:ShowClockDelta", false);
        settings["showClockLastTime"] = _configuration.GetValue<bool>("Display:ShowClockLastTime", false);
        settings["authFailDelay"] = _configuration.GetValue<int>("Auth:FailDelay", 5000);
        settings["adminNotifiesEnabled"] = _configuration.GetValue<bool>("Notifications:AdminNotifiesEnabled", true);
        settings["authenticationPromptOnLoad"] = _configuration.GetValue<bool>("Auth:PromptOnLoad", false);

        // Frame URLs (for Nightscout's multi-frame display feature)
        for (int i = 1; i <= 8; i++)
        {
            settings[$"frameUrl{i}"] = _configuration[$"Display:FrameUrl{i}"] ?? "";
            settings[$"frameName{i}"] = _configuration[$"Display:FrameName{i}"] ?? "";
        }

        // Default features
        settings["DEFAULT_FEATURES"] = GetDefaultFeatures();
        settings["alarmTypes"] = _configuration.GetSection("Alarms:Types").Get<string[]>() ?? new[] { "predict" };
        settings["enable"] = GetEnabledFeaturesArray();

        await Task.CompletedTask; // For future async operations

        return settings;
    }

    /// <summary>
    /// Get default features array
    /// </summary>
    private static string[] GetDefaultFeatures()
    {
        return new[]
        {
            "bgnow", "delta", "direction", "timeago", "devicestatus", "upbat",
            "errorcodes", "profile", "bolus", "dbsize", "runtimestate", "basal", "careportal"
        };
    }

    /// <summary>
    /// Get extended settings (Nightscout compatibility)
    /// </summary>
    private Dictionary<string, object> GetExtendedSettings()
    {
        return new Dictionary<string, object>
        {
            ["devicestatus"] = new Dictionary<string, object>
            {
                ["advanced"] = _configuration.GetValue<bool>("ExtendedSettings:DeviceStatus:Advanced", true),
                ["days"] = _configuration.GetValue<int>("ExtendedSettings:DeviceStatus:Days", 1)
            }
        };
    }

    /// <summary>
    /// Get the list of enabled features/plugins as a space-separated string
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
    /// Get the list of enabled features/plugins as an array (Nightscout compatibility)
    /// </summary>
    private string[] GetEnabledFeaturesArray()
    {
        var enabledFeatures = _configuration[ServiceNames.ConfigKeys.FeaturesEnable];
        if (!string.IsNullOrEmpty(enabledFeatures))
        {
            return enabledFeatures.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        // Default enabled features to match Nightscout behavior
        return new[]
        {
            "careportal", "basal", "iob", "cob", "bwp", "cage", "sage", "iage", "bage",
            "pump", "openaps", "loop", "treatmentnotify", "bgnow", "delta", "direction",
            "timeago", "devicestatus", "upbat", "errorcodes", "profile", "bolus", "dbsize",
            "runtimestate", "ar2"
        };
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

        var entriesTask = _dbContext
            .Entries.AsNoTracking()
            .OrderByDescending(e => e.SysUpdatedAt)
            .Select(e => (DateTime?)e.SysUpdatedAt)
            .FirstOrDefaultAsync();

        var treatmentsTask = _dbContext
            .Treatments.AsNoTracking()
            .OrderByDescending(t => t.SysUpdatedAt)
            .Select(t => (DateTime?)t.SysUpdatedAt)
            .FirstOrDefaultAsync();

        var profileTask = _dbContext
            .Profiles.AsNoTracking()
            .OrderByDescending(p => p.UpdatedAtPg)
            .Select(p => (DateTime?)p.UpdatedAtPg)
            .FirstOrDefaultAsync();

        var deviceStatusTask = _dbContext
            .DeviceStatuses.AsNoTracking()
            .OrderByDescending(d => d.SysUpdatedAt)
            .Select(d => (DateTime?)d.SysUpdatedAt)
            .FirstOrDefaultAsync();

        var foodTask = _dbContext
            .Foods.AsNoTracking()
            .OrderByDescending(f => f.SysUpdatedAt)
            .Select(f => (DateTime?)f.SysUpdatedAt)
            .FirstOrDefaultAsync();

        var settingsTask = _dbContext
            .Settings.AsNoTracking()
            .OrderByDescending(s => s.SrvModified ?? s.SysUpdatedAt)
            .Select(s =>
                (DateTime?)(
                    s.SrvModified.HasValue ? s.SrvModified.Value.UtcDateTime : s.SysUpdatedAt
                )
            )
            .FirstOrDefaultAsync();

        var activityTask = _dbContext
            .Activities.AsNoTracking()
            .OrderByDescending(a => a.SysUpdatedAt)
            .Select(a => (DateTime?)a.SysUpdatedAt)
            .FirstOrDefaultAsync();

        var authSubjectsTask = _dbContext
            .Subjects.AsNoTracking()
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => (DateTime?)s.UpdatedAt)
            .FirstOrDefaultAsync();

        var roleTask = _dbContext
            .Roles.AsNoTracking()
            .OrderByDescending(r => r.UpdatedAt)
            .Select(r => (DateTime?)r.UpdatedAt)
            .FirstOrDefaultAsync();

        var oidcProviderTask = _dbContext
            .OidcProviders.AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => (DateTime?)p.UpdatedAt)
            .FirstOrDefaultAsync();

        var notificationPreferencesTask = _dbContext
            .NotificationPreferences.AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => (DateTime?)p.UpdatedAt)
            .FirstOrDefaultAsync();

        var alertRulesTask = _dbContext
            .AlertRules.AsNoTracking()
            .OrderByDescending(a => a.UpdatedAt)
            .Select(a => (DateTime?)a.UpdatedAt)
            .FirstOrDefaultAsync();

        var alertHistoryTask = _dbContext
            .AlertHistory.AsNoTracking()
            .OrderByDescending(h => h.UpdatedAt)
            .Select(h => (DateTime?)h.UpdatedAt)
            .FirstOrDefaultAsync();

        await Task.WhenAll(
            entriesTask,
            treatmentsTask,
            profileTask,
            deviceStatusTask,
            foodTask,
            settingsTask,
            activityTask,
            authSubjectsTask,
            roleTask,
            oidcProviderTask,
            notificationPreferencesTask,
            alertRulesTask,
            alertHistoryTask
        );

        var additional = new Dictionary<string, DateTime>();

        var authLastModified = GetMostRecentTimestamp(
            authSubjectsTask.Result,
            roleTask.Result,
            oidcProviderTask.Result
        );
        if (authLastModified.HasValue)
        {
            additional["auth"] = authLastModified.Value;
        }

        var notificationsLastModified = GetMostRecentTimestamp(
            notificationPreferencesTask.Result,
            alertRulesTask.Result,
            alertHistoryTask.Result
        );
        if (notificationsLastModified.HasValue)
        {
            additional["notifications"] = notificationsLastModified.Value;
        }

        var response = new LastModifiedResponse
        {
            ServerTime = serverTime,
            Entries = entriesTask.Result,
            Treatments = treatmentsTask.Result,
            Profile = profileTask.Result,
            DeviceStatus = deviceStatusTask.Result,
            Food = foodTask.Result,
            Settings = settingsTask.Result,
            Activity = activityTask.Result,
            Additional = additional,
        };

        _logger.LogDebug("Last modified response generated");

        return response;
    }

    /// <summary>
    /// Get authorization information for the current request
    /// </summary>
    private AuthorizationInfo GetAuthorizationInfo()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var authContext = httpContext?.GetAuthContext();

        if (authContext == null || !authContext.IsAuthenticated)
        {
            return new AuthorizationInfo
            {
                IsAuthorized = false,
                Scope = new List<string>(),
                Roles = new List<string>(),
            };
        }

        var scope = new List<string>();
        if (authContext.Scopes?.Count > 0)
        {
            scope.AddRange(authContext.Scopes);
        }
        if (authContext.Permissions?.Count > 0)
        {
            scope.AddRange(authContext.Permissions);
        }

        return new AuthorizationInfo
        {
            IsAuthorized = true,
            Scope = scope.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Subject =
                authContext.SubjectId?.ToString() ?? authContext.SubjectName ?? authContext.Email,
            Roles =
                authContext.Roles?.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                ?? new List<string>(),
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

    private static DateTime? GetMostRecentTimestamp(params DateTime?[] timestamps)
    {
        var filtered = timestamps.Where(t => t.HasValue).Select(t => t!.Value).ToList();
        return filtered.Count == 0 ? null : filtered.Max();
    }
}
