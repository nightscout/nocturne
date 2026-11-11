using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Two changes of one settings row, the first held uncommitted until the second is blocked. The
/// second must wait on the first and then merge over what it wrote; without the row lock it merges
/// over the row as it was before the first and writes it back, undoing the first change.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class UISettingsPatchConcurrencyTests : ApiIntegrationTestBase
{
    public UISettingsPatchConcurrencyTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task PatchSectionAsync_DifferentFieldsWhileFirstUncommitted_BothLand()
    {
        await SeedFeaturesAsync();

        await RaceAsync(
            service => service.PatchSectionAsync<FeatureSettings>(
                UISettingsSections.Features, Parse("""{"display":{"focusHours":6}}""")),
            service => service.PatchSectionAsync<FeatureSettings>(
                UISettingsSections.Features, Parse("""{"trackerPills":{"enabled":false}}""")));

        var stored = await StoredAsync<FeatureSettings>(UISettingsSections.Features);
        stored.Display.FocusHours.Should().Be(6);
        stored.TrackerPills.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task PatchSectionAsync_FirstTimePatchesOfDifferentFieldsWhileFirstUncommitted_BothLand()
    {
        await ClearSettingsAsync();

        await RaceAsync(
            service => service.PatchSectionAsync<DataQualitySettings>(
                UISettingsSections.DataQuality, Parse("""{"sleepSchedule":{"bedtimeHour":1}}""")),
            service => service.PatchSectionAsync<DataQualitySettings>(
                UISettingsSections.DataQuality, Parse("""{"compressionLowDetection":{"enabled":false}}""")));

        var stored = await StoredAsync<DataQualitySettings>(UISettingsSections.DataQuality);
        stored.SleepSchedule.BedtimeHour.Should().Be(1);
        stored.CompressionLowDetection.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task AddOrUpdateAlarmProfileAsync_DifferentProfilesWhileFirstUncommitted_BothLand()
    {
        await ClearSettingsAsync();

        await RaceAsync(
            service => service.AddOrUpdateAlarmProfileAsync(new AlarmProfileConfiguration { Id = "night-high" }),
            service => service.AddOrUpdateAlarmProfileAsync(new AlarmProfileConfiguration { Id = "day-low" }));

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var stored = (await Service(new TestNocturneDbContextFactory(db)).GetAlarmConfigurationAsync())!;
        stored.Profiles.Select(p => p.Id).Should().BeEquivalentTo(["night-high", "day-low"]);
    }

    [Fact]
    public async Task NotificationsPatchAfterAnUncommittedAlarmProfile_BothLand()
    {
        await ClearSettingsAsync();

        await RaceAsync(
            service => service.AddOrUpdateAlarmProfileAsync(new AlarmProfileConfiguration { Id = "night-high" }),
            service => service.PatchSectionAsync<NotificationSettings>(
                UISettingsSections.Notifications, Parse("""{"alarmConfiguration":{"globalVolume":10}}""")));

        await AssertProfileAndVolumeBothLandedAsync();
    }

    [Fact]
    public async Task AlarmProfileAfterAnUncommittedNotificationsPatch_BothLand()
    {
        await ClearSettingsAsync();

        await RaceAsync(
            service => service.PatchSectionAsync<NotificationSettings>(
                UISettingsSections.Notifications, Parse("""{"alarmConfiguration":{"globalVolume":10}}""")),
            service => service.AddOrUpdateAlarmProfileAsync(new AlarmProfileConfiguration { Id = "night-high" }));

        await AssertProfileAndVolumeBothLandedAsync();
    }

    private async Task AssertProfileAndVolumeBothLandedAsync()
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var stored = (await Service(new TestNocturneDbContextFactory(db)).GetAlarmConfigurationAsync())!;
        stored.Profiles.Select(p => p.Id).Should().BeEquivalentTo(["night-high"]);
        stored.GlobalVolume.Should().Be(10);
    }

    private async Task RaceAsync(Func<UISettingsService, Task> first, Func<UISettingsService, Task> second)
    {
        await using var seed = Fixture.CreateDbContext(Fixture.TenantId);
        var contexts = new TestNocturneDbContextFactory(seed);
        var held = contexts.CreateUndisposed();
        var racer = contexts.CreateUndisposed();
        Task? secondTask = null;
        try
        {
            await racer.Database.OpenConnectionAsync();
            var racerPid = await racer.Database
                .SqlQuery<int>($"""SELECT pg_backend_pid() AS "Value" """)
                .SingleAsync();

            await held.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await held.Database.BeginTransactionAsync();
                await first(Service(new Pinned(held)));

                secondTask = second(Service(new Pinned(racer)));
                await WaitUntilBlockedOnALockAsync(racerPid, secondTask);

                await transaction.CommitAsync();
            });
            await secondTask!.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (secondTask is not null)
            {
                try
                {
                    await secondTask.WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (Exception ex)
                {
                    Output.WriteLine($"Second change after the race: {ex}");
                }
            }
            await held.Database.CloseConnectionAsync();
            await racer.Database.CloseConnectionAsync();
        }
    }

    private async Task WaitUntilBlockedOnALockAsync(int pid, Task racing)
    {
        await using var probe = Fixture.CreateDbContext(Fixture.TenantId);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (racing.IsCompleted)
                throw new InvalidOperationException("The second change finished while the first was uncommitted", racing.Exception);

            var blocked = await probe.Database
                .SqlQuery<bool>($"""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {pid} AND wait_event_type = 'Lock') AS "Value"
                    """)
                .SingleAsync();
            if (blocked)
                return;

            await Task.Delay(20);
        }

        throw new TimeoutException("The second change never blocked on the first");
    }

    private async Task SeedFeaturesAsync()
    {
        await ClearSettingsAsync();
        await using var seed = Fixture.CreateDbContext(Fixture.TenantId);
        await Service(new TestNocturneDbContextFactory(seed))
            .PatchSectionAsync<FeatureSettings>(UISettingsSections.Features, Parse("{}"));
    }

    private async Task ClearSettingsAsync()
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        await db.Settings.ExecuteDeleteAsync();
    }

    private async Task<T> StoredAsync<T>(string section)
        where T : class
    {
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        return (await Service(new TestNocturneDbContextFactory(db)).GetSectionAsync<T>(section))!;
    }

    private static UISettingsService Service(INocturneDbContextFactory contexts) =>
        new(contexts, TimeProvider.System, NullLogger<UISettingsService>.Instance);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// Hands the service one of the race's contexts on every call. Those come from
    /// <see cref="TestNocturneDbContextFactory.CreateUndisposed"/>, so the service's
    /// <c>await using</c> leaves the held transaction and the racer's connection open.
    /// </summary>
    private sealed class Pinned(NocturneDbContext context) : SingleReaderDbContextFactory
    {
        public override ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => ValueTask.FromResult(context);
    }
}
