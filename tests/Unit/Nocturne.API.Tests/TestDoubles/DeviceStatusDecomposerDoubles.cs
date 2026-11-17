using Nocturne.API.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Tests.TestDoubles;

internal static class DeviceStatusDecomposerDoubles
{
    /// <summary>A decomposer for a test that only maps in memory and never reaches a repository.</summary>
    public static DeviceStatusDecomposer Mapping(TimeProvider? time = null) =>
        new(
            Mock.Of<IApsSnapshotRepository>(),
            Mock.Of<IPumpSnapshotRepository>(),
            Mock.Of<IUploaderSnapshotRepository>(),
            Mock.Of<IDeviceStatusExtrasRepository>(),
            Mock.Of<IStateSpanService>(),
            new UnresolvedDevices(),
            Mock.Of<IAuditContext>(),
            Mock.Of<ILoopClosedLoopStamper>(),
            time ?? TimeProvider.System,
            NullLogger<DeviceStatusDecomposer>.Instance);
}
