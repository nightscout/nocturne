using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Tests.Shared.Mocks;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Platform;

/// <summary>
/// Comprehensive unit tests for StatusService
/// Tests system status functionality with Nightscout compatibility and caching
/// </summary>
[Parity("api.status.test.js")]
[Collection(ProcessEnvironmentCollection.Name)]
public class StatusServiceTests
{
    private readonly Mock<ICacheService> _mockCacheService;
    private readonly Mock<ITenantService> _tenants = new();
    private readonly Mock<IDemoModeService> _mockDemoModeService;
    private readonly Mock<ILogger<StatusService>> _mockLogger;
    private readonly Mock<ITenantAccessor> _mockTenantAccessor;
    private readonly IConfiguration _configuration;
    private readonly NocturneDbContext _dbContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly StatusService _statusService;

    public StatusServiceTests()
    {
        _mockCacheService = new Mock<ICacheService>();
        _mockDemoModeService = new Mock<IDemoModeService>();
        _mockLogger = new Mock<ILogger<StatusService>>();
        _mockTenantAccessor = MockTenantAccessor.Create();
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
        httpContext.SetGrantedScopes(ScopeTranslator.FromPermissions(["api:*:read"]));
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

    #region Demo tenant fields

    // isDemo/nextResetAt are read from the resolved TenantContext, not from the server-wide
    // IDemoModeService toggle: whether a tenant is a demo is a property of that tenant, and a
    // deployment that runs the generator as a separate service leaves the API's toggle off. The
    // web app keys its demo banner and its demo sign-in redirect off these two fields, so a
    // regression here is a demo tenant whose login page nobody can complete.

    [Fact]
    public async Task GetSystemStatusAsync_OnADemoTenant_ReportsIsDemoAndTheNextReset()
    {
        var nextResetAt = new DateTime(2026, 8, 5, 3, 0, 0, DateTimeKind.Utc);
        var dbName = $"nocturne_status_demo_{Guid.NewGuid()}";
        var ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
        var tenantId = MockTenantAccessor.DefaultTenantId;

        ctx.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = "demo",
            DisplayName = "Nocturne Demo",
            IsActive = true,
            IsDemo = true,
            DemoConfig = new TenantDemoConfigEntity { TenantId = tenantId, NextResetAt = nextResetAt },
        });
        await ctx.SaveChangesAsync();

        var service = CreateStatusService(
            _configuration, ctx, dbName,
            MockTenantAccessor.Create(tenantId: tenantId, slug: "demo", isDemo: true).Object);

        var result = await service.GetSystemStatusAsync();

