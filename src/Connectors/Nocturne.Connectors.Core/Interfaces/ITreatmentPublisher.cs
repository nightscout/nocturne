using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Core.Interfaces;

public interface ITreatmentPublisher
{
    Task<bool> PublishTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishBolusesAsync(
        IEnumerable<Bolus> records,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishCarbIntakesAsync(
        IEnumerable<CarbIntake> records,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishBGChecksAsync(
        IEnumerable<BGCheck> records,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishBolusCalculationsAsync(
        IEnumerable<BolusCalculation> records,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishTempBasalsAsync(
        IEnumerable<TempBasal> records,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishDecompositionBatchesAsync(
        IEnumerable<DecompositionBatch> batches,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishBasalInjectionsAsync(
        IEnumerable<BasalInjection> records,
        string source,
        CancellationToken cancellationToken = default);

    Task<DateTime?> GetLatestTreatmentTimestampAsync(
        string source,
        CancellationToken cancellationToken = default);
}
