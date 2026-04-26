using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
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
    private readonly DevicePublisher _publisher;

    public DevicePublisherTests()
    {
        _mockDecomposer = new Mock<IDeviceStatusDecomposer>();
        _mockDeviceEventRepository = new Mock<IDeviceEventRepository>();

        _publisher = new DevicePublisher(
            _mockDecomposer.Object,
            _mockDeviceEventRepository.Object,
            NullLogger<DevicePublisher>.Instance
        );
    }

    [Fact]
    public async Task PublishDeviceStatusAsync_DelegatesToDecomposer()
    {
        var statuses = new List<DeviceStatus> { new() };

        var result = await _publisher.PublishDeviceStatusAsync(statuses, "test-source");

        result.Should().BeTrue();
        _mockDecomposer.Verify(
            s => s.DecomposeAsync(It.IsAny<DeviceStatus>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PublishDeviceStatusAsync_ReturnsFalse_OnException()
    {
        _mockDecomposer
            .Setup(s => s.DecomposeAsync(It.IsAny<DeviceStatus>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));

        var result = await _publisher.PublishDeviceStatusAsync(new List<DeviceStatus> { new() }, "test-source");

        result.Should().BeFalse();
    }
}
