using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// The delete endpoints distinguish "no such source" (404) from "the delete failed" (500) by
/// <see cref="DataSourceDeleteResult.ErrorCode"/>, so the mapping survives any rewording of the
/// human-readable <see cref="DataSourceDeleteResult.Error"/> message.
/// </summary>
[Trait("Category", "Unit")]
public class ServicesControllerDeleteFailureMappingTests
{
    private const string DataSourceId = "ds-8ba3c03d";
    private const string ConnectorId = "dexcom";

    private readonly Mock<IDataSourceService> _dataSourceService = new();

    private ServicesController CreateController() =>
        new(
            _dataSourceService.Object,
            Mock.Of<IConnectorHealthService>(),
            Mock.Of<IConnectorSyncService>(),
            Mock.Of<ILogger<ServicesController>>(),
            Mock.Of<ITenantAccessor>(),
            Options.Create(new BaseDomainOptions()));

    [Fact]
    public async Task DeleteDataSourceData_MissingSource_Returns404_WhateverTheMessageSays()
    {
        _dataSourceService
            .Setup(s => s.DeleteDataSourceDataAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataSourceDeleteResult.Failed(
                DataSourceId,
                DataSourceDeleteError.NotFound,
                "No data source matches that identifier"));

        var response = await CreateController().DeleteDataSourceData(DataSourceId, CancellationToken.None);

        response.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task DeleteDataSourceData_DeleteFailure_Returns500_EvenWhenTheMessageMentionsNotFound()
    {
        _dataSourceService
            .Setup(s => s.DeleteDataSourceDataAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataSourceDeleteResult.Failed(
                DataSourceId,
                DataSourceDeleteError.DeleteFailed,
                "relation \"boluses\" not found"));

        var response = await CreateController().DeleteDataSourceData(DataSourceId, CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task DeleteConnectorData_MissingConnector_Returns404_WhateverTheMessageSays()
    {
        _dataSourceService
            .Setup(s => s.DeleteConnectorDataAsync(ConnectorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataSourceDeleteResult.Failed(
                ConnectorId,
                DataSourceDeleteError.NotFound,
                "No connector matches that identifier"));

        var response = await CreateController().DeleteConnectorData(ConnectorId, CancellationToken.None);

        response.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task DeleteConnectorData_DeleteFailure_Returns500_EvenWhenTheMessageMentionsNotFound()
    {
        _dataSourceService
            .Setup(s => s.DeleteConnectorDataAsync(ConnectorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataSourceDeleteResult.Failed(
                ConnectorId,
                DataSourceDeleteError.DeleteFailed,
                "sync cursor not found while purging"));

        var response = await CreateController().DeleteConnectorData(ConnectorId, CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(500);
    }
}
