using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Tests.Connectors;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// The sync status of a connector is read off the rows stored under its data source id, so the
/// id the endpoint queries must be the one the connector's registration states and its service
/// publishes under. Any other id reads an empty history for a connector that has data.
/// </summary>
public class ServicesControllerSyncStatusTests
{
    public static TheoryData<string, string> RegisteredConnectors()
    {
        var data = new TheoryData<string, string>();
        foreach (var registration in ConnectorInstallers.Types()
                     .Select(t => t.GetCustomAttribute<ConnectorRegistrationAttribute>(inherit: false))
                     .OfType<ConnectorRegistrationAttribute>()
                     .DistinctBy(r => r.ConnectorId))
            data.Add(registration.ConnectorId, registration.DataSourceId);
        return data;
    }

    [Theory]
    [MemberData(nameof(RegisteredConnectors))]
    public async Task GetConnectorSyncStatus_QueriesTheRegisteredDataSource(string connectorId, string dataSourceId)
    {
        var dataSources = new Mock<IDataSourceService>();
        var controller = CreateController(dataSources.Object);

        var result = await controller.GetConnectorSyncStatus(connectorId, CancellationToken.None);

        var status = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ConnectorSyncStatus>().Subject;
        status.DataSource.Should().Be(dataSourceId);
        dataSources.Verify(
            s => s.GetLatestGlucoseTimestampBySourceAsync(dataSourceId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetConnectorSyncStatus_UnknownConnector_Returns404()
    {
        var dataSources = new Mock<IDataSourceService>(MockBehavior.Strict);
        var controller = CreateController(dataSources.Object);

        var result = await controller.GetConnectorSyncStatus("not-a-connector", CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    private static ServicesController CreateController(IDataSourceService dataSources) =>
        new(
            dataSources,
            Mock.Of<IConnectorHealthService>(),
            Mock.Of<IConnectorSyncService>(),
            Mock.Of<ILogger<ServicesController>>(),
            Mock.Of<ITenantAccessor>(),
            Options.Create(new BaseDomainOptions()));
}
