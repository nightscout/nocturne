using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles;

/// <summary>
/// Coverage for the storage layout of <see cref="UISettingsService"/>: one owning row per value, the
/// aggregate assembled on read, and alarm configuration copies an earlier layout left in the
/// notifications row.
/// </summary>
[Trait("Category", "Unit")]
public class UISettingsServiceTests
{
    private const string NotificationsKey = "ui:settings:notifications";
    private const string AlarmsKey = "ui:settings:notifications:alarms";
    private const string ServicesKey = "ui:settings:services";
    private const string FeaturesKey = "ui:settings:features";
    private const string DevicesKey = "ui:settings:devices";

    private static readonly Guid TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task SaveAlarmConfigurationAsync_isVisibleThroughEveryReadPath()
    {
        var context = NewContext();
        var service = NewService(context);

        await service.SaveAlarmConfigurationAsync(AlarmConfiguration("fresh", 123));

        foreach (var config in await EveryAlarmReadPath(service))
        {
            config.Profiles.Should().ContainSingle().Which.Threshold.Should().Be(123);
        }
    }

    [Fact]
    public async Task SaveAlarmConfigurationAsync_preservesTheEmergencyContactVisualSettings()
    {
        var context = NewContext();
        var service = NewService(context);
        var config = AlarmConfiguration("fresh", 123);
        config.Profiles[0].Visual = new AlarmVisualSettings
        {
            ShowEmergencyContacts = true,
            EmergencyInstructions = "Spare key is under the mat",
        };

        await service.SaveAlarmConfigurationAsync(config);

        foreach (var stored in await EveryAlarmReadPath(service))
        {
            var visual = stored.Profiles.Should().ContainSingle().Subject.Visual;
            visual.ShowEmergencyContacts.Should().BeTrue();
            visual.EmergencyInstructions.Should().Be("Spare key is under the mat");
        }
    }

    [Fact]
    public async Task SaveAlarmConfigurationAsync_winsOverCopiesLeftByTheEarlierLayout()
    {
        var context = NewContext();
        var stale = AlarmConfiguration("stale", 55);
        Seed(context, NotificationsKey, new NotificationSettings { AlarmConfiguration = stale });
        Seed(context, AlarmsKey, stale);
        await context.SaveChangesAsync();

        var service = NewService(context);
        await service.SaveAlarmConfigurationAsync(AlarmConfiguration("fresh", 123));

        foreach (var config in await EveryAlarmReadPath(service))
        {
            config.Profiles.Should().ContainSingle().Which.Threshold.Should().Be(123);
        }
    }

    [Fact]
    public async Task PatchSectionAsync_ofNotificationsReplacesTheAlarmProfilesForEveryReadPath()
    {
        var context = NewContext();
        var service = NewService(context);

        await service.SaveAlarmConfigurationAsync(AlarmConfiguration("first", 55));
        await service.PatchSectionAsync<NotificationSettings>(
            UISettingsSections.Notifications,
            Json(JsonSerializer.Serialize(
                new NotificationSettings { AlarmConfiguration = AlarmConfiguration("second", 123) },
                JsonOptions
            ))
        );

        foreach (var config in await EveryAlarmReadPath(service))
        {
            config.Profiles.Should().ContainSingle().Which.Threshold.Should().Be(123);
        }
    }

    [Fact]
    public async Task SaveSettingsAsync_replacesTheAlarmConfigurationForEveryReadPath()
    {
        var context = NewContext();
        var service = NewService(context);

        await service.SaveAlarmConfigurationAsync(AlarmConfiguration("first", 55));

        var settings = (await service.GetSettingsAsync())!;
        settings.Notifications.AlarmConfiguration = AlarmConfiguration("second", 123);
        await service.SaveSettingsAsync(settings);

        foreach (var config in await EveryAlarmReadPath(service))
        {
            config.Profiles.Should().ContainSingle().Which.Threshold.Should().Be(123);
        }
    }

