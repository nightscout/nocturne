using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Health;

/// <summary>
/// <see cref="IStepCountService"/> over <see cref="IObservedHealthSeriesRepository{T}"/>, broadcasting
/// each committed write.
/// </summary>
public class StepCountService(
    IObservedHealthSeriesRepository<StepCount> repository,
    IDocumentProcessingService documentProcessingService,
    ISignalRBroadcastService signalRBroadcastService
) : IStepCountService
{
    private readonly HealthSeriesWriter<StepCount> _series =
        new(repository, documentProcessingService, signalRBroadcastService, "stepcount");

    public async Task<IEnumerable<StepCount>> GetStepCountsAsync(
        int count = 10,
        int skip = 0,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken cancellationToken = default
    ) => await repository.GetNewestFirstAsync(count, skip, from, to, cancellationToken);

    public async Task<IEnumerable<StepCount>> GetStepCountsByDateRangeAsync(
        DateTime from,
        DateTime to,
        int? count = null,
        int skip = 0,
        CancellationToken cancellationToken = default
    ) => await repository.GetByDateRangeAsync(from, to, count, skip, cancellationToken);

    public Task<DateTime?> GetLatestTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    ) => repository.GetLatestTimestampAsync(source, cancellationToken);

    public Task<StepCount?> GetStepCountByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    ) => repository.GetByIdAsync(id, cancellationToken);

    public Task<IEnumerable<StepCount>> CreateStepCountsAsync(
        IEnumerable<StepCount> stepCounts,
        CancellationToken cancellationToken = default
    ) => _series.CreateAsync(stepCounts, cancellationToken);

    public Task<StepCount?> UpdateStepCountAsync(
        string id,
        StepCount stepCount,
        CancellationToken cancellationToken = default
    ) => _series.UpdateAsync(id, stepCount, cancellationToken);

    public Task<bool> DeleteStepCountAsync(
        string id,
        CancellationToken cancellationToken = default
    ) => _series.DeleteAsync(id, cancellationToken);
}
