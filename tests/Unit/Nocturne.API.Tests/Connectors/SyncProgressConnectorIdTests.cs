using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Services;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// The connector card finds a running sync by the id its entry in the connector list carries, so
/// the id on a connector's progress event has to be that id and not the data source id its records
/// are stored under.
/// </summary>
public class SyncProgressConnectorIdTests
{
    private static readonly string[] ConnectorsWithNoSyncExecutor = ["homeassistant"];

    public static TheoryData<string> ConnectorIds() =>
        [.. SyncExecutors().Select(executor => executor.ConnectorId)];

    [Fact]
    public async Task TheTheoryCoversEveryListedConnectorWithASyncExecutor()
    {
        var listed = (await ConnectorList()).Select(c => c.Id).ToList();
        var covered = SyncExecutors().Select(executor => executor.ConnectorId).ToList();

        covered.Should().BeSubsetOf(listed);
        listed.Except(covered).Should().BeEquivalentTo(ConnectorsWithNoSyncExecutor);
    }

    [Theory]
    [MemberData(nameof(ConnectorIds))]
    public async Task ProgressEvent_CarriesTheIdTheConnectorListCarries(string connectorId)
    {
        var executor = SyncExecutors().Single(e => e.ConnectorId == connectorId);
        var (serviceType, configType) = executor.GetType().GetGenericArguments() switch
        {
            [var service, var config] => (service, config),
            _ => throw new InvalidOperationException(executor.GetType().Name),
        };

        var reported = await RunWithCancelledTokenAsync(serviceType, configType);

        (await ConnectorList()).Select(c => c.Id).Should().Contain(connectorId);
        reported.Should().NotBeEmpty();
        reported.Select(e => e.ConnectorId).Should().OnlyContain(id => id == connectorId);
    }

    private static async Task<List<SyncProgressEvent>> RunWithCancelledTokenAsync(Type serviceType, Type configType)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService(serviceType);
        var config = Activator.CreateInstance(configType)!;

        var reported = new List<SyncProgressEvent>();
        var reporter = new Mock<ISyncProgressReporter>();
        reporter
            .Setup(r => r.ReportProgressAsync(It.IsAny<SyncProgressEvent>(), It.IsAny<CancellationToken>()))
            .Callback<SyncProgressEvent, CancellationToken>((e, _) => reported.Add(e))
            .Returns(Task.CompletedTask);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var sync = typeof(IConnectorService<>).MakeGenericType(configType).GetMethod(
            nameof(IConnectorService<BaseConnectorConfiguration>.SyncDataAsync),
            [typeof(SyncRequest), configType, typeof(CancellationToken), typeof(ISyncProgressReporter)])!;

        var run = (Task<SyncResult>)sync.Invoke(
            service,
            [new SyncRequest { DataTypes = [SyncDataType.Glucose] }, config, cancelled.Token, reporter.Object])!;
        await Task.WhenAny(run);

        return reported;
    }

    private static async Task<List<AvailableConnector>> ConnectorList()
    {
        var configurations = new Mock<IConnectorConfigurationRepository>();
        configurations
            .Setup(c => c.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var service = new DataSourceService(
            Mock.Of<ISourceOwnedDataReader>(),
            Mock.Of<ISourceOwnedDataPurge>(),
            Mock.Of<IDemoDataPurge>(),
            configurations.Object,
            Mock.Of<ISensorGlucoseRepository>(),
            Mock.Of<IMeterGlucoseRepository>(),
            Mock.Of<ICalibrationRepository>(),
            Mock.Of<IAuditContext>(),
            Mock.Of<IConnectorConfigurationService>(),
            null!,
            [],
            [],
            TimeProvider.System,
            Mock.Of<ILogger<DataSourceService>>());

        return await service.GetAvailableConnectorsAsync();
    }

    private static List<IConnectorSyncExecutor> SyncExecutors()
    {
        var services = new ServiceCollection();
        services.AddConnectors(new ConfigurationBuilder().Build());

        return [.. services
            .Where(descriptor => descriptor.ServiceType == typeof(IConnectorSyncExecutor))
            .Select(descriptor => (IConnectorSyncExecutor)Activator.CreateInstance(descriptor.ImplementationType!)!)];
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(TimeProvider.System);
        services.AddBaseConnectorServices();
        services.AddSingleton(Mock.Of<ITenantAccessor>());
        services.AddSingleton(Mock.Of<IConnectorConfigurationService>());
        services.AddSingleton(Mock.Of<IGoogleHealthGrantHolders>());
        services.AddSingleton(Mock.Of<IGoogleHealthPlatformSettings>());
        services.AddSingleton(Mock.Of<IGoogleHealthReadingWriter>());
        services.AddSingleton(Mock.Of<IGoogleHealthSyncCoordinator>());
        services.AddSingleton(Mock.Of<IConnectorSyncCursorStore>());
        services.AddConnectors(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }
}
