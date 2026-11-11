using System.Runtime.CompilerServices;
using Moq;
using Nocturne.Core.Contracts.Devices;

namespace Nocturne.API.Tests.TestDoubles;

internal static class DeviceServiceMockExtensions
{
    /// <summary>
    /// Answers the batch forms through the per-record resolution and <see cref="Links"/>, so a test sets
    /// up and verifies one device or one link at a time.
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
            .ReturnsAsync(() => mock.Links().Object);
        return mock;
    }

    private static readonly ConditionalWeakTable<Mock<IDeviceService>, Mock<IPatientDeviceLinks>> LinksOf = new();

    /// <summary>The links <see cref="IDeviceService.GetPatientDeviceLinksAsync"/> answers with, set up per device and time.</summary>
    public static Mock<IPatientDeviceLinks> Links(this Mock<IDeviceService> mock) =>
        LinksOf.GetValue(mock, _ => new Mock<IPatientDeviceLinks>());
}
