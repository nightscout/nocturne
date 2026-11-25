using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Connectors;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

public class GlookoConnectorServiceResyncTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_SecondSyncOfTheSameWindow_StoresNoNewRow(bool useV3Api)
    {
        var store = new StoringConnectorPublisher();
        var service = new GlookoConnectorService(
            new HttpClient(new GlookoEndpointHandler(withHistoryMeals: true, withBolusAndGlucose: true)),
            new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
            NullLogger<GlookoConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            new StaticGlookoTokenProvider(),
            store,
            endpointPause: TimeSpan.Zero);
        var request = new SyncRequest
        {
            DataTypes =
            [
                SyncDataType.StateSpans, SyncDataType.TempBasals, SyncDataType.DeviceEvents,
                SyncDataType.CarbIntake, SyncDataType.Food, SyncDataType.Profiles, SyncDataType.Glucose,
                SyncDataType.Boluses,
            ],
            From = DateTime.UtcNow.AddDays(-3),
        };
        var config = GlookoSyncHarness.Config(useV3Api);
        config.V3IncludeCgmBackfill = true;

        (await service.SyncDataAsync(request, config, CancellationToken.None)).Success.Should().BeTrue();
        var stored = store.Stored.Keys.ToList();
        var offered = store.Offered;
        (await service.SyncDataAsync(request, config, CancellationToken.None)).Success.Should().BeTrue();

        stored.Select(k => k.Type).Distinct().Should().Contain(
            [
                nameof(TempBasal), nameof(StateSpan), nameof(CarbIntake), nameof(ProfileActivation),
                nameof(SensorGlucose), nameof(Bolus),
            ]);
        store.Offered.Should().BeEquivalentTo(offered.ToDictionary(o => o.Key, o => o.Value * 2));
        store.Unkeyed.Should().BeEmpty();
        store.Stored.Keys.Should().BeEquivalentTo(stored);
    }
}
