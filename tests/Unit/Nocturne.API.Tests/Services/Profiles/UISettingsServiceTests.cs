using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles;

/// <summary>
/// Coverage for the storage layout of <see cref="UISettingsService"/>: one owning row per value, the
/// aggregate assembled on read, and rows written by the earlier layout (a whole-document
/// <c>ui:settings:complete</c> blob plus a mirrored copy of the alarm configuration) still read
/// correctly.
/// </summary>
[Trait("Category", "Unit")]
public class UISettingsServiceTests
{
    private const string LegacyAggregateKey = "ui:settings:complete";
    private const string NotificationsKey = "ui:settings:notifications";
    private const string AlarmsKey = "ui:settings:notifications:alarms";

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
    public async Task SaveAlarmConfigurationAsync_winsOverCopiesLeftByTheEarlierLayout()
    {
        var context = NewContext();
        var stale = AlarmConfiguration("stale", 55);
        Seed(context, LegacyAggregateKey, LegacyAggregate(stale));
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
    public async Task SaveNotificationSettingsAsync_replacesTheAlarmConfigurationForEveryReadPath()
    {
        var context = NewContext();
        var service = NewService(context);

        await service.SaveAlarmConfigurationAsync(AlarmConfiguration("first", 55));
        await service.SaveNotificationSettingsAsync(
            new NotificationSettings { AlarmConfiguration = AlarmConfiguration("second", 123) }
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

        var settings = await service.GetSettingsAsync();
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
        await service.SaveSettingsAsync(await service.GetSettingsAsync());

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
        StoredRows(context).Select(r => r.Key).Should().NotContain(LegacyAggregateKey);
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
                Security = new SecuritySettings { RequireAuthForPublicAccess = true },
            }
        );

        var stored = await service.GetSettingsAsync();

        stored.Devices.AutoConnect.Should().BeFalse();
        stored.Algorithm.Prediction.Minutes.Should().Be(45);
        stored.Features.Display.Units.Should().Be("mmol/L");
        stored.Notifications.AlarmConfiguration.Profiles.Should().ContainSingle()
            .Which.Threshold.Should().Be(123);
        stored.Services.SyncSettings.AutoSync.Should().BeFalse();
        stored.DataQuality.SleepSchedule.BedtimeHour.Should().Be(1);
        stored.Security.RequireAuthForPublicAccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetSettingsAsync_readsSectionsWrittenByTheEarlierLayout()
    {
        var context = NewContext();
        var legacy = LegacyAggregate(AlarmConfiguration("legacy", 88));
        legacy.Devices.AutoConnect = false;
        legacy.DataQuality.SleepSchedule.BedtimeHour = 1;
        legacy.Security.RequireAuthForPublicAccess = true;
        Seed(context, LegacyAggregateKey, legacy);
        await context.SaveChangesAsync();

        var service = NewService(context);
        var stored = await service.GetSettingsAsync();

        stored.Devices.AutoConnect.Should().BeFalse();
        stored.DataQuality.SleepSchedule.BedtimeHour.Should().Be(1);
        stored.Security.RequireAuthForPublicAccess.Should().BeTrue();

        foreach (var config in await EveryAlarmReadPath(service))
        {
            config.Profiles.Should().ContainSingle().Which.Threshold.Should().Be(88);
        }
    }

    [Fact]
    public async Task GetAlarmConfigurationAsync_prefersTheOwningRowOverTheEarlierLayoutsCopies()
    {
        var context = NewContext();
        Seed(context, LegacyAggregateKey, LegacyAggregate(AlarmConfiguration("aggregate", 44)));
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
    public async Task SaveSectionAsync_leavesTheOtherSectionsOfTheEarlierLayoutReadable()
    {
        var context = NewContext();
        var legacy = LegacyAggregate(AlarmConfiguration("legacy", 88));
        legacy.Devices.AutoConnect = false;
        legacy.DataQuality.SleepSchedule.BedtimeHour = 1;
        Seed(context, LegacyAggregateKey, legacy);
        await context.SaveChangesAsync();

        var service = NewService(context);
        await service.SaveSectionAsync(
            "features",
            new FeatureSettings { Display = new DisplaySettings { Units = "mmol/L" } }
        );

        var stored = await service.GetSettingsAsync();
        stored.Features.Display.Units.Should().Be("mmol/L");
        stored.Devices.AutoConnect.Should().BeFalse();
        stored.DataQuality.SleepSchedule.BedtimeHour.Should().Be(1);
    }

    [Fact]
    public async Task SaveSectionAsync_refusesANameNoSectionOwns()
    {
        var context = NewContext();
        var service = NewService(context);

        var save = () => service.SaveSectionAsync("dataQaulity", new DataQualitySettings());

        await save.Should().ThrowAsync<ArgumentException>();
        StoredRows(context).Should().BeEmpty();
    }

    [Fact]
    public async Task SaveSectionAsync_acceptsEveryRegisteredSection()
    {
        var context = NewContext();
        var service = NewService(context);

        foreach (var section in UISettingsSections.All)
        {
            var save = () =>
                service.SaveSectionAsync(
                    section.Name,
                    Activator.CreateInstance(section.Type)!,
                    CancellationToken.None
                );

            await save.Should().NotThrowAsync($"section {section.Name} should be writable");
        }

        await service.SaveSectionAsync(
            "DATAQUALITY",
            new DataQualitySettings
            {
                SleepSchedule = new SleepScheduleSettings { BedtimeHour = 1 },
            }
        );

        (await service.GetSettingsAsync()).DataQuality.SleepSchedule.BedtimeHour.Should().Be(1);
    }

    [Theory]
    [InlineData("alarms")]
    [InlineData("alarmConfiguration")]
    [InlineData("AlarmConfiguration")]
    [InlineData("ALARMS")]
    public async Task SaveSectionAsync_routesEveryAlarmAliasToTheOwningRow(string alias)
    {
        var context = NewContext();
        var service = NewService(context);

        await service.SaveSectionAsync(alias, AlarmConfiguration("aliased", 123));

        foreach (var config in await EveryAlarmReadPath(service))
        {
            config.Profiles.Should().ContainSingle().Which.Threshold.Should().Be(123);
        }

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

        var stored = await service.GetSettingsAsync();

        stored.Should().BeEquivalentTo(new UISettingsConfiguration());
    }

    // ----- helpers -----

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
            (await service.GetSettingsAsync()).Notifications.AlarmConfiguration,
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

    private static UISettingsConfiguration LegacyAggregate(UserAlarmConfiguration alarms)
    {
        return new UISettingsConfiguration
        {
            Notifications = new NotificationSettings { AlarmConfiguration = alarms },
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

    private static NocturneDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new NocturneDbContext(options) { TenantId = TenantId };
    }

    private static UISettingsService NewService(NocturneDbContext context)
    {
        return new UISettingsService(context, NullLogger<UISettingsService>.Instance);
    }
}
