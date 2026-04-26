using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

public interface IGlucoseProcessingResolver
{
    Task ResolveAsync(SensorGlucose model, string? glucoseProcessing, double? smoothedMgdl, double? unsmoothedMgdl, CancellationToken ct = default);
}