    [Fact]
    public async Task WholeDocumentRoundTrip_preservesTheAlarmConfiguration()
    {
        var context = NewContext();
        var service = NewService(context);

        await service.SaveAlarmConfigurationAsync(AlarmConfiguration("fresh", 123));
        await service.SaveSettingsAsync((await service.GetSettingsAsync())!);

        foreach (var config in await EveryAlarmReadPath(service))
        {
            config.Profiles.Should().ContainSingle().Which.Threshold.Should().Be(123);
        }
    }

    [Fact]
    public async Task SaveSettingsAsync_storesTheAlarmConfigurationInOneRowOnly()
    {
        var context = NewContext();
        var service = NewService(context);

        await service.SaveSettingsAsync(
            new UISettingsConfiguration
            {
                Notifications = new NotificationSettings
                {
                    AlarmConfiguration = AlarmConfiguration("fresh", 123),
                },
            }
        );

        StoredRows(context)
            .Where(r => r.Value!.Contains("alarmConfiguration", StringComparison.Ordinal))
            .Should()
            .BeEmpty();
        StoredRows(context).Select(r => r.Key).Should().Contain(AlarmsKey);
    }

    [Fact]
    public async Task SaveSettingsAsync_roundTripsEverySection()
    {
        var context = NewContext();
        var service = NewService(context);

        await service.SaveSettingsAsync(
            new UISettingsConfiguration
            {
                Devices = new DeviceSettings { AutoConnect = false },
                Algorithm = new AlgorithmSettings
                {
                    Prediction = new PredictionSettings { Minutes = 45 },
                },
                Features = new FeatureSettings
                {
                    Display = new DisplaySettings { Units = "mmol/L" },
                },
                Notifications = new NotificationSettings
                {
                    AlarmConfiguration = AlarmConfiguration("fresh", 123),
                },
                Services = new ServicesSettings
                {
                    SyncSettings = new SyncSettings { AutoSync = false },
                },
                DataQuality = new DataQualitySettings
                {
                    SleepSchedule = new SleepScheduleSettings { BedtimeHour = 1 },
                },
            }
        );

        var stored = (await service.GetSettingsAsync())!;

        stored.Devices.AutoConnect.Should().BeFalse();
        stored.Algorithm.Prediction.Minutes.Should().Be(45);
        stored.Features.Display.Units.Should().Be("mmol/L");
        stored.Notifications.AlarmConfiguration.Profiles.Should().ContainSingle()
            .Which.Threshold.Should().Be(123);
        stored.Services.SyncSettings.AutoSync.Should().BeFalse();
        stored.DataQuality.SleepSchedule.BedtimeHour.Should().Be(1);
    }

    [Fact]
    public async Task GetAlarmConfigurationAsync_prefersTheOwningRowOverTheEarlierLayoutsCopies()
    {
        var context = NewContext();
        Seed(
            context,
            NotificationsKey,
            new NotificationSettings { AlarmConfiguration = AlarmConfiguration("section", 55) }
        );
        Seed(context, AlarmsKey, AlarmConfiguration("owned", 123));
        await context.SaveChangesAsync();

        var service = NewService(context);

        foreach (var config in await EveryAlarmReadPath(service))
        {
            config.Profiles.Should().ContainSingle().Which.Threshold.Should().Be(123);
        }
    }

    [Fact]
    public async Task No_read_returns_pushover_credentials_a_stored_configuration_carries()
    {
        var context = NewContext();
        Seed(context, AlarmsKey, new
        {
            profiles = new[] { new { id = "low", name = "low", alarmType = "Low", threshold = 70 } },
            channels = new { pushover = new { enabled = true, userKey = "u-secret", apiToken = "a-secret" } },
        });
        await context.SaveChangesAsync();

        var service = NewService(context);

        var reads = (await EveryAlarmReadPath(service)).Select(c => JsonSerializer.Serialize(c, JsonOptions))
            .Append(JsonSerializer.Serialize(await service.GetSettingsAsync(), JsonOptions));
        foreach (var json in reads)
        {
            json.Should().NotContain("secret").And.NotContain("apiToken").And.NotContain("userKey");
        }
    }

