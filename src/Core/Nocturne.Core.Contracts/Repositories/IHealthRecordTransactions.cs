using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>Runs writes to several health record tables so that they commit together or not at all.</summary>
public interface IHealthRecordTransactions
{
    /// <summary>
    /// Runs <paramref name="work"/> on repositories that share one transaction of the scope's reader,
    /// and commits it. A transient failure rolls the attempt back and runs the work again from the start.
    /// </summary>
    Task CommitAsync(Func<HealthRecordRepositories, CancellationToken, Task> work, CancellationToken ct = default);
}

/// <summary>The health record repositories of one <see cref="IHealthRecordTransactions"/> transaction.</summary>
public sealed record HealthRecordRepositories(
    IHeartRateRepository HeartRateRecords,
    IObservedHealthSeriesRepository<StepCount> StepCountRecords,
    IHealthSeriesRepository<BodyWeight> BodyWeightRecords,
    ISleepSessionRepository SleepSessionRecords);
