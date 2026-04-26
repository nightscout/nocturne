using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

public interface IGlucoseProcessingConfigProvider
{
    Task<GlucoseProcessing?> GetPreferredProcessingAsync(CancellationToken ct = default);
    Task<List<GlucoseProcessingSourceDefault>> GetSourceDefaultsAsync(CancellationToken ct = default);
}

public class GlucoseProcessingSourceDefault
{
    public required string Match { get; set; }
    public required string Field { get; set; }
    public required GlucoseProcessing Processing { get; set; }
}