    [Fact]
    public async Task PatchSectionAsync_changesOnlyTheNamedField()
    {
        var context = NewContext();
        var service = NewService(context);
        Seed(
            context,
            FeaturesKey,
            new FeatureSettings { Display = { FocusHours = 3 }, TrackerPills = { Enabled = false } }
        );
        await context.SaveChangesAsync();

        var patched = await service.PatchSectionAsync<FeatureSettings>(
            UISettingsSections.Features,
            Json("""{"display":{"focusHours":6}}""")
        );

        patched.Display.FocusHours.Should().Be(6);
        patched.TrackerPills.Enabled.Should().BeFalse();
        var stored = (await service.GetSettingsAsync())!.Features;
        stored.Display.FocusHours.Should().Be(6);
        stored.TrackerPills.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task PatchSectionAsync_aNullResetsTheFieldToItsDefault()
    {
        var context = NewContext();
        var service = NewService(context);
        Seed(
            context,
            FeaturesKey,
            new FeatureSettings { Display = { FocusHours = 3 }, TrackerPills = { Enabled = false } }
        );
        await context.SaveChangesAsync();

        var patched = await service.PatchSectionAsync<FeatureSettings>(
            UISettingsSections.Features,
            Json("""{"display":{"focusHours":null}}""")
        );

        patched.Display.FocusHours.Should().Be(new FeatureSettings().Display.FocusHours);
        patched.TrackerPills.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task PatchSectionAsync_aSectionNobodyHasSavedPatchesTheDefaults()
    {
        var context = NewContext();

        var patched = await NewService(context).PatchSectionAsync<DataQualitySettings>(
            UISettingsSections.DataQuality,
            Json("""{"sleepSchedule":{"bedtimeHour":1}}""")
        );

        patched.SleepSchedule.BedtimeHour.Should().Be(1);
        patched.SleepSchedule.WakeTimeHour.Should().Be(new DataQualitySettings().SleepSchedule.WakeTimeHour);
        StoredRows(context).Select(r => r.Key).Should().BeEquivalentTo(["ui:settings:dataquality"]);
    }

    [Fact]
    public async Task PatchSectionAsync_omitsWhatTheSectionsRowDoesNotCarry()
    {
        var context = NewContext();

        await NewService(context).PatchSectionAsync<NotificationSettings>(
            UISettingsSections.Notifications,
            Json("""{"alarmConfiguration":{"globalVolume":10}}""")
        );

        var rows = StoredRows(context).ToDictionary(r => r.Key, r => r.Value);
        rows[NotificationsKey].Should().NotContain("alarmConfiguration");
        rows[AlarmsKey].Should().Contain("\"globalVolume\":10");
    }

    [Theory]
    [InlineData("dataQaulity")]
    [InlineData("alarms")]
    public async Task PatchSectionAsync_refusesANameNoSectionOwns(string name)
    {
        var context = NewContext();

        var patch = () => NewService(context).PatchSectionAsync<DataQualitySettings>(name, Json("{}"));

        await patch.Should().ThrowAsync<ArgumentException>();
        StoredRows(context).Should().BeEmpty();
    }

    [Fact]
    public async Task PatchSectionAsync_refusesATypeThatIsNotTheSections()
    {
        var context = NewContext();

        var patch = () => NewService(context)
            .PatchSectionAsync<FeatureSettings>(UISettingsSections.DataQuality, Json("{}"));

        await patch.Should().ThrowAsync<ArgumentException>();
        StoredRows(context).Should().BeEmpty();
    }

    [Fact]
    public async Task PatchSectionAsync_refusesAPatchThatIsNotAnObject()
    {
        var context = NewContext();

        var patch = () => NewService(context)
            .PatchSectionAsync<FeatureSettings>(UISettingsSections.Features, Json("[1]"));

        await patch.Should().ThrowAsync<ArgumentException>();
        StoredRows(context).Should().BeEmpty();
    }

    [Fact]
    public async Task PatchSectionAsync_aValueOfTheWrongTypeChangesNothing()
    {
        var context = NewContext();
        var service = NewService(context);
        Seed(context, FeaturesKey, new FeatureSettings { Display = { FocusHours = 3 } });
        await context.SaveChangesAsync();

        var patch = () => service.PatchSectionAsync<FeatureSettings>(
            UISettingsSections.Features,
            Json("""{"display":{"focusHours":"soon"}}""")
        );

        await patch.Should().ThrowAsync<ArgumentException>();
        (await service.GetSettingsAsync())!.Features.Display.FocusHours.Should().Be(3);
    }

    [Fact]
    public async Task PatchSectionAsync_aStoredRowThatDoesNotReadIsNotThePatchsFault()
    {
        var context = NewContext();
        var service = NewService(context);
        context.Settings.Add(
            new SettingsEntity
            {
                Id = Guid.CreateVersion7(),
                Key = FeaturesKey,
                Value = """{"display":{"focusHours":"soon"}}""",
                IsActive = true,
            });
        await context.SaveChangesAsync();

        var patch = () => service.PatchSectionAsync<FeatureSettings>(
            UISettingsSections.Features,
            Json("""{"trackerPills":{"enabled":false}}""")
        );

        await patch.Should().ThrowAsync<JsonException>();
    }

    [Theory]
    [InlineData("""{"Display":{"FocusHours":6}}""")]
    [InlineData("""{"display":{"focushours":6}}""")]
    [InlineData("""{"display":{"noSuchField":6}}""")]
    public async Task PatchSectionAsync_refusesAKeyThatIsNotAPropertyNameAndChangesNothing(string body)
    {
        var context = NewContext();
        var service = NewService(context);

        var patch = () => service.PatchSectionAsync<FeatureSettings>(UISettingsSections.Features, Json(body));

        await patch.Should().ThrowAsync<ArgumentException>();
        StoredRows(context).Should().BeEmpty();
    }

    [Fact]
    public async Task PatchSectionAsync_appliesAnExactCamelCaseKeyNestedInTheSection()
    {
        var context = NewContext();
        var service = NewService(context);

        var patched = await service.PatchSectionAsync<FeatureSettings>(
            UISettingsSections.Features,
            Json("""{"display":{"focusHours":6}}""")
        );

        patched.Display.FocusHours.Should().Be(6);
    }

    [Fact]
    public async Task PatchSectionAsync_acceptsEveryRegisteredSection()
    {
        var context = NewContext();
        var service = NewService(context);
        var patchSection = typeof(UISettingsService).GetMethod(nameof(UISettingsService.PatchSectionAsync))!;

        foreach (var section in UISettingsSections.All)
        {
            var patch = () =>
                (Task)patchSection
                    .MakeGenericMethod(section.Type)
                    .Invoke(service, [section.Name, Json("{}"), CancellationToken.None])!;

            await patch.Should().NotThrowAsync($"section {section.Name} should be writable");
        }

        await service.PatchSectionAsync<DataQualitySettings>(
            "DATAQUALITY",
            Json("""{"sleepSchedule":{"bedtimeHour":1}}""")
        );

        (await service.GetSettingsAsync())!.DataQuality.SleepSchedule.BedtimeHour.Should().Be(1);
    }

    [Theory]
    [InlineData("alarms")]
    [InlineData("alarmConfiguration")]
    [InlineData("AlarmConfiguration")]
    [InlineData("ALARMS")]
    public async Task GetSectionAsync_readsEveryAlarmAliasFromTheOwningRow(string alias)
    {
        var context = NewContext();
        var service = NewService(context);

        await service.SaveAlarmConfigurationAsync(AlarmConfiguration("aliased", 123));

        (await service.GetSectionAsync<UserAlarmConfiguration>(alias))!
            .Profiles.Should().ContainSingle().Which.Threshold.Should().Be(123);
        StoredRows(context).Select(r => r.Key).Should().Equal([AlarmsKey]);
    }

    [Fact]
    public async Task SaveAlarmConfigurationAsync_revivesADeactivatedRow()
    {
        var context = NewContext();
        var row = Seed(context, AlarmsKey, AlarmConfiguration("stale", 55));
        row.IsActive = false;
        await context.SaveChangesAsync();

        var service = NewService(context);
        await service.SaveAlarmConfigurationAsync(AlarmConfiguration("fresh", 123));

        foreach (var config in await EveryAlarmReadPath(service))
        {
            config.Profiles.Should().ContainSingle().Which.Threshold.Should().Be(123);
        }
    }

    [Fact]
    public async Task GetSettingsAsync_returnsDefaults_whenNothingIsStored()
    {
        var service = NewService(NewContext());

        var stored = (await service.GetSettingsAsync())!;

        stored.Should().NotBeNull();
        stored.Should().BeEquivalentTo(new UISettingsConfiguration());
    }

    [Fact]
    public async Task SaveSettingsAsync_doesNotStoreTheConnectorCatalog()
    {
        var context = NewContext();
        var service = NewService(context);

        await service.SaveSettingsAsync(
            new UISettingsConfiguration
            {
                Services = new ServicesSettings
                {
                    AvailableServices = [Catalogued("dexcom-connector")],
                    SyncSettings = new SyncSettings { AutoSync = false },
                },
            }
        );

        StoredRows(context).Select(r => r.Key).Should().Contain(ServicesKey);
        StoredRows(context)
            .Where(r => r.Value!.Contains("availableServices", StringComparison.Ordinal))
            .Should()
            .BeEmpty();
        StoredRows(context)
            .Where(r => r.Value!.Contains("dexcom-connector", StringComparison.Ordinal))
            .Should()
            .BeEmpty();

        var stored = (await service.GetSettingsAsync())!;
        stored.Services.AvailableServices.Should().BeEmpty();
        stored.Services.SyncSettings.AutoSync.Should().BeFalse();
    }

    [Fact]
    public async Task PatchSectionAsync_doesNotStoreTheConnectorCatalog()
    {
        var context = NewContext();
        var service = NewService(context);

        await service.PatchSectionAsync<ServicesSettings>(
            "services",
            Json(JsonSerializer.Serialize(
                new ServicesSettings { AvailableServices = [Catalogued("dexcom-connector")] },
                JsonOptions
            ))
        );

        StoredRows(context).Select(r => r.Key).Should().Contain(ServicesKey);
        StoredRows(context)
            .Where(r => r.Value!.Contains("availableServices", StringComparison.Ordinal))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task SaveSettingsAsync_dropsACatalogSnapshotAnEarlierVersionStored()
    {
        var context = NewContext();
        Seed(
            context,
            ServicesKey,
            new ServicesSettings { AvailableServices = [Catalogued("retired-connector")] }
        );
        await context.SaveChangesAsync();

        var service = NewService(context);
        await service.SaveSettingsAsync((await service.GetSettingsAsync())!);

        StoredRows(context)
            .Where(r => r.Value!.Contains("retired-connector", StringComparison.Ordinal))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task GetSettingsAsync_dropsTheTopGridRowsAnEarlierVersionStored()
    {
        var context = NewContext();
        Seed(
            context,
            FeaturesKey,
            new FeatureSettings
            {
                Widgets =
                [
                    new()
                    {
                        Id = WidgetId.BgDelta,
                        Enabled = true,
                        Placement = WidgetPlacement.Top,
                    },
                    new()
                    {
                        Id = WidgetId.Statistics,
                        Enabled = false,
                        Placement = WidgetPlacement.Main,
                    },
                ],
            }
        );
        await context.SaveChangesAsync();

        var stored = (await NewService(context).GetSettingsAsync())!;

        stored
            .Features.Widgets.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(new { Id = WidgetId.Statistics, Enabled = false });
    }

    /// <summary>
    /// A features row whose widget list is an explicit JSON null binds over the property
    /// initialiser, so the narrowing on read has to survive it: the aggregate is assembled in one
    /// try block, and anything thrown there costs the caller every section, not just this one.
    /// </summary>
    [Fact]
    public async Task GetSettingsAsync_readsAFeaturesRowWithNoWidgetListAtAll()
    {
        var context = NewContext();
        Seed(context, FeaturesKey, new { widgets = (object?)null });
        Seed(context, DevicesKey, new DeviceSettings { AutoConnect = false });
        await context.SaveChangesAsync();

        var stored = (await NewService(context).GetSettingsAsync())!;

        stored.Features.Widgets.Should().BeEmpty();
        stored.Devices.AutoConnect.Should().BeFalse();
    }

    [Fact]
    public void OmittedProperties_nameJsonPropertiesTheirSectionDeclares()
    {
        foreach (var section in UISettingsSections.All)
        {
            var declared = section
                .Type.GetProperties()
                .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
                .Where(name => name != null)
                .ToList();

            section
                .OmittedProperties.Should()
                .BeSubsetOf(declared!, $"section {section.Name} can only omit what it declares");
        }
    }

    /// <summary>
    /// A failed read and a tenant that has saved nothing are different answers, as
    /// <see cref="IUISettingsService.GetAlarmConfigurationAsync"/> already has them: a caller that
    /// cannot run on defaults has to be able to tell the two apart.
    /// </summary>
    [Fact]
    public async Task GetSettingsAsync_returnsNullWhenTheReadFails()
    {
        var context = NewContext();
        var service = NewService(context);
        await context.DisposeAsync();

        (await service.GetSettingsAsync()).Should().BeNull();
    }

    // ----- helpers -----

    private static AvailableService Catalogued(string id)
    {
        return new AvailableService
        {
            Id = id,
            Name = id,
            Type = "cgm",
            Icon = id,
        };
    }

    /// <summary>
    /// The alarm configuration as returned by every public read path, so a copy that goes stale
    /// behind any one of them fails the assertion.
    /// </summary>
    private static async Task<IReadOnlyList<UserAlarmConfiguration>> EveryAlarmReadPath(
        UISettingsService service
    )
    {
        return
        [
            (await service.GetAlarmConfigurationAsync())!,
            (await service.GetSettingsAsync())!.Notifications.AlarmConfiguration,
            (await service.GetNotificationSettingsAsync()).AlarmConfiguration,
            (await service.GetSectionAsync<NotificationSettings>("notifications"))!
                .AlarmConfiguration,
            (await service.GetSectionAsync<UserAlarmConfiguration>("alarms"))!,
        ];
    }

    private static UserAlarmConfiguration AlarmConfiguration(string profileId, int threshold)
    {
        return new UserAlarmConfiguration
        {
            Profiles =
            [
                new AlarmProfileConfiguration
                {
                    Id = profileId,
                    Name = profileId,
                    AlarmType = AlarmTriggerType.Low,
                    Threshold = threshold,
                },
            ],
        };
    }

    private static SettingsEntity Seed(NocturneDbContext context, string key, object value)
    {
        var entity = new SettingsEntity
        {
            Id = Guid.CreateVersion7(),
            Key = key,
            Value = JsonSerializer.Serialize(value, JsonOptions),
            IsActive = true,
        };

        context.Settings.Add(entity);
        return entity;
    }

    private static List<SettingsEntity> StoredRows(NocturneDbContext context)
    {
        return context.Settings.AsNoTracking().Where(s => s.Value != null).ToList();
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static NocturneDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new NocturneDbContext(options) { TenantId = TenantId };
    }

    private static UISettingsService NewService(NocturneDbContext context)
    {
        return new UISettingsService(context.AsFactory(), TimeProvider.System, NullLogger<UISettingsService>.Instance);
    }
}
