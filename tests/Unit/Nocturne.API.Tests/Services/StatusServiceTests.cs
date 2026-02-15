using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Comprehensive unit tests for StatusService
/// Tests system status functionality with Nightscout compatibility and caching
/// </summary>
[Parity("api.status.test.js")]
public class StatusServiceTests
{
    private readonly Mock<ICacheService> _mockCacheService;
    private readonly Mock<IDemoModeService> _mockDemoModeService;
    private readonly Mock<ILogger<StatusService>> _mockLogger;
    private readonly IConfiguration _configuration;
    private readonly NocturneDbContext _dbContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly StatusService _statusService;

    public StatusServiceTests()
    {
        _mockCacheService = new Mock<ICacheService>();
        _mockDemoModeService = new Mock<IDemoModeService>();
        _mockLogger = new Mock<ILogger<StatusService>>();
        _mockDemoModeService.Setup(x => x.IsEnabled).Returns(false);
        _dbContext = TestDbContextFactory.CreateInMemoryContext();

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            Roles = new List<string> { "readable" },
            Permissions = new List<string> { "api:*:read" },
            Scopes = new List<string> { "api:*:read" },
            SubjectName = "test-user",
        };
        _httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };

        // Setup default configuration values using real ConfigurationBuilder
        var configData = new Dictionary<string, string?>
        {
            ["Nightscout:SiteName"] = "Test Nocturne",
            ["Features:CareportalEnabled"] = "true",
            ["Display:Units"] = "mg/dl",
            ["Display:TimeFormat"] = "12",
            ["Display:NightMode"] = "false",
            ["Display:EditMode"] = "true",
            ["Display:ShowRawBG"] = "never",
            ["Display:CustomTitle"] = "",
            ["Display:Theme"] = "default",
            ["Display:ShowPlugins"] = "",
            ["Display:ShowForecast"] = "",
            ["Display:ScaleY"] = "log",
            ["Alarms:UrgentHigh:Enabled"] = "true",
            ["Alarms:High:Enabled"] = "true",
            ["Alarms:Low:Enabled"] = "true",
            ["Alarms:UrgentLow:Enabled"] = "true",
            ["Alarms:TimeAgoWarn:Enabled"] = "true",
            ["Alarms:TimeAgoUrgent:Enabled"] = "true",
            ["Thresholds:BgHigh"] = "260",
            ["Thresholds:BgTargetTop"] = "180",
            ["Thresholds:BgTargetBottom"] = "80",
            ["Thresholds:BgLow"] = "55",
            ["Localization:Language"] = "en",
        };

        _configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        _statusService = CreateStatusService(_configuration, _dbContext);
    }

    #region GetSystemStatusAsync Tests

    [Fact]
    public async Task GetSystemStatusAsync_CacheHit_ShouldReturnCachedValue()
    {
        // Arrange
        var cachedStatus = new StatusResponse
        {
            Status = "ok",
            Name = "Test Nocturne",
            Version = "1.0.0",
            ServerTime = DateTime.UtcNow,
            ApiEnabled = true,
        };

        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync(cachedStatus);

        // Act
        var result = await _statusService.GetSystemStatusAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().BeSameAs(cachedStatus);
        result.Status.Should().Be("ok");
        result.Name.Should().Be("Test Nocturne");

        _mockCacheService.Verify(
            x => x.GetAsync<StatusResponse>("status:system", default),
            Times.Once
        );
        _mockCacheService.Verify(
            x =>
                x.SetAsync(
                    It.IsAny<string>(),
                    It.IsAny<StatusResponse>(),
                    It.IsAny<TimeSpan>(),
                    default
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task GetSystemStatusAsync_CacheMiss_ShouldGenerateAndCacheStatus()
    {
        // Arrange
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var result = await _statusService.GetSystemStatusAsync();

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be("ok");
        result.Name.Should().Be("Test Nocturne");
        result.ApiEnabled.Should().BeTrue();
        result.CareportalEnabled.Should().BeTrue();
        result.ServerTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        _mockCacheService.Verify(
            x => x.GetAsync<StatusResponse>("status:system", default),
            Times.Once
        );
        _mockCacheService.Verify(
            x => x.SetAsync("status:system", result, TimeSpan.FromMinutes(2), default),
            Times.Once
        );
    }

    [Fact]
    public async Task GetSystemStatusAsync_WithCustomConfiguration_ShouldUseConfigValues()
    {
        // Arrange
        var customConfigData = new Dictionary<string, string?>
        {
            ["Nightscout:SiteName"] = "Custom Site",
            ["Features:CareportalEnabled"] = "false",
        };

        var customConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(customConfigData)
            .Build();

        var customService = CreateStatusService(customConfiguration, _dbContext);

        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var result = await customService.GetSystemStatusAsync();

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("Custom Site");
        result.CareportalEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task GetSystemStatusAsync_WithEnvironmentVariables_ShouldIncludeGitCommit()
    {
        // Arrange
        var previousGitCommit = Environment.GetEnvironmentVariable("GIT_COMMIT");
        Environment.SetEnvironmentVariable("GIT_COMMIT", "abc123def456");

        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        try
        {
            // Act
            var result = await _statusService.GetSystemStatusAsync();

            // Assert
            result.Should().NotBeNull();
            result.Head.Should().Be("abc123def456");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_COMMIT", previousGitCommit);
        }
    }

    [Fact]
    public async Task GetSystemStatusAsync_WithoutGitCommit_ShouldUseRepositoryHeadOrDefault()
    {
        // Arrange
        var previousGitCommit = Environment.GetEnvironmentVariable("GIT_COMMIT");
        Environment.SetEnvironmentVariable("GIT_COMMIT", null);

        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        try
        {
            // Act
            var result = await _statusService.GetSystemStatusAsync();

            // Assert
            result.Should().NotBeNull();
            result.Head.Should().Be(ResolveExpectedHead());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_COMMIT", previousGitCommit);
        }
    }

    #endregion

    #region GetV3SystemStatusAsync Tests

    [Fact]
    public async Task GetV3SystemStatusAsync_ShouldIncludeExtendedInformation()
    {
        // Arrange
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var result = await _statusService.GetV3SystemStatusAsync();

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be("ok");
        result.Name.Should().Be("Test Nocturne");
        result.ApiEnabled.Should().BeTrue();
        result.CareportalEnabled.Should().BeTrue();

        // Extended information checks
        result.Extended.Should().NotBeNull();
        result.Extended!.Authorization.Should().NotBeNull();
        result.Extended.Authorization!.IsAuthorized.Should().BeTrue();
        result.Extended.Authorization.Scope.Should().Contain("api:*:read");
        result.Extended.Authorization.Roles.Should().Contain("readable");

        result.Extended.Permissions.Should().NotBeNull();
        result.Extended.Permissions!.Should().ContainKey("entries:read");
        result.Extended.Permissions["entries:read"].Should().BeTrue();
        result.Extended.Permissions.Should().ContainKey("entries:write");
        result.Extended.Permissions["entries:write"].Should().BeFalse();

        result.Extended.Collections.Should().NotBeNull();
        result.Extended.Collections!.Should().Contain("entries");
        result.Extended.Collections.Should().Contain("treatments");
        result.Extended.Collections.Should().Contain("profile");

        result.Extended.ApiVersions.Should().NotBeNull();
        result.Extended.ApiVersions!.Should().ContainKey("v1");
        result.Extended.ApiVersions["v1"].Should().BeTrue();
        result.Extended.ApiVersions.Should().ContainKey("v3");
        result.Extended.ApiVersions["v3"].Should().BeTrue();
        result.Extended.ApiVersions.Should().ContainKey("v2");
        result.Extended.ApiVersions["v2"].Should().BeFalse();

        result.Extended.UptimeMs.Should().BeGreaterThanOrEqualTo(0);
    }

    #endregion

    #region GetLastModifiedAsync Tests

    [Fact]
    public async Task GetLastModifiedAsync_ShouldReturnTimestampsForAllCollections()
    {
        // Arrange
        var now = DateTime.UtcNow;
        await using var context = TestDbContextFactory.CreateInMemoryContext();
        SeedLastModifiedData(context, now);
        var service = CreateStatusService(_configuration, context);

        // Act
        var result = await service.GetLastModifiedAsync();

        // Assert
        result.Should().NotBeNull();
        result.ServerTime.Should().BeCloseTo(now, TimeSpan.FromSeconds(5));

        // Collection timestamps should be present and not ahead of server time
        result.Entries.Should().NotBeNull();
        result.Treatments.Should().NotBeNull();
        result.Profile.Should().NotBeNull();
        result.DeviceStatus.Should().NotBeNull();
        result.Food.Should().NotBeNull();
        result.Settings.Should().NotBeNull();
        result.Activity.Should().NotBeNull();

        result.Entries.Should().BeOnOrBefore(result.ServerTime);
        result.Treatments.Should().BeOnOrBefore(result.ServerTime);
        result.Profile.Should().BeOnOrBefore(result.ServerTime);
        result.DeviceStatus.Should().BeOnOrBefore(result.ServerTime);
        result.Food.Should().BeOnOrBefore(result.ServerTime);
        result.Settings.Should().BeOnOrBefore(result.ServerTime);
        result.Activity.Should().BeOnOrBefore(result.ServerTime);

        // Additional timestamps
        result.Additional.Should().NotBeNull();
        result.Additional!.Should().ContainKey("auth");
        result.Additional.Should().ContainKey("notifications");
        result
            .Additional["auth"]
            .Should()
            .BeOnOrBefore(result.ServerTime);
        result
            .Additional["notifications"]
            .Should()
            .BeOnOrBefore(result.ServerTime);
    }

    #endregion

    #region Configuration and Settings Tests

    [Fact]
    public async Task GetSystemStatusAsync_WithCustomDisplaySettings_ShouldIncludeInSettings()
    {
        // Arrange
        var customConfigData = new Dictionary<string, string?>
        {
            ["Display:Units"] = "mmol/l",
            ["Display:TimeFormat"] = "24",
            ["Display:NightMode"] = "true",
            ["Display:EditMode"] = "false",
            ["Display:Theme"] = "dark",
        };

        var customConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(customConfigData)
            .Build();

        var customService = CreateStatusService(customConfiguration, _dbContext);

        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var result = await customService.GetSystemStatusAsync();

        // Assert
        result.Should().NotBeNull();
        result.Settings.Should().NotBeNull();
        Assert.NotNull(result.Settings);
        result.Settings!.Should().ContainKey("units");
        result.Settings["units"].Should().Be("mmol/l");

        result.Settings.Should().ContainKey("timeFormat");
        result.Settings["timeFormat"].Should().Be(24);

        result.Settings.Should().ContainKey("nightMode");
        result.Settings["nightMode"].Should().Be(true);

        result.Settings.Should().ContainKey("editMode");
        result.Settings["editMode"].Should().Be(false);

        result.Settings.Should().ContainKey("theme");
        result.Settings["theme"].Should().Be("dark");
    }

    [Fact]
    public async Task GetSystemStatusAsync_WithCustomAlarmSettings_ShouldIncludeInSettings()
    {
        // Arrange
        var customConfigData = new Dictionary<string, string?>
        {
            ["Alarms:UrgentHigh:Enabled"] = "false",
            ["Alarms:High:Enabled"] = "false",
            ["Alarms:Low:Enabled"] = "false",
        };

        var customConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(customConfigData)
            .Build();

        var customService = CreateStatusService(customConfiguration, _dbContext);

        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var result = await customService.GetSystemStatusAsync();

        // Assert
        result.Should().NotBeNull();
        result.Settings.Should().NotBeNull();
        Assert.NotNull(result.Settings);
        result.Settings!.Should().ContainKey("alarmUrgentHigh");
        result.Settings["alarmUrgentHigh"].Should().Be(false);

        result.Settings.Should().ContainKey("alarmHigh");
        result.Settings["alarmHigh"].Should().Be(false);

        result.Settings.Should().ContainKey("alarmLow");
        result.Settings["alarmLow"].Should().Be(false);
    }

    [Fact]
    public async Task GetSystemStatusAsync_WithCustomThresholds_ShouldIncludeInSettings()
    {
        // Arrange
        var customConfigData = new Dictionary<string, string?>
        {
            ["Thresholds:BgHigh"] = "300",
            ["Thresholds:BgTargetTop"] = "200",
            ["Thresholds:BgTargetBottom"] = "70",
            ["Thresholds:BgLow"] = "50",
        };

        var customConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(customConfigData)
            .Build();

        var customService = CreateStatusService(customConfiguration, _dbContext);

        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var result = await customService.GetSystemStatusAsync();

        // Assert
        result.Should().NotBeNull();
        result.Settings.Should().NotBeNull();
        Assert.NotNull(result.Settings);
        result.Settings!.Should().ContainKey("thresholds");
        var thresholds = result.Settings["thresholds"] as Dictionary<string, object>;
        Assert.NotNull(thresholds);
        thresholds.Should().ContainKey("bgHigh");
        thresholds["bgHigh"].Should().Be(300);

        thresholds.Should().ContainKey("bgTargetTop");
        thresholds["bgTargetTop"].Should().Be(200);

        thresholds.Should().ContainKey("bgTargetBottom");
        thresholds["bgTargetBottom"].Should().Be(70);

        thresholds.Should().ContainKey("bgLow");
        thresholds["bgLow"].Should().Be(50);
    }

    [Fact]
    public async Task GetSystemStatusAsync_WithCustomEnabledFeatures_ShouldUseCustomValue()
    {
        // Arrange
        var customConfigData = new Dictionary<string, string?>
        {
            ["Features:Enable"] = "careportal basal dbsize custom",
        };

        var customConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(customConfigData)
            .Build();

        var customService = CreateStatusService(customConfiguration, _dbContext);

        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var result = await customService.GetSystemStatusAsync();

        // Assert
        result.Should().NotBeNull();
        Assert.NotNull(result.Settings);
        result.Settings.Should().NotBeNull();
        result.Settings!.Should().ContainKey("enable");
        var enableArray = result.Settings["enable"] as string[];
        enableArray.Should().NotBeNull();
        enableArray.Should().Contain("careportal");
        enableArray.Should().Contain("basal");
        enableArray.Should().Contain("dbsize");
        enableArray.Should().Contain("custom");
    }

    [Fact]
    public async Task GetSystemStatusAsync_WithoutCustomEnabledFeatures_ShouldUseDefault()
    {
        // Arrange
        var customConfigData = new Dictionary<string, string?>();

        var customConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(customConfigData)
            .Build();

        var customService = CreateStatusService(customConfiguration, _dbContext);

        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var result = await customService.GetSystemStatusAsync();

        // Assert
        result.Should().NotBeNull();
        result.Settings.Should().NotBeNull();
        Assert.NotNull(result.Settings);
        result.Settings!.Should().ContainKey("enable");
        var enabledFeatures = result.Settings["enable"] as string[];
        enabledFeatures.Should().NotBeNull();
        enabledFeatures.Should().Contain("careportal");
        enabledFeatures.Should().Contain("basal");
        enabledFeatures.Should().Contain("dbsize");
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task GetSystemStatusAsync_CacheServiceThrows_ShouldPropagateException()
    {
        // Arrange
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ThrowsAsync(new InvalidOperationException("Cache service unavailable"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _statusService.GetSystemStatusAsync()
        );

        // Verify cache operations were attempted
        _mockCacheService.Verify(
            x => x.GetAsync<StatusResponse>("status:system", default),
            Times.Once
        );
    }

    [Fact]
    public async Task GetSystemStatusAsync_NullConfiguration_ShouldUseDefaults()
    {
        // Arrange
        var emptyConfiguration = new ConfigurationBuilder().Build();

        var nullConfigService = CreateStatusService(emptyConfiguration, _dbContext);

        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var result = await nullConfigService.GetSystemStatusAsync();

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("nightscout"); // Default value matching Nightscout compat
        result.CareportalEnabled.Should().BeTrue(); // Default value
    }

    #endregion

    #region Nightscout Compatibility Tests

    [Fact]
    public async Task GetSystemStatusAsync_ResponseFormat_ShouldMatchNightscoutStructure()
    {
        // Arrange
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var result = await _statusService.GetSystemStatusAsync();

        // Assert
        result.Should().NotBeNull();

        // Core Nightscout fields
        result.Status.Should().NotBeNullOrEmpty();
        result.Name.Should().NotBeNullOrEmpty();
        result.Version.Should().NotBeNullOrEmpty();
        result.ServerTime.Should().NotBe(default);
        result.ApiEnabled.Should().BeTrue();
        result.CareportalEnabled.Should().HaveValue();
        result.Head.Should().NotBeNullOrEmpty();
        result.Settings.Should().NotBeNull();

        // Settings should contain expected Nightscout keys
        var expectedKeys = new[]
        {
            "units",
            "timeFormat",
            "nightMode",
            "editMode",
            "showRawbg",
            "customTitle",
            "theme",
            "enable",
            "showPlugins",
            "showForecast",
            "alarmUrgentHigh",
            "alarmHigh",
            "alarmLow",
            "alarmUrgentLow",
            "alarmTimeagoWarn",
            "alarmTimeagoUrgent",
            "thresholds",
            "language",
            "scaleY",
        };

        foreach (var key in expectedKeys)
        {
            result.Settings.Should().ContainKey(key, $"Missing expected Nightscout setting: {key}");
        }
    }

    [Fact]
    public async Task GetSystemStatusAsync_JsonSerialization_ShouldSerializeCorrectly()
    {
        // Arrange
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var result = await _statusService.GetSystemStatusAsync();
        var json = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("\"status\":");
        json.Should().Contain("\"name\":");
        json.Should().Contain("\"version\":");
        json.Should().Contain("\"serverTime\":");
        json.Should().Contain("\"apiEnabled\":");
        json.Should().Contain("\"careportalEnabled\":");
        json.Should().Contain("\"head\":");
        json.Should().Contain("\"settings\":");

        // Should be valid JSON
        var deserialized = JsonSerializer.Deserialize<StatusResponse>(json);
        deserialized.Should().NotBeNull();
        deserialized!.Status.Should().Be(result.Status);
    }

    #endregion

    #region Performance and Caching Tests

    [Fact]
    public async Task GetSystemStatusAsync_CacheTTL_ShouldUse2MinuteTTL()
    {
        // Arrange
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var result = await _statusService.GetSystemStatusAsync();

        // Assert
        _mockCacheService.Verify(
            x => x.SetAsync("status:system", result, TimeSpan.FromMinutes(2), default),
            Times.Once
        );
    }

    [Fact]
    public async Task GetSystemStatusAsync_MultipleConcurrentCalls_ShouldHandleCorrectly()
    {
        // Arrange
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var tasks = Enumerable
            .Range(0, 5)
            .Select(_ => _statusService.GetSystemStatusAsync())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(5);
        results.Should().AllSatisfy(r => r.Should().NotBeNull());
        results.Should().AllSatisfy(r => r.Status.Should().Be("ok"));

        // Cache should be checked for each call
        _mockCacheService.Verify(
            x => x.GetAsync<StatusResponse>("status:system", default),
            Times.Exactly(5)
        );
    }

    #endregion

    #region Helper Methods

    private static void SeedLastModifiedData(NocturneDbContext context, DateTime now)
    {
        var baseMills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        context.Entries.Add(
            new EntryEntity
            {
                Id = Guid.CreateVersion7(),
                Mills = baseMills,
                Mgdl = 100,
                Type = "sgv",
                SysUpdatedAt = now.AddMinutes(-5),
            }
        );

        context.Treatments.Add(
            new TreatmentEntity
            {
                Id = Guid.CreateVersion7(),
                Mills = baseMills,
                EventType = "bolus",
                Insulin = 1.5,
                SysUpdatedAt = now.AddMinutes(-10),
            }
        );

        context.Profiles.Add(
            new ProfileEntity
            {
                Id = Guid.CreateVersion7(),
                Mills = baseMills,
                DefaultProfile = "Default",
                Units = "mg/dl",
                UpdatedAtPg = now.AddHours(-1),
            }
        );

        context.DeviceStatuses.Add(
            new DeviceStatusEntity
            {
                Id = Guid.CreateVersion7(),
                Device = "dexcom",
                SysUpdatedAt = now.AddMinutes(-2),
            }
        );

        context.Foods.Add(
            new FoodEntity
            {
                Id = Guid.CreateVersion7(),
                Name = "test-food",
                SysUpdatedAt = now.AddDays(-1),
            }
        );

        context.Settings.Add(
            new SettingsEntity
            {
                Id = Guid.CreateVersion7(),
                Key = "test-setting",
                Value = "{}",
                SysUpdatedAt = now.AddHours(-6),
                SrvModified = now.AddHours(-6),
            }
        );

        context.Activities.Add(
            new ActivityEntity
            {
                Id = Guid.CreateVersion7(),
                Type = "exercise",
                SysUpdatedAt = now.AddMinutes(-30),
            }
        );

        context.Subjects.Add(
            new SubjectEntity
            {
                Id = Guid.CreateVersion7(),
                Name = "test-subject",
                UpdatedAt = now.AddDays(-7),
            }
        );

        context.Roles.Add(
            new RoleEntity
            {
                Id = Guid.CreateVersion7(),
                Name = "reader",
                UpdatedAt = now.AddDays(-7),
            }
        );

        context.OidcProviders.Add(
            new OidcProviderEntity
            {
                Id = Guid.CreateVersion7(),
                Name = "test-oidc",
                IssuerUrl = "https://issuer",
                ClientId = "client",
                ClientSecretEncrypted = Array.Empty<byte>(),
                UpdatedAt = now.AddDays(-7),
            }
        );

        context.NotificationPreferences.Add(
            new NotificationPreferencesEntity
            {
                Id = Guid.CreateVersion7(),
                UserId = "test-user",
                EmailEnabled = true,
                UpdatedAt = now.AddMinutes(-15),
            }
        );

        context.AlertRules.Add(
            new AlertRuleEntity
            {
                Id = Guid.CreateVersion7(),
                Name = "test-rule",
                UpdatedAt = now.AddMinutes(-15),
            }
        );

        context.AlertHistory.Add(
            new AlertHistoryEntity
            {
                Id = Guid.CreateVersion7(),
                UpdatedAt = now.AddMinutes(-15),
            }
        );

        context.SaveChanges();
    }

    private StatusService CreateStatusService(
        IConfiguration configuration,
        NocturneDbContext? context = null
    ) =>
        new(
            configuration,
            _mockCacheService.Object,
            _mockDemoModeService.Object,
            context ?? TestDbContextFactory.CreateInMemoryContext(),
            _httpContextAccessor,
            _mockLogger.Object
        );

    private static string ResolveExpectedHead()
    {
        var envCommit = Environment.GetEnvironmentVariable("GIT_COMMIT");
        if (!string.IsNullOrWhiteSpace(envCommit))
        {
            return envCommit;
        }

        var gitDirectory = FindGitDirectory(AppContext.BaseDirectory);
        var repositoryCommit = gitDirectory != null ? ReadCommitFromGitDirectory(gitDirectory) : null;

        return string.IsNullOrWhiteSpace(repositoryCommit) ? "nocturne-dev" : repositoryCommit;
    }

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

    #endregion
}
