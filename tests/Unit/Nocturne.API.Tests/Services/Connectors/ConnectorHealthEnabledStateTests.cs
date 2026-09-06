using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// The health surface is where an operator checks whether a connector is running. It reads the same
/// <c>Enabled</c> configuration that decides installation and polling, so a switch the other two
/// honour and this one does not reports a connector as live while nothing syncs it.
/// </summary>
public class ConnectorHealthEnabledStateTests
{
    [Theory]
    [InlineData("Parameters:Connectors:Settings:Enabled")]
    [InlineData("Connectors:Settings:Enabled")]
    public async Task DisablingConnectorsGlobally_ReportsEveryConnectorDisabled(string key)
    {
        var statuses = await Service((key, "false")).GetConnectorStatusesAsync();

        statuses.Should().NotBeEmpty();
        statuses.Should().OnlyContain(status => status.IsEnabled == false && status.State == "Disabled");
    }

    /// <summary>
    /// The chain enables as well as disables. A connector the configuration switches on is
    /// installed and polled on that alone, with no database row, so the health surface reports it
    /// enabled on the same evidence rather than as unconfigured.
    /// </summary>
    [Theory]
    [InlineData("Parameters:Connectors:Settings:Enabled")]
    [InlineData("Connectors:Settings:Enabled")]
    public async Task EnablingConnectorsGlobally_ReportsThemEnabledWithNoDatabaseRow(string key)
    {
        var statuses = await Service((key, "true")).GetConnectorStatusesAsync();

        statuses.Should().NotBeEmpty();
        statuses.Should().OnlyContain(status => status.IsEnabled && !status.HasDatabaseConfig);
    }

    /// <summary>
    /// Silence is neither a yes nor a no. A connector with no configuration row and no configured
    /// switch is unconfigured, and the aggregate drops an unconfigured connector that holds no data
    /// — so defaulting the missing switch either way would put every connector on the dashboard.
    /// </summary>
    [Fact]
    public async Task WithNoEnabledSettingAnywhere_NoConnectorIsReportedAtAll()
    {
        (await Service().GetConnectorStatusesAsync()).Should().BeEmpty();
    }

    private static ConnectorHealthService Service(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var dataSources = new Mock<IDataSourceService>();
        dataSources
            .Setup(s => s.GetDataSourceStatsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string dataSource, CancellationToken _) => new DataSourceStats(
                dataSource, 0, 0, null, null, 0, 0, null, null, 0, 0, null, null, [], []));

        var connectorConfig = new Mock<IConnectorConfigurationService>();
        connectorConfig
            .Setup(s => s.GetAllConnectorStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        connectorConfig
            .Setup(s => s.GetConfigurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConnectorConfigurationResponse?)null);

        return new ConnectorHealthService(
            configuration,
            dataSources.Object,
            connectorConfig.Object,
            NullLogger<ConnectorHealthService>.Instance);
    }
}
