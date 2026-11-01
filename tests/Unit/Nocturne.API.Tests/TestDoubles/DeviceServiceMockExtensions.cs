using Moq;
using Nocturne.Core.Contracts.Devices;

namespace Nocturne.API.Tests.TestDoubles;

internal static class DeviceServiceMockExtensions
{
    /// <summary>
    /// Answers the batch forms through the per-record ones, so a test sets up and verifies one device
    /// or one link at a time.
    /// </summary>
    public static Mock<IDeviceService> AnswersBatchFormsPerRecord(this Mock<IDeviceService> mock)
    {
        mock
            .Setup(s => s.ResolveManyAsync(It.IsAny<IReadOnlyList<DeviceSighting>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<DeviceSighting> sightings, CancellationToken ct) =>
            {
                var ids = new List<Guid?>(sightings.Count);
                foreach (var s in sightings)
                    ids.Add(await mock.Object.ResolveAsync(s.Category, s.Type, s.Serial, s.Mills, ct));
                return (IReadOnlyList<Guid?>)ids;
            });
        mock
            .Setup(s => s.GetPatientDeviceLinksAsync(It.IsAny<IEnumerable<Guid?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PerRecordLinks(mock));
        mock
            .Setup(s => s.DeferLastSeen(It.IsAny<CancellationToken>()))
            .Returns(Mock.Of<ILastSeenDeferral>());
        return mock;
    }

    private sealed class PerRecordLinks(Mock<IDeviceService> mock) : IPatientDeviceLinks
    {
        public Guid? Resolve(Guid? deviceId, long mills) =>
            mock.Object.ResolvePatientDeviceAsync(deviceId, mills).GetAwaiter().GetResult();
    }
}
