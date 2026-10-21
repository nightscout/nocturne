using Nocturne.Core.Contracts.Glucose;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Runs <see cref="CompressionLowDetectionService.DetectForNightAsync(DateOnly, IServiceProvider, CancellationToken)"/>
/// for the scope it is resolved in, as that scope's reader: a history-clamped member reads no older
/// night through it than through any other endpoint.
/// </summary>
public sealed class CompressionLowDetection(IServiceProvider scope, CompressionLowDetectionService detector)
    : ICompressionLowDetectionService
{
    /// <inheritdoc />
    public async Task<int> DetectForNightAsync(DateOnly nightOf, CancellationToken cancellationToken = default)
    {
        await using var job = TenantJobScope.OpenFor(
            scope, TenantReader.Of(scope), CompressionLowDetectionService.AuditEndpoint);

        return await detector.DetectForNightAsync(nightOf, job.ServiceProvider, cancellationToken);
    }
}
