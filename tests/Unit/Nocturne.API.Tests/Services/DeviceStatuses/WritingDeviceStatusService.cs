using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.DeviceStatuses;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.DeviceStatuses;

/// <summary>
/// An <see cref="IDeviceStatusService"/> whose writes go through a real <see cref="DeviceStatusDecomposer"/>,
/// for a consumer test that asserts on what was stored. It reads nothing back and announces to no one.
/// </summary>
internal static class WritingDeviceStatusService
{
    public static IDeviceStatusService Over(DeviceStatusDecomposer decomposer) =>
        new DeviceStatusService(
            Mock.Of<IDeviceStatusStore>(),
            decomposer,
            Mock.Of<IWriteSideEffects>(),
            Mock.Of<IDataEventSink<DeviceStatus>>(),
            NullLogger<DeviceStatusService>.Instance);
}
