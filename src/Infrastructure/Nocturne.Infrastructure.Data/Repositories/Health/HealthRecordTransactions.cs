using Nocturne.Core.Contracts.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.Health;

/// <summary>
/// <see cref="IHealthRecordTransactions"/> over one <see cref="DbUnitOfWork"/> per call, whose
/// repositories run on the unit's context.
/// </summary>
public sealed class HealthRecordTransactions(INocturneDbContextFactory contexts, TimeProvider time)
    : IHealthRecordTransactions
{
    /// <inheritdoc />
    public async Task CommitAsync(
        Func<HealthRecordRepositories, CancellationToken, Task> work, CancellationToken ct = default)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(contexts, ct);
        var repositories = new HealthRecordRepositories(
            new HeartRateRepository(unit, time),
            new StepCountRepository(unit, time),
            new BodyWeightRepository(unit, time),
            new SleepSessionRepository(unit));
        await unit.CommitAsync(token => work(repositories, token), ct);
    }
}
