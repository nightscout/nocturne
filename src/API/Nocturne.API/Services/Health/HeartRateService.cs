using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Health;

/// <summary>
/// <see cref="IHeartRateService"/> over <see cref="IHeartRateRepository"/>, broadcasting each
/// committed write.
/// </summary>
public class HeartRateService(
    IHeartRateRepository repository,
    IDocumentProcessingService documentProcessingService,
    ISignalRBroadcastService signalRBroadcastService
) : IHeartRateService
{
    private readonly HealthSeriesWriter<HeartRate> _series =
        new(repository, documentProcessingService, signalRBroadcastService, "heartrate");

    public async Task<IEnumerable<HeartRate>> GetHeartRatesAsync(
        int count = 10,
        int skip = 0,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken cancellationToken = default
    ) => await repository.GetNewestFirstAsync(count, skip, from, to, cancellationToken);

    public async Task<IEnumerable<HeartRate>> GetHeartRatesByDateRangeAsync(
        DateTime from,
        DateTime to,
        int? count = null,
        int skip = 0,
        CancellationToken cancellationToken = default
    ) => await repository.GetByDateRangeAsync(from, to, count, skip, cancellationToken);

    public async Task<IEnumerable<HeartRate>> GetHeartRateMinuteAveragesByDateRangeAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default
    ) => await repository.GetMinuteAveragesAsync(from, to, cancellationToken);

    public Task<DateTime?> GetLatestTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    ) => repository.GetLatestTimestampAsync(source, cancellationToken);

    public Task<HeartRate?> GetHeartRateByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    ) => repository.GetByIdAsync(id, cancellationToken);

    public Task<IEnumerable<HeartRate>> CreateHeartRatesAsync(
        IEnumerable<HeartRate> heartRates,
        CancellationToken cancellationToken = default
    ) => _series.CreateAsync(heartRates, cancellationToken);

    public Task<HeartRate?> UpdateHeartRateAsync(
        string id,
        HeartRate heartRate,
        CancellationToken cancellationToken = default
    ) => _series.UpdateAsync(id, heartRate, cancellationToken);

    public Task<bool> DeleteHeartRateAsync(
        string id,
        CancellationToken cancellationToken = default
    ) => _series.DeleteAsync(id, cancellationToken);
}
