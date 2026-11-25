using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Tests.TestDoubles;

internal static class EntryDecomposerDoubles
{
    /// <summary>A decomposer whose every collaborator is a loose double unless the test names it.</summary>
    public static EntryDecomposer Build(
        ISensorGlucoseRepository? sensorGlucose = null,
        IMeterGlucoseRepository? meterGlucose = null,
        ICalibrationRepository? calibration = null,
        IGlucoseProcessingResolver? glucoseResolver = null,
        IPatientDeviceStamper? stamper = null,
        IAuditContext? audit = null,
        ILogger<EntryDecomposer>? logger = null) =>
        new(
            sensorGlucose ?? Mock.Of<ISensorGlucoseRepository>(),
            meterGlucose ?? Mock.Of<IMeterGlucoseRepository>(),
            calibration ?? Mock.Of<ICalibrationRepository>(),
            glucoseResolver ?? Mock.Of<IGlucoseProcessingResolver>(),
            stamper ?? Mock.Of<IPatientDeviceStamper>(),
            audit ?? Mock.Of<IAuditContext>(),
            logger ?? NullLogger<EntryDecomposer>.Instance);
}
