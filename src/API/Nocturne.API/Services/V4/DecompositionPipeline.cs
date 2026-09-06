using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Unified orchestration layer that dispatches decomposition to the appropriate
/// <see cref="IDecomposer{T}"/> for a given record type, and absorbs errors internally
/// (try-catch-log). Callers never need their own try-catch around decomposition.
/// </summary>
/// <remarks>
/// Decomposers are resolved from a child <see cref="IServiceProvider"/> scope per invocation
/// to support scoped service lifetimes. A <see cref="BatchDecompositionResult"/> is returned
/// with per-record success/failure counts so callers can track partial failures.
/// </remarks>
/// <seealso cref="IDecompositionPipeline"/>
/// <seealso cref="EntryDecomposer"/>
/// <seealso cref="TreatmentDecomposer"/>
/// <seealso cref="DeviceStatusDecomposer"/>
/// <seealso cref="ProfileDecomposer"/>
public class DecompositionPipeline : IDecompositionPipeline
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DecompositionPipeline> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DecompositionPipeline"/>.
    /// </summary>
    /// <param name="serviceProvider">The root service provider used to resolve <see cref="IDecomposer{T}"/> instances.</param>
    /// <param name="logger">The logger instance.</param>
    public DecompositionPipeline(IServiceProvider serviceProvider, ILogger<DecompositionPipeline> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<BatchDecompositionResult> DecomposeAsync<T>(IEnumerable<T> records, WriteOrigin origin, CancellationToken ct = default) where T : class
    {
        var result = new BatchDecompositionResult();
        var decomposer = ResolveDecomposer<T>();

        foreach (var record in records)
        {
            try
            {
                var decomposed = await decomposer.DecomposeAsync(record, origin, ct);
                result.Succeeded++;
                result.Results.Add(decomposed);
            }
            catch (Exception ex)
            {
                result.Failed++;
                _logger.LogError(ex, "Failed to decompose {RecordType} into v4 tables", typeof(T).Name);
            }
        }

        return result;
    }

    public async Task<BatchDecompositionResult> DecomposeAsync<T>(T record, WriteOrigin origin, CancellationToken ct = default) where T : class
    {
        var result = new BatchDecompositionResult();
        var decomposer = ResolveDecomposer<T>();

        try
        {
            var decomposed = await decomposer.DecomposeAsync(record, origin, ct);
            result.Succeeded++;
            result.Results.Add(decomposed);
        }
        catch (Exception ex)
        {
            result.Failed++;
            _logger.LogError(ex, "Failed to decompose {RecordType} into v4 tables", typeof(T).Name);
        }

        return result;
    }

    public async Task<int> DeleteByLegacyIdAsync<T>(string legacyId, WriteOrigin origin, CancellationToken ct = default) where T : class
    {
        var decomposer = ResolveDecomposer<T>();

        try
        {
            return await decomposer.DeleteByLegacyIdAsync(legacyId, origin, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete v4 records for legacy {RecordType} {LegacyId}", typeof(T).Name, legacyId);
            return 0;
        }
    }

    private IDecomposer<T> ResolveDecomposer<T>() where T : class
    {
        return _serviceProvider.GetRequiredService<IDecomposer<T>>();
    }
}
