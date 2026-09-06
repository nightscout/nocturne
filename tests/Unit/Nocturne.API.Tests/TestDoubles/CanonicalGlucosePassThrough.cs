using Moq;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// A canonical glucose service that returns its input unchanged — the single-stream behaviour,
/// which is what fixtures not exercising multi-CGM scenarios expect.
/// </summary>
internal static class CanonicalGlucosePassThrough
{
    public static ICanonicalGlucoseService Create()
    {
        var mock = new Mock<ICanonicalGlucoseService>();
        mock.Setup(s => s.SelectAsync(It.IsAny<IReadOnlyList<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SensorGlucose> readings, CancellationToken _) => readings);
        return mock.Object;
    }
}
