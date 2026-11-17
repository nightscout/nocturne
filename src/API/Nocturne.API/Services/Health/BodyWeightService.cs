using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Health;

/// <summary>
/// <see cref="IBodyWeightService"/> over <see cref="IHealthSeriesRepository{T}"/>, broadcasting each
/// committed write.
/// </summary>
public class BodyWeightService(
    IHealthSeriesRepository<BodyWeight> repository,
    IDocumentProcessingService documentProcessingService,
    ISignalRBroadcastService signalRBroadcastService
) : IBodyWeightService
{
    private readonly HealthSeriesWriter<BodyWeight> _series =
        new(repository, documentProcessingService, signalRBroadcastService, "bodyweight");

    public async Task<IEnumerable<BodyWeight>> GetBodyWeightsAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    ) => await repository.GetNewestFirstAsync(count, skip, cancellationToken);

    public Task<BodyWeight?> GetBodyWeightByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    ) => repository.GetByIdAsync(id, cancellationToken);

    public Task<IEnumerable<BodyWeight>> CreateBodyWeightsAsync(
        IEnumerable<BodyWeight> bodyWeights,
        CancellationToken cancellationToken = default
    ) => _series.CreateAsync(bodyWeights, cancellationToken);

    public Task<BodyWeight?> UpdateBodyWeightAsync(
        string id,
        BodyWeight bodyWeight,
        CancellationToken cancellationToken = default
    ) => _series.UpdateAsync(id, bodyWeight, cancellationToken);

    public Task<bool> DeleteBodyWeightAsync(
        string id,
        CancellationToken cancellationToken = default
    ) => _series.DeleteAsync(id, cancellationToken);
}
