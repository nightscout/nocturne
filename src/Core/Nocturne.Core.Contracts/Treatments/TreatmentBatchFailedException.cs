using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// A treatment in a create batch failed to write, so the batch stopped there and the request fails,
/// as Nightscout 15.0.8's ordered bulk write fails a v1 POST. <see cref="Written"/> holds what the
/// treatments before it wrote, which stays stored and is still announced.
/// </summary>
public sealed class TreatmentBatchFailedException(BulkWrite<Treatment> written, Exception inner)
    : Exception($"A treatment failed to write after {written.Settled.Count} treatment(s) of the batch.", inner)
{
    /// <summary>What the treatments before the failing one wrote.</summary>
    public BulkWrite<Treatment> Written { get; } = written;
}
