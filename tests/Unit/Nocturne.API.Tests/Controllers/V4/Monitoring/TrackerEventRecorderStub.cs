using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>A <see cref="TrackerEventRecorder"/> over mocks that writes to the given repository.</summary>
internal static class TrackerEventRecorderStub
{
    public static TrackerEventRecorder Create(IDeviceEventRepository? deviceEvents = null) =>
        new(
            deviceEvents ?? Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IPatientDeviceRepository>(),
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<ITenantCache>(),
            Mock.Of<ILogger<TrackerEventRecorder>>());
}
