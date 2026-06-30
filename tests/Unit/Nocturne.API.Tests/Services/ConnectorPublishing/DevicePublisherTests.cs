using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

[Trait("Category", "Unit")]
public class DevicePublisherTests
{
    private readonly Mock<IDeviceStatusDecomposer> _mockDecomposer;
    private readonly Mock<IDeviceEventRepository> _mockDeviceEventRepository;
    private readonly Mock<IApsSnapshotRepository> _mockApsSnapshotRepository;
    private readonly Mock<IPumpSnapshotRepository> _mockPumpSnapshotRepository;
    private readonly Mock<IUploaderSnapshotRepository> _mockUploaderSnapshotRepository;
    private readonly DevicePublisher _publisher;

    public DevicePublisherTests()
    {
        _mockDecomposer = new Mock<IDeviceStatusDecomposer>();
        _mockDeviceEventRepository = new Mock<IDeviceEventRepository>();
        _mockApsSnapshotRepository = new Mock<IApsSnapshotRepository>();
        _mockPumpSnapshotRepository = new Mock<IPumpSnapshotRepository>();
        _mockUploaderSnapshotRepository = new Mock<IUploaderSnapshotRepository>();

        _publisher = new DevicePublisher(
            _mockDecomposer.Object,
            _mockDeviceEventRepository.Object,
            Mock.Of<IAuditContext>(),
            _mockApsSnapshotRepository.Object,
            _mockPumpSnapshotRepository.Object,
            _mockUploaderSnapshotRepository.Object,
            NullLogger<DevicePublisher>.Instance
        );
    }

    [Fact]
    public async Task PublishDeviceStatusAsync_DelegatesToDecomposer()
    {
        var statuses = new List<DeviceStatus> { new() };

        var result = await _publisher.PublishDeviceStatusAsync(statuses, "test-source", WriteOrigin.Live);

        result.Should().BeTrue();
        _mockDecomposer.Verify(
            s => s.DecomposeAsync(It.IsAny<DeviceStatus>(), "test-source", WriteOrigin.Live, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PublishDeviceStatusAsync_ReturnsFalse_OnException()
    {
        _mockDecomposer
            .Setup(s => s.DecomposeAsync(It.IsAny<DeviceStatus>(), It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));

        var result = await _publisher.PublishDeviceStatusAsync(new List<DeviceStatus> { new() }, "test-source", WriteOrigin.Live);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetLatestDeviceStatusTimestampAsync_ReturnsMaxAcrossSnapshotRepos_ForSource()
    {
        var t1 = new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 4, 30, 11, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc); // latest

        _mockApsSnapshotRepository
            .Setup(r => r.GetLatestTimestampAsync("src", It.IsAny<CancellationToken>()))
            .ReturnsAsync(t1);
        _mockPumpSnapshotRepository
            .Setup(r => r.GetLatestTimestampAsync("src", It.IsAny<CancellationToken>()))
            .ReturnsAsync(t3);
        _mockUploaderSnapshotRepository
            .Setup(r => r.GetLatestTimestampAsync("src", It.IsAny<CancellationToken>()))
            .ReturnsAsync(t2);

        var result = await _publisher.GetLatestDeviceStatusTimestampAsync("src");

        result.Should().Be(t3);
        // The source is forwarded to all three repos (not APS-only-global as before).
        _mockApsSnapshotRepository.Verify(r => r.GetLatestTimestampAsync("src", It.IsAny<CancellationToken>()), Times.Once);
        _mockPumpSnapshotRepository.Verify(r => r.GetLatestTimestampAsync("src", It.IsAny<CancellationToken>()), Times.Once);
        _mockUploaderSnapshotRepository.Verify(r => r.GetLatestTimestampAsync("src", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetLatestDeviceStatusTimestampAsync_ReturnsNull_WhenAllReposEmpty()
    {
        _mockApsSnapshotRepository
            .Setup(r => r.GetLatestTimestampAsync("src", It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);
        _mockPumpSnapshotRepository
            .Setup(r => r.GetLatestTimestampAsync("src", It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);
        _mockUploaderSnapshotRepository
            .Setup(r => r.GetLatestTimestampAsync("src", It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);

        var result = await _publisher.GetLatestDeviceStatusTimestampAsync("src");

        result.Should().BeNull();
    }
}
