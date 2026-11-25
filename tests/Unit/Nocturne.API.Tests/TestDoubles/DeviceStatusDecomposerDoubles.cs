using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.TestDoubles;

internal static class DeviceStatusDecomposerDoubles
{
    /// <summary>A decomposer whose every collaborator is a loose double unless the test names it.</summary>
    public static DeviceStatusDecomposer Build(
        IApsSnapshotRepository? aps = null,
        IPumpSnapshotRepository? pump = null,
        IUploaderSnapshotRepository? uploader = null,
        IDeviceStatusExtrasRepository? extras = null,
        ISensorStateReportRepository? sensorState = null,
        IStateSpanService? stateSpans = null,
        IDeviceService? devices = null,
        IAuditContext? audit = null,
        ILoopClosedLoopStamper? closedLoop = null,
        TimeProvider? time = null,
        ILogger<DeviceStatusDecomposer>? logger = null) =>
        new(
            aps ?? Mock.Of<IApsSnapshotRepository>(),
            pump ?? Mock.Of<IPumpSnapshotRepository>(),
            uploader ?? Mock.Of<IUploaderSnapshotRepository>(),
            extras ?? Mock.Of<IDeviceStatusExtrasRepository>(),
            sensorState ?? Mock.Of<ISensorStateReportRepository>(),
            stateSpans ?? Mock.Of<IStateSpanService>(),
            devices ?? new UnresolvedDevices(),
            audit ?? Mock.Of<IAuditContext>(),
            closedLoop ?? Mock.Of<ILoopClosedLoopStamper>(),
            time ?? TimeProvider.System,
            logger ?? NullLogger<DeviceStatusDecomposer>.Instance);

    /// <summary>A decomposer over the real snapshot and extras repositories of <paramref name="contexts"/>.</summary>
    public static DeviceStatusDecomposer Stored(
        INocturneDbContextFactory contexts,
        IAuditContext audit,
        IStateSpanService? stateSpans = null,
        IRecordChangePublisher? uploaderBroadcaster = null) =>
        Build(
            aps: new ApsSnapshotRepository(contexts, audit, NullLogger<ApsSnapshotRepository>.Instance),
            pump: new PumpSnapshotRepository(contexts, audit, NullLogger<PumpSnapshotRepository>.Instance),
            uploader: new UploaderSnapshotRepository(contexts, audit, NullLogger<UploaderSnapshotRepository>.Instance, uploaderBroadcaster),
            extras: new DeviceStatusExtrasRepository(contexts, audit, NullLogger<DeviceStatusExtrasRepository>.Instance),
            sensorState: new SensorStateReportRepository(contexts, audit, NullLogger<SensorStateReportRepository>.Instance),
            stateSpans: stateSpans,
            audit: audit);
}