        result.IsDemo.Should().BeTrue("the web app gates the demo banner and demo sign-in on this");
        result.NextResetAt.Should().Be(nextResetAt);
    }

    [Fact]
    public async Task GetSystemStatusAsync_OnAnOrdinaryTenant_LeavesTheDemoFieldsUnset()
    {
        var service = CreateStatusService(
            _configuration,
            tenantAccessor: MockTenantAccessor.Create(isDemo: false).Object);

        var result = await service.GetSystemStatusAsync();

        result.IsDemo.Should().BeNull("an ordinary tenant reports no demo state at all");
        result.NextResetAt.Should().BeNull();
    }

    [Fact]
    public async Task GetSystemStatusAsync_OnlyChargesADemoTenantForTheResetLookup()
    {
        // Status is polled often and is generated per cache miss, so the demo-config lookup is
        // gated on the context flag rather than run for everyone. Counting contexts opened
        // (other parts of status generation open some too) pins the gate: the demo tenant pays
        // for exactly one more than an ordinary tenant does.
        var ordinary = await CountContextsOpenedAsync(isDemo: false);
        var demo = await CountContextsOpenedAsync(isDemo: true);

        demo.Should().Be(ordinary + 1,
            "the reset lookup is the only extra query a demo tenant causes, and an ordinary " +
            "tenant must not cause it at all");
    }

    private async Task<int> CountContextsOpenedAsync(bool isDemo)
    {
        var dbName = $"nocturne_status_cost_{Guid.NewGuid()}";
        var ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
        var tenantId = MockTenantAccessor.DefaultTenantId;

        ctx.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = "t",
            DisplayName = "T",
            IsActive = true,
            IsDemo = isDemo,
            DemoConfig = new TenantDemoConfigEntity { TenantId = tenantId, NextResetAt = DateTime.UtcNow },
        });
        await ctx.SaveChangesAsync();

        var opened = 0;
        var mockFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        mockFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                opened++;
                var factoryCtx = TestDbContextFactory.CreateInMemoryContext(dbName);
                factoryCtx.TenantId = tenantId;
                return factoryCtx;
            });

        var tenantFactory = new Mock<INocturneDbContextFactory>();
        tenantFactory
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                opened++;
                return TestDbContextFactory.CreateInMemoryContext(dbName);
            });
        tenantFactory
            .Setup(f => f.CreatePlatformAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => mockFactory.Object.CreateDbContextAsync().GetAwaiter().GetResult());
        var appearanceReader = new Mock<ITenantAppearanceReader>();
        appearanceReader
            .Setup(r => r.GetTenantAppearanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplayPreferences());

        var tenantAccessor = MockTenantAccessor.Create(tenantId: tenantId, slug: "t", isDemo: isDemo).Object;
        var service = new StatusService(
            _configuration,
            _mockCacheService.Object,
            new CacheFlights(),
            _mockDemoModeService.Object,
            tenantFactory.Object,
            new TenantRepository(tenantFactory.Object), new SubjectRepository(tenantFactory.Object), new RoleRepository(tenantFactory.Object), new OidcProviderRepository(tenantFactory.Object),
            _httpContextAccessor,
            tenantAccessor,
            TestPublicAccessCache.Create(),
            new TenantGlucoseUnits(tenantAccessor, appearanceReader.Object, _tenants.Object),
            new GlucoseStatusClassifier(_configuration, NullLogger<GlucoseStatusClassifier>.Instance),
            new AlertRepository(mockFactory.Object.AsNocturneFactory()),
            Mock.Of<ICategoryReadContext>(),
            TimeProvider.System,
            _mockLogger.Object);

        await service.GetSystemStatusAsync();
        return opened;
    }

    #endregion

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
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
            .ReturnsAsync(cachedStatus);

        // Act
        var result = await _statusService.GetSystemStatusAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().BeSameAs(cachedStatus);
        result.Status.Should().Be("ok");
        result.Name.Should().Be("Test Nocturne");

        _mockCacheService.Verify(
            x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default),
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
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
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
            x => x.SetAsync("status:system:00000000-0000-0000-0000-000000000001", result, TimeSpan.FromMinutes(2), default),
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
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
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
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
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
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
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

    [Fact]
    public async Task GetSystemStatusAsync_WithBuildDate_ShouldIncludeBuild()
    {
        // Arrange
        var previousBuildDate = Environment.GetEnvironmentVariable("BUILD_DATE");
        Environment.SetEnvironmentVariable("BUILD_DATE", "2026-06-14T09:32:10Z");

        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
            .ReturnsAsync((StatusResponse?)null);

        try
        {
            // Act
            var result = await _statusService.GetSystemStatusAsync();

            // Assert
            result.Should().NotBeNull();
            result.Build.Should().Be("2026-06-14T09:32:10Z");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BUILD_DATE", previousBuildDate);
        }
    }

    [Fact]
    public async Task GetSystemStatusAsync_WithoutBuildDate_ShouldLeaveBuildNull()
    {
        // Arrange
        var previousBuildDate = Environment.GetEnvironmentVariable("BUILD_DATE");
        Environment.SetEnvironmentVariable("BUILD_DATE", null);

        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
            .ReturnsAsync((StatusResponse?)null);

        try
        {
            // Act
            var result = await _statusService.GetSystemStatusAsync();

            // Assert
            result.Should().NotBeNull();
            result.Build.Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("BUILD_DATE", previousBuildDate);
        }
    }

    #endregion

    #region GetV3SystemStatusAsync Tests

    [Fact]
    public async Task GetV3SystemStatusAsync_ShouldIncludeExtendedInformation()
    {
        // Arrange
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
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
        result.Extended.ApiVersions["v2"].Should().BeTrue();

        result.Extended.UptimeMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GetV3SystemStatusAsync_ReportsTheV3ApiLevelNotTheGitHead()
    {
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
            .ReturnsAsync((StatusResponse?)null);

        var result = await _statusService.GetV3SystemStatusAsync();

        result.ApiVersion.Should().Be("3.0.5");
        result.ApiVersion.Should().NotBe(result.Head);
    }

    [Fact]
    public async Task GetV3SystemStatusAsync_SrvDateTracksTheClockInsideTheStatusCacheWindow()
    {
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
            .ReturnsAsync(new StatusResponse { Status = "ok", Name = "Cached", ServerTime = DateTime.UtcNow.AddMinutes(-1) });

        var first = await _statusService.GetV3SystemStatusAsync();
        await Task.Delay(25);
        var second = await _statusService.GetV3SystemStatusAsync();

        second.SrvDate.Should().BeGreaterThan(first.SrvDate);
        first.SrvDate.Should().BeGreaterThan(
            new DateTimeOffset(DateTime.UtcNow.AddSeconds(-30)).ToUnixTimeMilliseconds());
        second.ServerTime.Should().BeAfter(first.ServerTime);
    }

    [Fact]
    public async Task GetSystemStatusAsync_ReportsTheAppVersion()
    {
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
            .ReturnsAsync((StatusResponse?)null);

        var result = await _statusService.GetSystemStatusAsync();

        result.Version.Should().Be(AppVersion.Current);
    }

    #endregion

    #region GetLastModifiedAsync Tests

    [Fact]
    public async Task GetLastModifiedAsync_StampsServerTimeFromTheClock()
    {
        var instant = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var dbName = $"nocturne_lastmod_{Guid.NewGuid()}";
        await using var context = TestDbContextFactory.CreateInMemoryContext(dbName);
        var service = CreateStatusService(_configuration, context, dbName, time: new FakeTimeProvider(instant));

        var result = await service.GetLastModifiedAsync();

        result.ServerTime.Should().Be(instant.UtcDateTime);
    }

    [Fact]
    public async Task GetLastModifiedAsync_ShouldReturnTimestampsForAllCollections()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var dbName = $"nocturne_lastmod_{Guid.NewGuid()}";
        await using var context = TestDbContextFactory.CreateInMemoryContext(dbName);
        SeedLastModifiedData(context, now);
        var service = CreateStatusService(_configuration, context, dbName);

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
        result.Entries.Should().BeOnOrBefore(result.ServerTime);
        result.Treatments.Should().BeOnOrBefore(result.ServerTime);
        result.Profile.Should().BeOnOrBefore(result.ServerTime);
        result.DeviceStatus.Should().BeOnOrBefore(result.ServerTime);
        result.Food.Should().BeOnOrBefore(result.ServerTime);
        result.Settings.Should().BeOnOrBefore(result.ServerTime);

        // Additional timestamps
        result.Additional.Should().NotBeNull();
        result.Additional!.Should().ContainKey("auth");
        result
            .Additional["auth"]
            .Should()
            .BeOnOrBefore(result.ServerTime);
    }

    [Fact]
    public async Task GetLastModifiedAsync_BasalScheduleNewerThanSettings_ReportsBasalStampForProfile()
    {
        var now = DateTime.UtcNow;
        var dbName = $"nocturne_lastmod_{Guid.NewGuid()}";
        await using var context = TestDbContextFactory.CreateInMemoryContext(dbName);
        SeedLastModifiedData(context, now);
        var basal = new Nocturne.Infrastructure.Data.Entities.V4.BasalScheduleEntity
        {
            Id = Guid.CreateVersion7(),
            ProfileName = "Default",
        };
        context.BasalSchedules.Add(basal);
        context.SaveChanges();
        // Insert stamps SysUpdatedAt with the save time; a timestamp-only touch keeps the assigned value.
        var basalStamp = now.AddMinutes(-10);
        context.TherapySettings.Single().SysUpdatedAt = now.AddHours(-1);
        basal.SysUpdatedAt = basalStamp;
        context.SaveChanges();
        var service = CreateStatusService(_configuration, context, dbName);

        var result = await service.GetLastModifiedAsync();

        result.Profile.Should().Be(basalStamp);
    }

    [Fact]
    public async Task GetLastModifiedAsync_CountsDeletedRows_AsNightscoutCountsItsInvalidDocuments()
    {
        var now = DateTime.UtcNow;
        var dbName = $"nocturne_lastmod_{Guid.NewGuid()}";
        await using var context = TestDbContextFactory.CreateInMemoryContext(dbName);
        SeedLastModifiedData(context, now);
        var bolus = new Nocturne.Infrastructure.Data.Entities.V4.BolusEntity
        {
            Id = Guid.CreateVersion7(), Timestamp = now.AddHours(-1), Insulin = 1, DeletedAt = now,
        };
        var aps = new Nocturne.Infrastructure.Data.Entities.V4.ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(), Timestamp = now.AddHours(-1), AidAlgorithm = "Loop", DeletedAt = now,
        };
        var food = new FoodEntity { Id = Guid.CreateVersion7(), Name = "deleted", DeletedAt = now };
        context.AddRange(bolus, aps, food);
        context.SaveChanges();
        var deletedAt = now.AddMinutes(1);
        bolus.SysUpdatedAt = deletedAt;
        aps.SysUpdatedAt = deletedAt;
        food.SysUpdatedAt = deletedAt;
        context.SaveChanges();
        var service = CreateStatusService(_configuration, context, dbName);

        var result = await service.GetLastModifiedAsync();

        result.Treatments.Should().Be(deletedAt);
        result.DeviceStatus.Should().Be(deletedAt);
        result.Food.Should().Be(deletedAt);
    }

    [Fact]
    public async Task GetLastModifiedAsync_CalibrationNewerThanSgv_ReportsCalibrationStampForEntries()
    {
        var now = DateTime.UtcNow;
        var dbName = $"nocturne_lastmod_{Guid.NewGuid()}";
        await using var context = TestDbContextFactory.CreateInMemoryContext(dbName);
        SeedLastModifiedData(context, now);
        var calibration = new Nocturne.Infrastructure.Data.Entities.V4.CalibrationEntity
        {
            Id = Guid.CreateVersion7(),
            Timestamp = now.AddMinutes(-1),
        };
        context.Calibrations.Add(calibration);
        context.SaveChanges();
        var calibrationStamp = now.AddMinutes(-1);
        context.SensorGlucose.Single().SysUpdatedAt = now.AddMinutes(-5);
        calibration.SysUpdatedAt = calibrationStamp;
        context.SaveChanges();
        var service = CreateStatusService(_configuration, context, dbName);

        var result = await service.GetLastModifiedAsync();

        result.Entries.Should().Be(calibrationStamp);
    }

    [Fact]
    public async Task GetLastModifiedAsync_ReportsOnlyTheCollectionsTheCallerCanRead()
    {
        var now = DateTime.UtcNow;
        var dbName = $"nocturne_lastmod_{Guid.NewGuid()}";
        await using var context = TestDbContextFactory.CreateInMemoryContext(dbName);
        SeedLastModifiedData(context, now);
        var service = CreateStatusService(
            _configuration, context, dbName, httpContextAccessor: Caller(authenticated: true, Scope.GlucoseRead));

        var result = await service.GetLastModifiedAsync();

        result.Entries.Should().NotBeNull();
        result.Treatments.Should().BeNull();
        result.Profile.Should().BeNull();
        result.DeviceStatus.Should().BeNull();
        result.Food.Should().BeNull();
        result.Settings.Should().BeNull();
    }

    [Fact]
    public async Task GetLastModifiedAsync_OnAShare_ReportsOnlyTheSharedCategories()
    {
        var now = DateTime.UtcNow;
        var dbName = $"nocturne_lastmod_{Guid.NewGuid()}";
        await using var context = TestDbContextFactory.CreateInMemoryContext(dbName);
        SeedLastModifiedData(context, now);
        var service = CreateStatusService(
            _configuration, context, dbName,
            httpContextAccessor: Caller(authenticated: false, Scope.GlucoseRead, Scope.TreatmentsRead));

        var result = await service.GetLastModifiedAsync();

        result.Entries.Should().NotBeNull();
        result.Treatments.Should().NotBeNull();
        result.Profile.Should().BeNull();
        result.DeviceStatus.Should().BeNull();
        result.Food.Should().BeNull();
        result.Settings.Should().BeNull();
    }

    [Fact]
    public async Task GetV3SystemStatusAsync_AnAuthenticatedCaller_IsToldCrudOnEveryCollection()
    {
        // See StatusService.GetV3ApiPermissions: AAPS stops uploading unless all six read "crud".
        var service = CreateStatusService(
            _configuration, httpContextAccessor: Caller(authenticated: true, Scope.GlucoseReadWrite));

        var result = await service.GetV3SystemStatusAsync();

        result.ApiPermissions.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["devicestatus"] = "crud",
            ["entries"] = "crud",
            ["food"] = "crud",
            ["profile"] = "crud",
            ["settings"] = "crud",
            ["treatments"] = "crud",
        });
    }

    [Fact]
    public async Task GetV3SystemStatusAsync_OnAShare_IsToldReadOnlyOnTheSharedCollections()
    {
        var service = CreateStatusService(
            _configuration, httpContextAccessor: Caller(authenticated: false, Scope.GlucoseRead));

        var result = await service.GetV3SystemStatusAsync();

        result.ApiPermissions.Should().BeEquivalentTo(new Dictionary<string, string> { ["entries"] = "r" });
    }

    private static IHttpContextAccessor Caller(bool authenticated, params string[] scopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.SetAuthContext(new AuthContext { IsAuthenticated = authenticated });
        httpContext.SetGrantedScopes(Scope.Normalize(scopes));
        if (!authenticated)
            httpContext.SetShareAccess();
        return new HttpContextAccessor { HttpContext = httpContext };
    }

    #endregion

    #region Configuration and Settings Tests

    [Fact]
    public async Task GetSystemStatusAsync_ReportsTheTenantDefaultOverTheInstanceConfig()
    {
        _tenants.Setup(t => t.GetDefaultGlucoseUnitsAsync(MockTenantAccessor.DefaultTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("mmol");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Display:Units"] = "mg/dl" })
            .Build();

        var result = await CreateStatusService(configuration, _dbContext).GetSystemStatusAsync();

        result.Settings!["units"].Should().Be("mmol");
        (await CreateStatusService(configuration, _dbContext).GetV3SystemStatusAsync())
            .Settings!["units"].Should().Be("mmol");
    }

    [Fact]
    public async Task GetSystemStatusAsync_ReportsTheOwnersOwnUnitsOverTheTenantDefault()
    {
        _tenants.Setup(t => t.GetDefaultGlucoseUnitsAsync(MockTenantAccessor.DefaultTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("mg/dl");

        var result = await CreateStatusService(
                new ConfigurationBuilder().Build(),
                appearance: new UserDisplayPreferences { GlucoseUnits = "mmol" })
            .GetSystemStatusAsync();

        result.Settings!["units"].Should().Be("mmol");
    }

    // A tenant upgraded from before the explicit default holds "mg/dl" in its features settings
    // from the old class default. Status must not read that as the owner's choice.
    [Fact]
    public async Task GetSystemStatusAsync_IgnoresTheLegacyFeaturesUnitsUntilTheOwnerChooses()
    {
        _dbContext.TenantId = MockTenantAccessor.DefaultTenantId;
        await new UISettingsService(new TestNocturneDbContextFactory(_dbContext), TimeProvider.System, Mock.Of<ILogger<UISettingsService>>()).PatchSectionAsync<FeatureSettings>(
            "features", JsonDocument.Parse("""{"display":{"units":"mmol"}}""").RootElement);
        var configuration = new ConfigurationBuilder().Build();

        var result = await CreateStatusService(configuration, _dbContext).GetSystemStatusAsync();

        result.Settings!["units"].Should().Be("mg/dl");

        _tenants.Setup(t => t.GetDefaultGlucoseUnitsAsync(MockTenantAccessor.DefaultTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("mmol");

        (await CreateStatusService(configuration, _dbContext).GetSystemStatusAsync())
            .Settings!["units"].Should().Be("mmol");
    }

    [Fact]
    public async Task GetSystemStatusAsync_FallsBackToMgDlWithNoTenantUnits()
    {
        var result = await CreateStatusService(new ConfigurationBuilder().Build(), _dbContext)
            .GetSystemStatusAsync();

        result.Settings!["units"].Should().Be("mg/dl");
    }

    [Fact]
    public async Task GetSystemStatusAsync_WithCustomDisplaySettings_ShouldIncludeInSettings()
    {
        var customConfigData = new Dictionary<string, string?>
        {
            ["Display:TimeFormat"] = "24",
            ["Display:NightMode"] = "true",
            ["Display:EditMode"] = "false",
            ["Display:Theme"] = "dark",
        };

        var customConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(customConfigData)
            .Build();

        var customService = CreateStatusService(customConfiguration, _dbContext);

        var result = await customService.GetSystemStatusAsync();

        result.Settings.Should().NotBeNull();
        result.Settings!.Should().ContainKey("timeFormat");
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
        var customConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alarms:UrgentHigh:Enabled"] = "false",
                ["Alarms:High:Enabled"] = "false",
                ["Alarms:Low:Enabled"] = "false",
            })
            .Build();

        var result = await CreateStatusService(customConfiguration, _dbContext).GetSystemStatusAsync();

        result.Settings.Should().NotBeNull();
        result.Settings!["alarmUrgentHigh"].Should().Be(false);
        result.Settings["alarmHigh"].Should().Be(false);
        result.Settings["alarmLow"].Should().Be(false);
    }

    [Theory]
    [InlineData("mmol", "mmol")]
    [InlineData("mg/dl", "mg/dl")]
    [InlineData(null, "mg/dl")]
    public async Task GetSystemStatusAsync_ReportsTheTenantOwnersUnits_NotTheInstanceConfiguration(
        string? ownerUnits, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Display:Units"] = "mmol" })
            .Build();
        var service = CreateStatusService(
            configuration,
            appearance: new UserDisplayPreferences { GlucoseUnits = ownerUnits });

        var result = await service.GetSystemStatusAsync();

        result.Settings!["units"].Should().Be(expected);
    }

    [Fact]
    public async Task GetSystemStatusAsync_WithoutTenantRules_ReportsTheConfiguredThresholds()
    {
        var customConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Thresholds:BgHigh"] = "300",
                ["Thresholds:BgTargetTop"] = "200",
                ["Thresholds:BgTargetBottom"] = "70",
                ["Thresholds:BgLow"] = "50",
            })
            .Build();

        var result = await CreateStatusService(customConfiguration).GetSystemStatusAsync();

        var thresholds = (Dictionary<string, object>)result.Settings!["thresholds"];
        thresholds["bgHigh"].Should().Be(300d);
        thresholds["bgTargetTop"].Should().Be(200d);
        thresholds["bgTargetBottom"].Should().Be(70d);
        thresholds["bgLow"].Should().Be(50d);
    }

    [Fact]
    public async Task GetSystemStatusAsync_ReportsTheTenantsThresholdRulesInMgdl()
    {
        var dbName = $"nocturne_status_thresholds_{Guid.NewGuid()}";
        var ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
        ctx.TenantId = MockTenantAccessor.DefaultTenantId;
        AddThresholdRule(ctx, "below", 60, AlertRuleSeverity.Critical);
        AddThresholdRule(ctx, "below", 90, AlertRuleSeverity.Warning);
        AddThresholdRule(ctx, "above", 170, AlertRuleSeverity.Warning);
        AddThresholdRule(ctx, "above", 230, AlertRuleSeverity.Critical);
        await ctx.SaveChangesAsync();

        var service = CreateStatusService(
            _configuration, ctx, dbName,
            appearance: new UserDisplayPreferences { GlucoseUnits = "mmol" });

        var result = await service.GetSystemStatusAsync();

        var thresholds = (Dictionary<string, object>)result.Settings!["thresholds"];
        thresholds["bgLow"].Should().Be(60d);
        thresholds["bgTargetBottom"].Should().Be(90d);
        thresholds["bgTargetTop"].Should().Be(170d);
        thresholds["bgHigh"].Should().Be(230d);
    }

    [Fact]
    public async Task GetSystemStatusAsync_GivesAShareAndAnAnonymousCallerTheDefaultsNotTheTenantRules()
    {
        var dbName = $"nocturne_status_callers_{Guid.NewGuid()}";
        var ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
        ctx.TenantId = MockTenantAccessor.DefaultTenantId;
        AddThresholdRule(ctx, "above", 170, AlertRuleSeverity.Warning);
        await ctx.SaveChangesAsync();
        var share = new Mock<ICategoryReadContext>();
        share.SetupGet(c => c.IsShare).Returns(true);

        var member = await CreateStatusService(_configuration, ctx, dbName).GetSystemStatusAsync();
        var shared = await CreateStatusService(_configuration, ctx, dbName, categoryReadContext: share.Object)
            .GetSystemStatusAsync();
        var anonymous = await CreateStatusService(
                _configuration, ctx, dbName,
                httpContextAccessor: new HttpContextAccessor { HttpContext = new DefaultHttpContext() })
            .GetSystemStatusAsync();

        ((Dictionary<string, object>)member.Settings!["thresholds"])["bgTargetTop"].Should().Be(170d);
        ((Dictionary<string, object>)shared.Settings!["thresholds"])["bgTargetTop"].Should().Be(180d);
        ((Dictionary<string, object>)anonymous.Settings!["thresholds"])["bgTargetTop"].Should().Be(180d);
    }

    [Fact]
    public async Task GetSystemStatusAsync_KeysTheCacheApartForCallersWhoCannotReadAlertRules()
    {
        var share = new Mock<ICategoryReadContext>();
        share.SetupGet(c => c.IsShare).Returns(true);
        var keys = new List<string>();
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StatusResponse?)null);
        _mockCacheService
            .Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<StatusResponse>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, StatusResponse, TimeSpan?, CancellationToken>((key, _, _, _) => keys.Add(key))
            .Returns(Task.CompletedTask);

        await CreateStatusService(_configuration).GetSystemStatusAsync();
        await CreateStatusService(_configuration, categoryReadContext: share.Object).GetSystemStatusAsync();
        await CreateStatusService(
                _configuration,
                httpContextAccessor: new HttpContextAccessor { HttpContext = new DefaultHttpContext() })
            .GetSystemStatusAsync();

        keys.Should().HaveCount(3);
        keys[0].Should().NotBe(keys[1], "a share must not be served the member result, or the reverse");
        keys[0].Should().NotBe(keys[2], "an anonymous caller must not be served the member result, or the reverse");
    }

    public static TheoryData<Exception> AlertRuleReadFailures => new()
    {
        new TestDbException(),
        new RetryLimitExceededException("retries exhausted", new TestDbException()),
    };

    [Theory]
    [MemberData(nameof(AlertRuleReadFailures))]
    public async Task GetSystemStatusAsync_WhenAlertRulesCannotBeRead_DegradesToTheDefaults(Exception failure)
    {
        var alerts = FailingAlertRepository(failure);

        var result = await CreateStatusService(_configuration, alertRepository: alerts).GetSystemStatusAsync();

        result.Status.Should().Be("ok");
        var thresholds = (Dictionary<string, object>)result.Settings!["thresholds"];
        thresholds["bgHigh"].Should().Be(260d);
        thresholds["bgTargetTop"].Should().Be(180d);
        thresholds["bgTargetBottom"].Should().Be(80d);
        thresholds["bgLow"].Should().Be(55d);
    }

    [Fact]
    public async Task GetSystemStatusAsync_WhenAlertRulesCannotBeRead_DoesNotCacheTheFallback()
    {
        var alerts = FailingAlertRepository(new TestDbException());

        await CreateStatusService(_configuration, alertRepository: alerts).GetSystemStatusAsync();

        _mockCacheService.Verify(
            x => x.SetAsync(It.IsAny<string>(), It.IsAny<StatusResponse>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetSystemStatusAsync_WhenAlertRulesReadSucceeds_CachesTheResult()
    {
        await CreateStatusService(_configuration).GetSystemStatusAsync();

        _mockCacheService.Verify(
            x => x.SetAsync(It.IsAny<string>(), It.IsAny<StatusResponse>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetSystemStatusAsync_ConcurrentColdReads_GenerateOnce()
    {
        var flights = new CacheFlights();
        var runs = 0;
        var held = new TaskCompletionSource<UserDisplayPreferences>();
        var appearance = new Mock<ITenantAppearanceReader>();
        appearance
            .Setup(r => r.GetTenantAppearanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref runs);
                return held.Task;
            });

        var reads = Enumerable.Range(0, 8)
            .Select(_ => CreateStatusService(_configuration, flights: flights, appearanceReader: appearance.Object)
                .GetSystemStatusAsync())
            .ToList();
        held.SetResult(new UserDisplayPreferences());
        var results = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(5));

        runs.Should().Be(1);
        results.Should().AllSatisfy(r => r.Should().BeSameAs(results[0]));
        _mockCacheService.Verify(
            x => x.SetAsync(It.IsAny<string>(), It.IsAny<StatusResponse>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetSystemStatusAsync_WhenAlertRulesCannotBeRead_TheNextReadGeneratesAgain()
    {
        var alerts = new Mock<IAlertRepository>();
        alerts
            .Setup(a => a.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TestDbException());
        var cache = TestTenantCache.Distributed(new MemoryCache(new MemoryCacheOptions()));
        var flights = new CacheFlights();
        StatusService Service() => new(
            _configuration, cache, flights, _mockDemoModeService.Object, Mock.Of<INocturneDbContextFactory>(),
            new TenantRepository(Mock.Of<INocturneDbContextFactory>()), new SubjectRepository(Mock.Of<INocturneDbContextFactory>()),
            new RoleRepository(Mock.Of<INocturneDbContextFactory>()), new OidcProviderRepository(Mock.Of<INocturneDbContextFactory>()),
            _httpContextAccessor, _mockTenantAccessor.Object, TestPublicAccessCache.Create(),
            new TenantGlucoseUnits(
                _mockTenantAccessor.Object,
                Mock.Of<ITenantAppearanceReader>(r =>
                    r.GetTenantAppearanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())
                        == Task.FromResult(new UserDisplayPreferences())),
                _tenants.Object),
            new GlucoseStatusClassifier(_configuration, NullLogger<GlucoseStatusClassifier>.Instance),
            alerts.Object, Mock.Of<ICategoryReadContext>(), TimeProvider.System, _mockLogger.Object);

        await Service().GetSystemStatusAsync();
        await Service().GetSystemStatusAsync();

        alerts.Verify(a => a.GetEnabledRulesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetSystemStatusAsync_WhenAlertRulesReadHitsABug_DoesNotHideIt()
    {
        var alerts = FailingAlertRepository(new InvalidOperationException("bug"));

        var act = () => CreateStatusService(_configuration, alertRepository: alerts).GetSystemStatusAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static IAlertRepository FailingAlertRepository(Exception failure)
    {
        var alerts = new Mock<IAlertRepository>();
        alerts
            .Setup(a => a.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        return alerts.Object;
    }

    [Fact]
    public async Task GetSystemStatusAsync_WhenTheCallerCancels_DoesNotSwallowIt()
    {
        var alerts = FailingAlertRepository(new OperationCanceledException());

        var act = () => CreateStatusService(_configuration, alertRepository: alerts).GetSystemStatusAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class TestDbException : System.Data.Common.DbException;

    private static void AddThresholdRule(
        NocturneDbContext ctx, string direction, int value, AlertRuleSeverity severity) =>
        ctx.AlertRules.Add(new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = MockTenantAccessor.DefaultTenantId,
            Name = $"{direction}-{value}",
            IsEnabled = true,
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = JsonSerializer.Serialize(new { direction, value }),
            Severity = severity,
        });

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
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
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
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
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
        enabledFeatures.Should().NotContain(EnabledFeatures.Cors, "the legacy API answers foreign origins only when the instance enables cors");
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task GetSystemStatusAsync_CacheServiceThrows_ShouldPropagateException()
    {
        // Arrange
        _mockCacheService
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
            .ThrowsAsync(new InvalidOperationException("Cache service unavailable"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _statusService.GetSystemStatusAsync()
        );

        // Verify cache operations were attempted
        _mockCacheService.Verify(
            x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default),
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
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
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
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
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
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
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
            .Setup(x => x.GetAsync<StatusResponse>("status:system:00000000-0000-0000-0000-000000000001", default))
            .ReturnsAsync((StatusResponse?)null);

        // Act
        var result = await _statusService.GetSystemStatusAsync();

        // Assert
        _mockCacheService.Verify(
            x => x.SetAsync("status:system:00000000-0000-0000-0000-000000000001", result, TimeSpan.FromMinutes(2), default),
            Times.Once
        );
    }

    #endregion

    #region Helper Methods

    private static void SeedLastModifiedData(NocturneDbContext context, DateTime now)
    {
        // Set tenant ID on the context so entities pass the tenant validation on save
        context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        var baseMills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        context.SensorGlucose.Add(
            new Nocturne.Infrastructure.Data.Entities.V4.SensorGlucoseEntity
            {
                Id = Guid.CreateVersion7(),
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(baseMills).UtcDateTime,
                Mgdl = 100,
                SysUpdatedAt = now.AddMinutes(-5),
            }
        );

        context.Boluses.Add(
            new Nocturne.Infrastructure.Data.Entities.V4.BolusEntity
            {
                Id = Guid.CreateVersion7(),
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(baseMills).UtcDateTime,
                Insulin = 2.5,
                SysUpdatedAt = now.AddMinutes(-3),
            }
        );

        context.TherapySettings.Add(
            new Nocturne.Infrastructure.Data.Entities.V4.TherapySettingsEntity
            {
                Id = Guid.CreateVersion7(),
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(baseMills).UtcDateTime,
                ProfileName = "Default",
                SysUpdatedAt = now.AddHours(-1),
            }
        );

        context.ApsSnapshots.Add(
            new ApsSnapshotEntity
            {
                Id = Guid.CreateVersion7(),
                Device = "dexcom",
                AidAlgorithm = "OpenAPS",
                Timestamp = now.AddMinutes(-2),
                SysCreatedAt = now.AddMinutes(-2),
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


        context.SaveChanges();
    }

    private StatusService CreateStatusService(
        IConfiguration configuration,
        NocturneDbContext? context = null,
        string? databaseName = null,
        ITenantAccessor? tenantAccessor = null,
        IHttpContextAccessor? httpContextAccessor = null,
        UserDisplayPreferences? appearance = null,
        ICategoryReadContext? categoryReadContext = null,
        IAlertRepository? alertRepository = null,
        CacheFlights? flights = null,
        ITenantAppearanceReader? appearanceReader = null,
        TimeProvider? time = null
    )
    {
        var dbName = databaseName ?? $"nocturne_status_tests_{Guid.NewGuid()}";
        var ctx = context ?? TestDbContextFactory.CreateInMemoryContext(dbName);
        NocturneDbContext FactoryContext()
        {
            var factoryCtx = TestDbContextFactory.CreateInMemoryContext(dbName);
            factoryCtx.TenantId = ctx.TenantId;
            return factoryCtx;
        }
        var mockTenantDbContextFactory = new Mock<INocturneDbContextFactory>();
        mockTenantDbContextFactory
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FactoryContext);
        mockTenantDbContextFactory
            .Setup(f => f.CreatePlatformAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FactoryContext);
        appearanceReader ??= Mock.Of<ITenantAppearanceReader>(r =>
            r.GetTenantAppearanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())
                == Task.FromResult(appearance ?? new UserDisplayPreferences()));
        var alertContexts = new Mock<IDbContextFactory<NocturneDbContext>>();
        alertContexts
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TestDbContextFactory.CreateInMemoryContext(dbName));
        tenantAccessor ??= _mockTenantAccessor.Object;
        alertRepository ??= new AlertRepository(alertContexts.Object.AsNocturneFactory(tenantAccessor.TenantId));
        return new StatusService(
            configuration,
            _mockCacheService.Object,
            flights ?? new CacheFlights(),
            _mockDemoModeService.Object,
            mockTenantDbContextFactory.Object,
            new TenantRepository(mockTenantDbContextFactory.Object), new SubjectRepository(mockTenantDbContextFactory.Object), new RoleRepository(mockTenantDbContextFactory.Object), new OidcProviderRepository(mockTenantDbContextFactory.Object),
            httpContextAccessor ?? _httpContextAccessor,
            tenantAccessor,
            TestPublicAccessCache.Create(),
            new TenantGlucoseUnits(tenantAccessor, appearanceReader, _tenants.Object),
            new GlucoseStatusClassifier(configuration, NullLogger<GlucoseStatusClassifier>.Instance),
            alertRepository,
            categoryReadContext ?? Mock.Of<ICategoryReadContext>(),
            time ?? TimeProvider.System,
            _mockLogger.Object
        );
    }

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
