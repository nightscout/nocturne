using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Entries;

/// <summary>
/// An <see cref="IEntryService"/> whose writes go through a real <see cref="EntryDecomposer"/> into the
/// given repositories, for a consumer test that asserts on what was stored. Its reads return nothing.
/// </summary>
internal static class WritingEntryService
{
    public static IEntryService Over(
        ISensorGlucoseRepository sensorGlucose,
        IMeterGlucoseRepository meterGlucose,
        ICalibrationRepository calibration,
        IAuditContext audit)
    {
        var config = new Mock<IGlucoseProcessingConfigProvider>();
        config.Setup(c => c.GetSourceDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GlucoseProcessingSourceDefault>());
        config.Setup(c => c.GetPreferredProcessingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((GlucoseProcessing?)null);

        var decomposer = new EntryDecomposer(
            sensorGlucose, meterGlucose, calibration, new GlucoseProcessingResolver(config.Object),
            Mock.Of<IPatientDeviceStamper>(), audit, NullLogger<EntryDecomposer>.Instance);

        return new EntryService(
            Mock.Of<IEntryStore>(), decomposer, Mock.Of<IEntryCache>(), NullLogger<EntryService>.Instance);
    }
}
