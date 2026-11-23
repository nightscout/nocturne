namespace Nocturne.Core.Contracts.Events;

/// <summary>
/// Hands a committed write to every <see cref="IRecordChangeSubscriber"/>, in
/// <see cref="RecordChangeStage"/> order. The first subscriber to throw faults the caller, and the
/// subscribers after it do not run.
/// </summary>
public interface IRecordChangePublisher
{
    Task PublishAsync(RecordsChanged batch, CancellationToken ct);
}
