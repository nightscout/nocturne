using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Devices;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

[Trait("Category", "Unit")]
public class ReservoirReportsControllerTests
{
    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshots = new();
    private readonly Mock<IDeviceEventRepository> _deviceEvents = new();
    private readonly ReservoirReportsController _controller;

    public ReservoirReportsControllerTests()
    {
        _pumpSnapshots
            .Setup(r => r.CreateAsync(It.IsAny<PumpSnapshot>(), WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PumpSnapshot model, WriteOrigin _, CancellationToken _) => model);
        _controller = new ReservoirReportsController(_pumpSnapshots.Object, _deviceEvents.Object);
    }

    [Fact]
    public async Task Reading_StoresManualPumpSnapshot_WithoutDeviceEvent()
    {
        var timestamp = new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero);
        var request = new CreateReservoirReportRequest
        {
            Units = 62,
            Kind = ReservoirReportKind.Reading,
            Timestamp = timestamp,
            Device = "ypsopump",
        };

        var result = await _controller.Create(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var snapshot = Assert.IsType<PumpSnapshot>(ok.Value);
        snapshot.Reservoir.Should().Be(62);
        snapshot.Timestamp.Should().Be(timestamp.UtcDateTime);
        snapshot.Device.Should().Be("ypsopump");
        snapshot.DataSource.Should().Be(DataSources.ManualEntry);
        _deviceEvents.Verify(
            r => r.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Fill_AlsoRecordsReservoirChangeDeviceEvent()
    {
        var timestamp = new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero);
        var request = new CreateReservoirReportRequest
        {
            Units = 85,
            Kind = ReservoirReportKind.Fill,
            Timestamp = timestamp,
        };

        await _controller.Create(request, CancellationToken.None);

        _deviceEvents.Verify(
            r => r.CreateAsync(
                It.Is<DeviceEvent>(e =>
                    e.EventType == DeviceEventType.ReservoirChange
                    && e.Timestamp == timestamp.UtcDateTime
                    && e.DataSource == DataSources.ManualEntry),
                WriteOrigin.Live,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OmittedTimestamp_DefaultsToNow()
    {
        var before = DateTime.UtcNow;
        var request = new CreateReservoirReportRequest { Units = 40, Kind = ReservoirReportKind.Reading };

        var result = await _controller.Create(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var snapshot = Assert.IsType<PumpSnapshot>(ok.Value);
        snapshot.Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }
}
