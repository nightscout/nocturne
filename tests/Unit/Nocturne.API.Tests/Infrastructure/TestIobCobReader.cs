using Moq;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>An <see cref="IIobCobReader"/> over no treatments and no device, for tests that do not read IOB or COB.</summary>
public static class TestIobCobReader
{
    public static IIobCobReader Empty()
    {
        var reader = new Mock<IIobCobReader>();
        reader
            .Setup(r => r.ReadInputsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => NoInputs());
        reader
            .Setup(r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new IobCobReading(new IobResult(), new CobResult(), NoInputs()));
        return reader.Object;
    }

    private static IobCobInputs NoInputs() =>
        new(new List<Bolus>(), new List<TempBasal>(), new List<CarbIntake>());
}
